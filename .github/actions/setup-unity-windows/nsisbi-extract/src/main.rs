use std::{
    collections::BTreeMap,
    env,
    ffi::OsString,
    fs::File,
    io::{self, Read, Write},
    path::{Component, Path, PathBuf},
    sync::{
        mpsc::{sync_channel, Receiver},
        Arc,
    },
};

use anyhow::{bail, Context, Result};
use lzma_rust2::LzmaReader;
use memmap2::{Mmap, MmapOptions};
use rayon::prelude::*;

const NSIS_HEADER: &[u8] = b"NullsoftInst";
const NSISBI_FLAG_MASK: u32 = 0x30;
const NSIS_HEADER_PREFIX_SIZE: usize = 36;
const NSIS_BLOCK_COUNT: usize = 7;
const NSIS_ENTRY_SIZE: usize = 36;
const EXTRACT_FILE_COMMAND: u32 = 20;
const CREATE_DIRECTORY_COMMAND: u32 = 11;
const ASSIGN_VARIABLE_COMMAND: u32 = 27;

struct Args {
    installer: PathBuf,
    output: PathBuf,
    include_prefixes: Vec<String>,
    force_include_paths: Vec<String>,
    exclude_components: Vec<String>,
    exclude_prefixes: Vec<String>,
    exclude_suffixes: Vec<String>,
    threads: Option<usize>,
}

impl Args {
    fn parse() -> Result<Self> {
        let mut arguments = env::args_os().skip(1);
        let installer = PathBuf::from(
            arguments
                .next()
                .context("usage: nsisbi-extract <installer> <output> [options]")?,
        );
        let output = PathBuf::from(
            arguments
                .next()
                .context("usage: nsisbi-extract <installer> <output> [options]")?,
        );
        let mut result = Self {
            installer,
            output,
            include_prefixes: Vec::new(),
            force_include_paths: Vec::new(),
            exclude_components: Vec::new(),
            exclude_prefixes: Vec::new(),
            exclude_suffixes: Vec::new(),
            threads: None,
        };

        while let Some(option) = arguments.next() {
            let option = option
                .to_str()
                .with_context(|| format!("invalid option: {}", option.to_string_lossy()))?;
            match option {
                "--include-prefix" => result.include_prefixes.push(next_filter(&mut arguments)?),
                "--force-include" => result
                    .force_include_paths
                    .push(next_filter(&mut arguments)?),
                "--exclude-component" => {
                    result.exclude_components.push(next_filter(&mut arguments)?)
                }
                "--exclude-prefix" => result.exclude_prefixes.push(next_filter(&mut arguments)?),
                "--exclude-suffix" => result.exclude_suffixes.push(next_filter(&mut arguments)?),
                "--threads" => {
                    let value = arguments.next().context("--threads requires a value")?;
                    let value = value.to_string_lossy().parse::<usize>()?;
                    if value == 0 {
                        bail!("--threads must be greater than zero");
                    }
                    result.threads = Some(value);
                }
                _ => bail!("unknown option: {option}"),
            }
        }

        for filters in [
            &mut result.include_prefixes,
            &mut result.force_include_paths,
            &mut result.exclude_components,
            &mut result.exclude_prefixes,
            &mut result.exclude_suffixes,
        ] {
            for filter in filters {
                *filter = filter
                    .replace('\\', "/")
                    .trim_matches('/')
                    .to_ascii_lowercase();
            }
        }
        return Ok(result);

        fn next_filter(arguments: &mut impl Iterator<Item = OsString>) -> Result<String> {
            arguments
                .next()
                .context("filter option requires a value")?
                .into_string()
                .map_err(|value| anyhow::anyhow!("invalid filter: {}", value.to_string_lossy()))
        }
    }

    fn includes(&self, path: &Path) -> bool {
        let path = path
            .to_string_lossy()
            .replace('\\', "/")
            .to_ascii_lowercase();
        self.force_include_paths.contains(&path)
            || ((self.include_prefixes.is_empty()
                || self
                    .include_prefixes
                    .iter()
                    .any(|prefix| has_path_prefix(&path, prefix)))
                && !self
                    .exclude_components
                    .iter()
                    .any(|component| path.split('/').any(|part| part == component))
                && !self
                    .exclude_prefixes
                    .iter()
                    .any(|prefix| has_path_prefix(&path, prefix))
                && !self
                    .exclude_suffixes
                    .iter()
                    .any(|suffix| path.ends_with(suffix)))
    }
}

fn has_path_prefix(path: &str, prefix: &str) -> bool {
    path == prefix
        || path
            .strip_prefix(prefix)
            .is_some_and(|remainder| remainder.starts_with('/'))
}

