#nullable enable

using System;
using System.Globalization;
using System.IO;

namespace Conduit
{
    readonly struct RecordOutputPath
    {
        const string OutputDirectoryName = "Recordings";

        RecordOutputPath(string directory, int index, string extension)
        {
            var baseName = index.ToString(CultureInfo.InvariantCulture);
            AbsolutePath = Path.Combine(directory, baseName + extension);
            PartialPath = Path.Combine(directory, baseName + ".partial" + extension);
            IntermediatePath = Path.Combine(directory, baseName + ".recording.mkv");
            PalettePath = Path.Combine(directory, baseName + ".palette.png");
            RelativePath = $"Library/{OutputDirectoryName}/{baseName}{extension}";
        }

        internal string AbsolutePath { get; }
        internal string PartialPath { get; }
        internal string IntermediatePath { get; }
        internal string PalettePath { get; }
        internal string RelativePath { get; }

        internal static RecordOutputPath Allocate(string projectPath, string format)
        {
            var directory = Path.Combine(projectPath, "Library", OutputDirectoryName);
            Directory.CreateDirectory(directory);
            var extension = format switch
            {
                "gif"  => ".gif",
                "webm" => ".webm",
                _      => ".mp4",
            };

            for (var index = FindNextIndex(directory); index < int.MaxValue; ++index)
            {
                var candidate = new RecordOutputPath(directory, index, extension);
                if (IsAvailable(index, directory))
                    return candidate;
            }

            throw new InvalidOperationException("Could not allocate a recording output path.");
        }

        static int FindNextIndex(string directory)
        {
            var nextIndex = 0;
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var fileName = Path.GetFileName(path);
                var suffixOffset = fileName.IndexOf('.');
                if (suffixOffset <= 0
                    || !IsRecordingSuffix(fileName.AsSpan(suffixOffset))
                    || !int.TryParse(
                        fileName.AsSpan(0, suffixOffset),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var index
                    )
                    || index < nextIndex)
                    continue;

                if (index == int.MaxValue)
                    return 0;

                nextIndex = index + 1;
            }

            return nextIndex;
        }

        static bool IsRecordingSuffix(ReadOnlySpan<char> suffix)
            => suffix.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
               || suffix.Equals(".webm", StringComparison.OrdinalIgnoreCase)
               || suffix.Equals(".gif", StringComparison.OrdinalIgnoreCase)
               || suffix.Equals(".partial.mp4", StringComparison.OrdinalIgnoreCase)
               || suffix.Equals(".partial.webm", StringComparison.OrdinalIgnoreCase)
               || suffix.Equals(".partial.gif", StringComparison.OrdinalIgnoreCase)
               || suffix.Equals(".recording.mkv", StringComparison.OrdinalIgnoreCase)
               || suffix.Equals(".palette.png", StringComparison.OrdinalIgnoreCase);

        internal void DeleteTemporaryFiles()
        {
            ConduitFileUtility.TryDelete(PartialPath);
            ConduitFileUtility.TryDelete(IntermediatePath);
            ConduitFileUtility.TryDelete(PalettePath);
        }

        static bool IsAvailable(int index, string directory)
        {
            var baseName = index.ToString(CultureInfo.InvariantCulture);
            return !File.Exists(Path.Combine(directory, baseName + ".mp4"))
                   && !File.Exists(Path.Combine(directory, baseName + ".webm"))
                   && !File.Exists(Path.Combine(directory, baseName + ".gif"))
                   && !File.Exists(Path.Combine(directory, baseName + ".partial.mp4"))
                   && !File.Exists(Path.Combine(directory, baseName + ".partial.webm"))
                   && !File.Exists(Path.Combine(directory, baseName + ".partial.gif"))
                   && !File.Exists(Path.Combine(directory, baseName + ".recording.mkv"))
                   && !File.Exists(Path.Combine(directory, baseName + ".palette.png"));
        }

    }
}
