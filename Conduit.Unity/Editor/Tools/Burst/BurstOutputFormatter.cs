#nullable enable

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Conduit
{
    static class BurstOutputFormatter
    {
        const int LargeOutputLineThreshold = 1000;
        static readonly Regex burstError = new(@"^.*\(\d+,\d+\):\sBurst\serror", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex burstDiagnostic = new(@"(?m)^(?:.*\(\d+,\d+\):\s*)?Burst\s+(?:warning|error)\s+BC\d+\s*:", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex ignoredBurstWarning = new(@"(?m)^(?:.*\(\d+,\d+\):\s*)?Burst\s+warning\s+BC1371\s*:", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly char[] invalidFileNameCharacters = Path.GetInvalidFileNameChars();

        internal static string StripTrailingTemporaryLabelBlocks(string assembly)
        {
            if (assembly.Length > 0
                && assembly[^1] != '\n'
                && assembly.IndexOf('\r') < 0)
            {
                var finalLine = assembly.AsSpan(assembly.LastIndexOf('\n') + 1).Trim();
                if (!finalLine.IsEmpty && finalLine[0] is not ('#' or '/' or ';' or '.'))
                    return assembly;
            }

            var lines = SplitLines(assembly);
            var end = lines.Length - 1;
            while (end >= 0 && string.IsNullOrWhiteSpace(lines[end]))
                end--;

            var start = end;
            var sawLabel = false;
            while (start >= 0 && BurstSymbolFormatter.IsTemporarySuffixLine(lines[start], out var isLabel))
            {
                sawLabel |= isLabel;
                start--;
            }

            if (!sawLabel)
                return Join(lines, end + 1);

            var keep = start + 1;
            while (keep <= end && !BurstSymbolFormatter.IsTemporaryLabel(lines[keep]))
                keep++;

            return Join(lines, keep);
        }

        internal static string CleanDisassembly(string assembly)
        {
            var lines = SplitLines(assembly);
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.EnsureCapacity(assembly.Length);
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                    builder.Append('\n');

                var line = BurstSymbolFormatter.CleanLine(lines[i]);
                var start = 0;
                while (start < line.Length && char.IsWhiteSpace(line[start]))
                    ++start;
                builder.Append(line, start, line.Length - start);
            }

            return builder.ToString();
        }

        internal static bool ShouldSuppressBurstDiagnostic(string message) =>
            !string.IsNullOrEmpty(message) && ignoredBurstWarning.IsMatch(message);

        internal static bool IsBurstDiagnostic(string message) =>
            !string.IsNullOrEmpty(message)
            && (burstDiagnostic.IsMatch(message)
                || message.Contains("InvalidOperationException: Burst failed to compile", StringComparison.Ordinal)
                || message.Contains("BuildFailedException: Burst compiler failed running", StringComparison.Ordinal)
                || message.Contains("Unexpected exception Burst.Compiler.", StringComparison.Ordinal)
                || message.Contains("Burst.Compiler.", StringComparison.Ordinal) && message.Contains("Exception:", StringComparison.Ordinal));

        internal static string SimplifyBurstDiagnostic(string diagnostic)
        {
            var lines = SplitLines(diagnostic);
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.EnsureCapacity(diagnostic.Length);
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                    builder.Append('\n');
                builder.Append(BurstSymbolFormatter.CleanDiagnosticLine(lines[i]));
            }

            return builder.ToString();
        }

        internal static string BuildOutput(BurstTarget target, string disassembly)
            => BuildOutput(target, disassembly, BurstAssemblyAnalyzer.Analyze(target, disassembly, string.Empty));

        internal static string BuildOutput(
            BurstTarget target,
            string disassembly,
            BurstCompilationContext context,
            string optimizationRemarks)
        {
            var stats = BurstAssemblyAnalyzer.Analyze(target, disassembly, context.Cpu);
            stats.Context = context;
            BurstCompilationParser.ParseOptimizationRemarks(optimizationRemarks, stats.OptimizationRemarks);
            return BuildOutput(target, disassembly, stats);
        }

        internal static BridgeCommandResult CompleteOutput(BurstTarget target, string disassembly)
            => CompleteOutput(target, disassembly, BurstAssemblyAnalyzer.Analyze(target, disassembly, string.Empty));

        static string BuildOutput(BurstTarget target, string disassembly, BurstAsmStats stats)
            => BuildOutput(BurstReportFormatter.FormatReport(target, stats), disassembly);

        static string BuildOutput(string report, string disassembly)
            => $"{report}\n\n# Asm\n\n```asm\n{disassembly}\n```";

        internal static string BuildRawOutput(
            BurstTarget target,
            string output,
            BurstOutputTarget outputTarget,
            BurstCompilationContext context = default,
            string optimizationRemarks = "")
        {
            var stats = new BurstAsmStats { Context = context };
            BurstCompilationParser.ParseOptimizationRemarks(optimizationRemarks, stats.OptimizationRemarks);
            return BuildRawOutput(target, output, outputTarget, stats);
        }

        internal static BridgeCommandResult CompleteRawOutput(
            BurstTarget target,
            string output,
            BurstOutputTarget outputTarget)
            => CompleteRawOutput(target, output, outputTarget, new BurstAsmStats());

        internal static BridgeCommandResult CompleteOutput(BurstTarget target, string disassembly, BurstAsmStats stats)
        {
            var report = BurstReportFormatter.FormatReport(target, stats);
            var output = BuildOutput(report, disassembly);
            if (CountLines(output) <= LargeOutputLineThreshold)
                return BridgeCommandResult.Success(output);

            var path = SaveLargeOutput(target, output, ".txt");
            var kilobytes = Math.Max(1, (Encoding.UTF8.GetByteCount(output) + 1023) / 1024);
            return BridgeCommandResult.Success($"{report}\n\n*Assembly output very large ({kilobytes} KB); saved to `{path}`.*");
        }

        static string BuildRawOutput(
            BurstTarget target,
            string output,
            BurstOutputTarget outputTarget,
            BurstAsmStats stats)
        {
            var report = BurstReportFormatter.FormatRawReport(target, outputTarget, stats);
            return BuildRawOutput(report, output, outputTarget);
        }

        static string BuildRawOutput(
            string report,
            string output,
            BurstOutputTarget outputTarget)
        {
            return $"{report}\n\n## {outputTarget.DisplayName}\n\n```{outputTarget.CodeFence}\n{output}\n```";
        }

        internal static BridgeCommandResult CompleteRawOutput(
            BurstTarget target,
            string output,
            BurstOutputTarget outputTarget,
            BurstAsmStats stats)
        {
            var report = BurstReportFormatter.FormatRawReport(target, outputTarget, stats);
            var formatted = BuildRawOutput(report, output, outputTarget);
            if (CountLines(formatted) <= LargeOutputLineThreshold)
                return BridgeCommandResult.Success(formatted);

            var path = SaveLargeOutput(target, output, outputTarget.FileExtension);
            var kilobytes = Math.Max(1, (Encoding.UTF8.GetByteCount(output) + 1023) / 1024);
            return BridgeCommandResult.Success(
                $"{report}\n\n" +
                $"*{outputTarget.DisplayName} output very large ({kilobytes} KB); saved to `{path}`.*"
            );
        }

        static int CountLines(string text)
        {
            if (text.Length == 0)
                return 0;

            var lines = 1;
            foreach (var character in text)
                if (character == '\n')
                    lines++;

            return lines;
        }

        static string SaveLargeOutput(BurstTarget target, string output, string extension)
        {
            var path = Path.Combine("Temp", "Conduit", "Burst", SafeFileName(OutputFileName(target)) + extension);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, output);
            return path.Replace('\\', '/');
        }

        static string OutputFileName(BurstTarget target)
        {
            var name = BurstSymbolFormatter.CleanDisplayName(target.DisplayName);
            TrimAt(" - ");
            TrimAt("(");
            return name.Length > 0 ? name : target.MethodName;

            void TrimAt(string marker)
            {
                var index = name.IndexOf(marker, StringComparison.Ordinal);
                if (index >= 0)
                    name = name[..index].Trim();
            }
        }

        static string SafeFileName(string fileName)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            if (builder.Capacity < fileName.Length)
                builder.Capacity = fileName.Length;
            foreach (var character in fileName)
            {
                if (Array.IndexOf(invalidFileNameCharacters, character) >= 0)
                    builder.Append('_');
                else if (!char.IsWhiteSpace(character))
                    builder.Append(character);
            }

            return builder.Length == 0 ? "burst_asm" : builder.ToString();
        }

        static string Join(string[] lines, int endExclusive)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            for (var i = 0; i < endExclusive; i++)
            {
                if (i > 0)
                    builder.Append('\n');

                builder.Append(lines[i]);
            }

            return builder.ToString();
        }

        internal static string[] SplitLines(string text)
        {
            if (text.IndexOf('\r') < 0)
                return text.Split('\n');

            var lineCount = 1;
            for (var index = 0; index < text.Length; ++index)
            {
                if (text[index] == '\r')
                {
                    ++lineCount;
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                        ++index;
                }
                else if (text[index] == '\n')
                    ++lineCount;
            }

            var lines = new string[lineCount];
            var lineIndex = 0;
            var lineStart = 0;
            for (var index = 0; index < text.Length; ++index)
            {
                if (text[index] is not ('\r' or '\n'))
                    continue;

                lines[lineIndex++] = text.Substring(lineStart, index - lineStart);
                if (text[index] == '\r'
                    && index + 1 < text.Length
                    && text[index + 1] == '\n')
                    ++index;
                lineStart = index + 1;
            }

            lines[lineIndex] = text.Substring(lineStart);
            return lines;
        }

        internal static string FirstLine(string text)
        {
            var carriageReturn = text.IndexOf('\r');
            var lineFeed = text.IndexOf('\n');
            var end = carriageReturn < 0
                ? lineFeed
                : lineFeed < 0
                    ? carriageReturn
                    : Math.Min(carriageReturn, lineFeed);
            return (end < 0 ? text : text[..end]).Trim();
        }

        internal static bool IsBurstError(string disassembly) =>
            disassembly.StartsWith("Failed to compile:", StringComparison.Ordinal)
            || burstError.IsMatch(disassembly);

        internal static string BuildEmptyDisassemblyDiagnostic(BurstTarget target)
            => BuildEmptyOutputDiagnostic(target, "assembly");

        internal static string BuildEmptyOutputDiagnostic(BurstTarget target, string outputName) =>
            $"Failed to compile '{BurstSymbolFormatter.CleanDisplayName(target.DisplayName)}': Burst returned no {outputName.ToLowerInvariant()} or diagnostic text.";
    }
}
