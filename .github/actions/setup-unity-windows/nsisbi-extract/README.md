# Unity NSISBI extractor

Unity's Windows Editor executable uses the NSISBI fork of NSIS. Its payload is one solid stream
split into independently compressed LZMA chunks. This utility reconstructs the install paths from
the compiled NSIS commands, decodes small chunk batches in parallel, and writes selected files in
stream order without materializing the multi-gigabyte uncompressed stream.

The implementation was adapted from
[`kmod-midori/unity-nsisbi-ext`](https://github.com/kmod-midori/unity-nsisbi-ext/tree/fdaf2d6dd8126c4964104a582b64825f08a88cf5)
and rewritten for Unity's current chunk framing and solid-data layout. The original MIT license is
included in [LICENSE](LICENSE).