fn read_u16(data: &[u8], offset: usize) -> Result<u16> {
    Ok(u16::from_le_bytes(
        data.get(offset..offset + 2)
            .context("unexpected end of NSIS data")?
            .try_into()?,
    ))
}

fn read_u32(data: &[u8], offset: usize) -> Result<u32> {
    Ok(u32::from_le_bytes(
        data.get(offset..offset + 4)
            .context("unexpected end of NSIS data")?
            .try_into()?,
    ))
}

fn read_u64(data: &[u8], offset: usize) -> Result<u64> {
    Ok(u64::from_le_bytes(
        data.get(offset..offset + 8)
            .context("unexpected end of NSIS data")?
            .try_into()?,
    ))
}

fn read_string(block: &[u8], unicode: bool, position: u32) -> Result<String> {
    if !unicode {
        let bytes = block
            .get(position as usize..)
            .context("string offset exceeds the strings block")?;
        let end = bytes
            .iter()
            .position(|byte| *byte == 0)
            .unwrap_or(bytes.len());
        return Ok(String::from_utf8(bytes[..end].to_vec())?);
    }

    let mut offset = position as usize * 2;
    let mut result = Vec::new();
    for _ in 0..0xffff {
        let character = read_u16(block, offset)?;
        offset += 2;
        match character {
            0 => break,
            // NSIS shell and language codes carry one additional encoded word.
            0x01 | 0x02 => offset += 2,
            0x03 => {
                let encoded = read_u16(block, offset)?;
                offset += 2;
                let index = ((encoded >> 8 & 0x7f) << 7) | (encoded & 0x7f);
                let variable = match index {
                    21 => "$INSTDIR".to_string(),
                    22 => "$OUTDIR".to_string(),
                    31 => "$_OUTDIR".to_string(),
                    26 => "$PLUGINSDIR".to_string(),
                    _ => format!("$VAR{index}"),
                };
                result.extend(variable.encode_utf16());
            }
            // An escaped character follows this code literally.
            0x04 => {
                result.push(read_u16(block, offset)?);
                offset += 2;
            }
            _ => result.push(character),
        }
    }
    Ok(String::from_utf16(&result)?)
}

fn resolve_path(path: &str, current: &Path, saved: &Path) -> PathBuf {
    let normalized = path.replace('\\', "/");
    match normalized.as_str() {
        "$INSTDIR" => PathBuf::new(),
        "$OUTDIR" => current.to_path_buf(),
        "$_OUTDIR" => saved.to_path_buf(),
        _ => normalized
            .strip_prefix("$INSTDIR/")
            .map(PathBuf::from)
            .or_else(|| {
                normalized
                    .strip_prefix("$OUTDIR/")
                    .map(|path| current.join(path))
            })
            .or_else(|| {
                normalized
                    .strip_prefix("$_OUTDIR/")
                    .map(|path| saved.join(path))
            })
            .unwrap_or_else(|| PathBuf::from(normalized.trim_start_matches('/'))),
    }
}

fn safe_relative_path(path: PathBuf) -> Option<PathBuf> {
    if path.components().any(|component| {
        matches!(
            component,
            Component::ParentDir | Component::RootDir | Component::Prefix(_)
        )
    }) {
        return None;
    }
    if path.components().any(|component| {
        let component = component.as_os_str().to_string_lossy();
        component.starts_with('$') && component != "$PLUGINSDIR"
    }) {
        return None;
    }
    Some(path)
}

fn decode_chunk(input: &[u8]) -> io::Result<Vec<u8>> {
    if input.len() < 5 {
        return Err(invalid_data("invalid NSISBI LZMA chunk"));
    }
    let properties = input[0];
    let dictionary = u32::from_le_bytes(input[1..5].try_into().unwrap());
    let mut output = Vec::with_capacity(2 * 1024 * 1024);
    LzmaReader::new_with_props(&input[5..], u64::MAX, properties, dictionary, None)
        .map_err(invalid_data)?
        .read_to_end(&mut output)?;
    Ok(output)
}

// Unity's NSIS fork splits one solid data stream into independent LZMA chunks.
// Decode one batch ahead so output and filtering overlap the next batch's CPU work.
struct SolidReader {
    input: Arc<Mmap>,
    compressed_position: usize,
    decoded: Vec<Vec<u8>>,
    decoded_chunk: usize,
    decoded_position: usize,
    logical_position: u64,
    pool: Arc<rayon::ThreadPool>,
    pending: Option<Receiver<io::Result<Vec<Vec<u8>>>>>,
}

impl SolidReader {
    fn new(input: Arc<Mmap>, compressed_position: usize, pool: Arc<rayon::ThreadPool>) -> Self {
        let mut reader = Self {
            input,
            compressed_position,
            decoded: Vec::new(),
            decoded_chunk: 0,
            decoded_position: 0,
            logical_position: 0,
            pool,
            pending: None,
        };
        reader.schedule_batch();
        reader
    }

