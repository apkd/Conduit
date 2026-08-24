#nullable enable

using System;
using System.Text.RegularExpressions;

namespace Conduit
{
    static class BurstSymbolFormatter
    {
        static readonly Regex tempLabel = new(@"^\s*\.Ltmp\d+:\s*(?:[#;].*)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex sourceLocation = new(@"^(?<prefix>\s*[#;]\s+)(?<file>.+?)\((?<line>\d+),\s*\d+\)(?<rest>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex assemblyQualifier = new(@",\s*[^,\]\)>]+,\s*Version=[0-9.]+,\s*Culture=[^,\]\)>\s]+,\s*PublicKeyToken=(?:null|[0-9a-fA-F]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex fromAssemblyQualifier = new(@"\s+from\s+[^,\]\)>]+,\s*Version=[0-9.]+,\s*Culture=[^,\]\)>\s]+,\s*PublicKeyToken=(?:null|[0-9a-fA-F]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex hashSuffix = new(@"(?<=[A-Za-z0-9_])_[0-9a-fA-F]{32}(?=\b)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex guidId = new(@"(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static bool IsTemporarySuffixLine(string line, out bool isLabel)
        {
            isLabel = tempLabel.IsMatch(line);
            if (isLabel)
                return true;

            var text = line.Trim();
            return text.Length == 0
                   || text.StartsWith("#", StringComparison.Ordinal)
                   || text.StartsWith("//", StringComparison.Ordinal)
                   || text.StartsWith(";", StringComparison.Ordinal)
                   || text.StartsWith(".", StringComparison.Ordinal) && text.IndexOf(':') < 0;
        }

        internal static bool IsTemporaryLabel(string line) => tempLabel.IsMatch(line);

        internal static string CleanLine(string line)
        {
            line = NormalizeSourceLocation(line);

            return LimitGuidIds(CleanQuotedSymbols(line));
        }

        internal static string CleanDisplayName(string displayName) =>
            LimitGuidIds(CleanSymbol(displayName.Trim()));

        internal static string CleanDiagnosticLine(string line)
        {
            line = NormalizeSourceLocation(line);

            line = fromAssemblyQualifier.Replace(line, string.Empty);
            line = assemblyQualifier.Replace(line, string.Empty);
            line = hashSuffix.Replace(line, string.Empty);
            line = BurstSignatureFormatter.FormatRawBurstSignatureParameters(line);
            line = BurstTypeNameFormatter.SimplifyMetadataGenerics(line);
            line = BurstTypeNameFormatter.ReplaceBuiltInTypeNames(line);
            line = BurstTypeNameFormatter.StripCommonLowercaseTypeNamespaces(line);
            line = BurstTypeNameFormatter.StripNamespaces(line);
            line = BurstTypeNameFormatter.ReplaceBuiltInTypeNames(line);
            return LimitGuidIds(line);
        }

        static string NormalizeSourceLocation(string line)
        {
            var trimmed = line.AsSpan().TrimStart();
            if (trimmed.Length == 0
                || trimmed[0] is not ('#' or ';')
                || trimmed.IndexOf('(') < 0)
                return line;

            return sourceLocation.Replace(
                line,
                match => $"{match.Groups["prefix"].Value}{match.Groups["file"].Value}:{match.Groups["line"].Value}{match.Groups["rest"].Value}"
            );
        }

        static string CleanQuotedSymbols(string line)
        {
            var firstQuote = line.IndexOf('"');
            if (firstQuote < 0)
                return line;

            var scanOffset = firstQuote;
            var requiresCleanup = false;
            while (scanOffset < line.Length)
            {
                var start = line.IndexOf('"', scanOffset);
                if (start < 0)
                    break;
                var end = FindClosingQuote(line, start + 1);
                if (end < 0)
                    break;
                if (ShouldCleanSymbol(line.AsSpan(start + 1, end - start - 1)))
                {
                    requiresCleanup = true;
                    break;
                }
                scanOffset = end + 1;
            }
            if (!requiresCleanup)
                return line;

            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.EnsureCapacity(line.Length);
            var offset = 0;
            while (offset < line.Length)
            {
                var start = line.IndexOf('"', offset);
                if (start < 0)
                {
                    builder.Append(line, offset, line.Length - offset);
                    break;
                }

                var end = FindClosingQuote(line, start + 1);
                if (end < 0)
                {
                    builder.Append(line, offset, line.Length - offset);
                    break;
                }

                builder.Append(line, offset, start - offset + 1);
                var symbol = line.Substring(start + 1, end - start - 1);
                builder.Append(ShouldCleanSymbol(symbol.AsSpan()) ? CleanSymbol(symbol) : symbol);
                builder.Append('"');
                offset = end + 1;
            }

            return builder.ToString();
        }

        static int FindClosingQuote(string text, int start)
        {
            for (var i = start; i < text.Length; i++)
            {
                if (text[i] == '\\')
                {
                    i++;
                    continue;
                }

                if (text[i] == '"')
                    return i;
            }

            return -1;
        }

        static bool ShouldCleanSymbol(ReadOnlySpan<char> symbol) =>
            symbol.IndexOf("Version=".AsSpan(), StringComparison.Ordinal) >= 0
            || symbol.IndexOf("PublicKeyToken=".AsSpan(), StringComparison.Ordinal) >= 0
            || symbol.IndexOf(" -> ".AsSpan(), StringComparison.Ordinal) >= 0
            || symbol.IndexOf('`') >= 0
            || symbol.IndexOf("System.".AsSpan(), StringComparison.Ordinal) >= 0;

        static string CleanSymbol(string symbol)
        {
            symbol = RemoveBurstLabelSuffix(symbol);
            symbol = assemblyQualifier.Replace(symbol, string.Empty);
            symbol = BurstTypeNameFormatter.SimplifyMetadataGenerics(symbol);
            symbol = BurstTypeNameFormatter.ReplaceBuiltInTypeNames(symbol);
            symbol = BurstTypeNameFormatter.StripCommonLowercaseTypeNamespaces(symbol);
            symbol = BurstTypeNameFormatter.StripNamespaces(symbol);
            symbol = BurstTypeNameFormatter.ReplaceBuiltInTypeNames(symbol);
            return symbol;
        }

        static string RemoveBurstLabelSuffix(string symbol)
        {
            var fromIndex = symbol.LastIndexOf(" from ", StringComparison.Ordinal);
            if (fromIndex < 0)
                return StripHashSuffix(symbol);

            var signature = StripHashSuffix(symbol[..fromIndex]);
            var suffix = symbol[(fromIndex + " from ".Length)..];
            var stringLabelIndex = suffix.IndexOf(".string.IL_", StringComparison.Ordinal);
            return stringLabelIndex < 0
                ? signature
                : signature + suffix[stringLabelIndex..];
        }

        static string StripHashSuffix(string text)
        {
            var underscore = text.LastIndexOf('_');
            if (underscore < 0 || text.Length - underscore != 33)
                return text;

            for (var i = underscore + 1; i < text.Length; i++)
                if (!IsHex(text[i]))
                    return text;

            return text[..underscore];
        }

        internal static bool IsHex(char character) =>
            character is >= '0' and <= '9'
            || character is >= 'a' and <= 'f'
            || character is >= 'A' and <= 'F';

        static string LimitGuidIds(string line)
        {
            if (line.Length < 32 || !ContainsGuidId(line))
                return line;

            return guidId.Replace(line, match => match.Value[..8]);
        }

        static bool ContainsGuidId(string line)
        {
            for (var index = 0; index < line.Length;)
            {
                if (!IsHex(line[index]))
                {
                    index++;
                    continue;
                }

                var start = index++;
                while (index < line.Length && IsHex(line[index]))
                    index++;
                if (index - start == 32)
                    return true;
            }

            return false;
        }

    }
}