    fn schedule_batch(&mut self) -> bool {
        let mut chunks = Vec::with_capacity(self.pool.current_num_threads() * 2);
        while chunks.len() < chunks.capacity() {
            let Some(length) = self
                .input
                .get(self.compressed_position..self.compressed_position.saturating_add(3))
            else {
                break;
            };
            let length = u32::from_le_bytes([length[0], length[1], length[2], 0]) as usize;
            let start = self.compressed_position + 3;
            let end = start.saturating_add(length);
            if self.input.get(start..end).is_none() {
                break;
            }
            if length < 5 {
                break;
            }
            chunks.push((start, end));
            self.compressed_position = end;
        }
        if chunks.is_empty() {
            return false;
        }

        let input = Arc::clone(&self.input);
        let (sender, receiver) = sync_channel(1);
        self.pool.spawn_fifo(move || {
            let decoded = chunks
                .par_iter()
                .map(|&(start, end)| decode_chunk(&input[start..end]))
                .collect::<io::Result<Vec<_>>>();
            let _ = sender.send(decoded);
        });
        self.pending = Some(receiver);
        true
    }

    fn load_batch(&mut self) -> io::Result<bool> {
        let Some(receiver) = self.pending.take() else {
            return Ok(false);
        };
        self.decoded = receiver
            .recv()
            .map_err(|_| invalid_data("NSISBI decoder stopped unexpectedly"))??;
        self.decoded_chunk = 0;
        self.decoded_position = 0;
        self.schedule_batch();
        Ok(true)
    }

    fn ensure_data(&mut self) -> io::Result<bool> {
        while self.decoded_chunk == self.decoded.len()
            || self.decoded_position == self.decoded[self.decoded_chunk].len()
        {
            if self.decoded_chunk + 1 < self.decoded.len() {
                self.decoded_chunk += 1;
                self.decoded_position = 0;
            } else if !self.load_batch()? {
                return Ok(false);
            }
        }
        Ok(true)
    }

    fn skip(&mut self, mut count: u64) -> io::Result<bool> {
        while count != 0 {
            if !self.ensure_data()? {
                return Ok(false);
            }
            let available = self.decoded[self.decoded_chunk].len() - self.decoded_position;
            let skipped = count.min(available as u64) as usize;
            self.decoded_position += skipped;
            self.logical_position += skipped as u64;
            count -= skipped as u64;
        }
        Ok(true)
    }
}

impl Read for SolidReader {
    fn read(&mut self, output: &mut [u8]) -> io::Result<usize> {
        if output.is_empty() || !self.ensure_data()? {
            return Ok(0);
        }
        let available = self.decoded[self.decoded_chunk].len() - self.decoded_position;
        let count = output.len().min(available);
        output[..count].copy_from_slice(
            &self.decoded[self.decoded_chunk][self.decoded_position..self.decoded_position + count],
        );
        self.decoded_position += count;
        self.logical_position += count as u64;
        Ok(count)
    }
}

fn invalid_data(error: impl ToString) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, error.to_string())
}

fn main() -> Result<()> {
    let args = Args::parse()?;
    let pool = Arc::new(
        rayon::ThreadPoolBuilder::new()
            .num_threads(args.threads.unwrap_or_else(|| {
                std::thread::available_parallelism()
                    .map(usize::from)
                    .unwrap_or(1)
            }))
            .build()?,
    );
    extract(&args, pool)
}

fn extract(args: &Args, pool: Arc<rayon::ThreadPool>) -> Result<()> {
    let file = File::open(&args.installer)?;
    let mmap = Arc::new(unsafe { MmapOptions::new().map(&file)? });
    let (header, data_offset) = read_header(&mmap)?;
    let files = collect_files(&header, args)?;
    let file_count = files.values().map(Vec::len).sum::<usize>();

    mmap.get(data_offset..)
        .context("missing NSISBI data block")?;
    let mut reader = SolidReader::new(mmap, data_offset, pool);
    let mut extracted_bytes = 0u64;
    let mut buffer = vec![0; 1024 * 1024];
    for (offset, paths) in files {
        let gap = offset
            .checked_sub(reader.logical_position)
            .context("overlapping NSISBI data entries")?;
        if !reader.skip(gap)? {
            bail!("NSISBI data ended before offset {offset}");
        }

        let mut encoded_size = [0; 8];
        reader.read_exact(&mut encoded_size)?;
        let encoded_size = u64::from_le_bytes(encoded_size);
        if encoded_size & (1 << 63) != 0 {
            bail!("nested compression is unsupported at data offset {offset}");
        }
        let size = encoded_size & !(1 << 63);

        let mut outputs = Vec::with_capacity(paths.len());
        for path in &paths {
            let output = args.output.join(path);
            if let Some(parent) = output.parent() {
                std::fs::create_dir_all(parent)?;
            }
            outputs.push(File::create(output)?);
        }

        let mut remaining = size;
        while remaining != 0 {
            let requested = remaining.min(buffer.len() as u64) as usize;
            let count = reader.read(&mut buffer[..requested])?;
            if count == 0 {
                bail!("NSISBI data ended while extracting {}", paths[0].display());
            }
            for output in &mut outputs {
                output.write_all(&buffer[..count])?;
            }
            remaining -= count as u64;
        }
        extracted_bytes += size * paths.len() as u64;
    }

    println!(
        "Extracted {file_count} files ({:.2} GiB)",
        extracted_bytes as f64 / 1024f64.powi(3)
    );
    Ok(())
}

fn read_header(installer: &Mmap) -> Result<(Vec<u8>, usize)> {
    let signature_offset = installer
        .windows(NSIS_HEADER.len())
        .position(|window| window == NSIS_HEADER)
        .context("NSIS header not found")?;
    let header_offset = signature_offset
        .checked_sub(8)
        .context("invalid NSIS header offset")?;
    let flags = read_u32(installer, header_offset)?;
    if flags & NSISBI_FLAG_MASK == 0 {
        bail!("installer does not use Unity's NSISBI format");
    }
    let header_size = read_u32(installer, header_offset + 20)? as usize;

    // NSISBI adds an eight-byte data size, then frames both solid streams with u24 lengths.
    let mut position = header_offset + NSIS_HEADER_PREFIX_SIZE;
    let mut header = Vec::with_capacity(header_size + 8);
    while header.len() < header_size + 8 {
        let length = installer
            .get(position..position + 3)
            .context("truncated NSISBI header chunk length")?;
        let length = u32::from_le_bytes([length[0], length[1], length[2], 0]) as usize;
        position += 3;
        let chunk = installer
            .get(position..position + length)
            .context("truncated NSISBI header chunk")?;
        header.extend(decode_chunk(chunk)?);
        position += length;
    }
    if header.len() != header_size + 8 {
        bail!("NSISBI header has an unexpected decompressed size");
    }
    header.copy_within(8.., 0);
    header.truncate(header_size);
    Ok((header, position))
}

fn collect_files(header: &[u8], args: &Args) -> Result<BTreeMap<u64, Vec<PathBuf>>> {
    let mut blocks = [(0u32, 0u32); NSIS_BLOCK_COUNT];
    for (index, block) in blocks.iter_mut().enumerate() {
        let offset = 4 + index * 8;
        *block = (read_u32(header, offset)?, read_u32(header, offset + 4)?);
    }
    let (entries_offset, entries_count) = blocks[2];
    let (strings_offset, _) = blocks[3];
    let strings = header
        .get(strings_offset as usize..)
        .context("strings block exceeds the NSIS header")?;
    let unicode = read_u16(strings, 0)? == 0;

    let mut current_directory = PathBuf::new();
    let mut saved_directory = PathBuf::new();
    let mut files = BTreeMap::<u64, Vec<PathBuf>>::new();
    for index in 0..entries_count as usize {
        let entry = entries_offset as usize + index * NSIS_ENTRY_SIZE;
        let command = read_u32(header, entry)?;
        match command {
            CREATE_DIRECTORY_COMMAND => {
                let path = read_string(strings, unicode, read_u32(header, entry + 4)?)?;
                current_directory = resolve_path(&path, &current_directory, &saved_directory);
            }
            EXTRACT_FILE_COMMAND => {
                let name = read_string(strings, unicode, read_u32(header, entry + 8)?)?;
                let relative = if name.starts_with('$') {
                    resolve_path(&name, &current_directory, &saved_directory)
                } else {
                    current_directory.join(resolve_path(
                        &name,
                        &current_directory,
                        &saved_directory,
                    ))
                };
                let Some(relative) = safe_relative_path(relative) else {
                    continue;
                };
                if !args.includes(&relative) {
                    continue;
                }
                let offset = read_u64(header, entry + 12)?;
                let paths = files.entry(offset).or_default();
                if !paths.contains(&relative) {
                    paths.push(relative);
                }
            }
            ASSIGN_VARIABLE_COMMAND if read_u32(header, entry + 4)? == 31 => {
                let value = read_string(strings, unicode, read_u32(header, entry + 8)?)?;
                saved_directory = resolve_path(&value, &current_directory, &saved_directory);
            }
            _ => {}
        }
    }
    Ok(files)
}
