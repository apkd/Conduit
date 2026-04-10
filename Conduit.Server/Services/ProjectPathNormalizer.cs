namespace Conduit;

public static class ProjectPathNormalizer
{
    public static string Normalize(string? path)
    {
        if (path is null)
            return string.Empty;

        var normalized = TrimWhiteSpace(path.AsSpan());
        normalized = Trim(normalized, '"');
        normalized = Trim(normalized, '\'');

        if (normalized.IsEmpty || IsWhiteSpace(normalized))
            return string.Empty;

        var normalizedPath = CreatePathString(path, normalized);
        normalizedPath = NormalizePathString(normalizedPath);
        return TrimEndingDirectorySeparators(normalizedPath);

        static string NormalizePathString(string value)
        {
            var separatorNormalized = ReplaceDirectorySeparators(value);
            if (TryNormalizeWindowsDrivePath(separatorNormalized, out var windowsDrivePath))
                return windowsDrivePath;

            if (TryNormalizeWslUncPath(separatorNormalized, out var wslUncPath))
                return wslUncPath;

            if (separatorNormalized.Length > 0 && separatorNormalized[0] == Path.AltDirectorySeparatorChar)
                return separatorNormalized;

            try
            {
                var fullPath = ReplaceDirectorySeparators(Path.GetFullPath(value));
                if (TryNormalizeWindowsDrivePath(fullPath, out windowsDrivePath))
                    return windowsDrivePath;

                if (TryNormalizeWslUncPath(fullPath, out wslUncPath))
                    return wslUncPath;

                return fullPath;
            }
            catch (Exception)
            {
                return separatorNormalized;
            }
        }

        static ReadOnlySpan<char> TrimWhiteSpace(ReadOnlySpan<char> value)
        {
            var start = 0;
            var end = value.Length - 1;

            while (start <= end && char.IsWhiteSpace(value[start]))
                start++;

            while (end >= start && char.IsWhiteSpace(value[end]))
                end--;

            return value[start..(end + 1)];
        }

        static ReadOnlySpan<char> Trim(ReadOnlySpan<char> value, char trimChar)
        {
            var start = 0;
            var end = value.Length - 1;

            while (start <= end && value[start] == trimChar)
                start++;

            while (end >= start && value[end] == trimChar)
                end--;

            return value[start..(end + 1)];
        }

        static bool IsWhiteSpace(ReadOnlySpan<char> value)
        {
            foreach (var character in value)
                if (!char.IsWhiteSpace(character))
                    return false;

            return true;
        }

        static string CreatePathString(string original, ReadOnlySpan<char> value)
        {
            if (Path.DirectorySeparatorChar == Path.AltDirectorySeparatorChar
                || value.IndexOf(Path.AltDirectorySeparatorChar) < 0)
                return value.Length == original.Length ? original : value.ToString();

            return string.Create(
                value.Length,
                value,
                static (buffer, source) =>
                {
                    for (var index = 0; index < source.Length; index++)
                        buffer[index] = source[index] == Path.AltDirectorySeparatorChar
                            ? Path.DirectorySeparatorChar
                            : source[index];
                }
            );
        }

        static string ReplaceDirectorySeparators(string value)
        {
            if (value.IndexOf('\\') < 0)
                return value;

            return string.Create(
                value.Length,
                value,
                static (buffer, source) =>
                {
                    for (var index = 0; index < source.Length; index++)
                        buffer[index] = source[index] == '\\' ? Path.AltDirectorySeparatorChar : source[index];
                }
            );
        }

        static bool TryNormalizeWindowsDrivePath(string value, out string normalizedPath)
        {
            normalizedPath = string.Empty;
            if (value.Length < 2 || value[1] != ':' || !char.IsAsciiLetter(value[0]))
                return false;

            var remainder = value.AsSpan(2);
            if (!remainder.IsEmpty && remainder[0] != Path.AltDirectorySeparatorChar)
                return false;

            remainder = TrimLeadingDirectorySeparators(remainder);
            if (remainder.StartsWith("mnt", StringComparison.OrdinalIgnoreCase)
                && (remainder.Length == 3 || remainder[3] == Path.AltDirectorySeparatorChar))
            {
                normalizedPath = "/" + remainder.ToString();
                return true;
            }

            normalizedPath = remainder.IsEmpty
                ? $"/mnt/{char.ToLowerInvariant(value[0])}"
                : $"/mnt/{char.ToLowerInvariant(value[0])}/{remainder}";

            return true;
        }

        static bool TryNormalizeWslUncPath(string value, out string normalizedPath)
        {
            const string localhostPrefix = "//wsl.localhost/";
            const string shortPrefix = "//wsl$/";

            normalizedPath = string.Empty;

            var prefixLength = value.StartsWith(localhostPrefix, StringComparison.OrdinalIgnoreCase)
                ? localhostPrefix.Length
                : value.StartsWith(shortPrefix, StringComparison.OrdinalIgnoreCase)
                    ? shortPrefix.Length
                    : 0;

            if (prefixLength == 0 || value.Length <= prefixLength)
                return false;

            var remainder = value.AsSpan(prefixLength);
            var distroSeparatorIndex = remainder.IndexOf(Path.AltDirectorySeparatorChar);
            if (distroSeparatorIndex < 0 || distroSeparatorIndex == remainder.Length - 1)
                return false;

            remainder = TrimLeadingDirectorySeparators(remainder[(distroSeparatorIndex + 1)..]);
            normalizedPath = remainder.IsEmpty ? "/" : "/" + remainder.ToString();
            return true;
        }

        static ReadOnlySpan<char> TrimLeadingDirectorySeparators(ReadOnlySpan<char> value)
        {
            var start = 0;
            while (start < value.Length && value[start] == Path.AltDirectorySeparatorChar)
                start++;

            return value[start..];
        }

        static string TrimEndingDirectorySeparators(string value)
        {
            var rootLength = Path.GetPathRoot(value)?.Length ?? 0;
            var end = value.Length;

            while (end > rootLength && IsDirectorySeparator(value[end - 1]))
                end--;

            return end == value.Length ? value : value[..end];
        }

        static bool IsDirectorySeparator(char value) =>
            value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;
    }

    public static string ToPlatformPath(string? path)
    {
        var normalizedPath = Normalize(path);
        if (!OperatingSystem.IsWindows() || normalizedPath.Length == 0)
            return normalizedPath;

        if (normalizedPath.Length >= 6
            && normalizedPath[0] == '/'
            && normalizedPath[1] == 'm'
            && normalizedPath[2] == 'n'
            && normalizedPath[3] == 't'
            && normalizedPath[4] == '/'
            && char.IsAsciiLetter(normalizedPath[5])
            && (normalizedPath.Length == 6 || normalizedPath[6] == '/'))
        {
            var driveLetter = char.ToUpperInvariant(normalizedPath[5]);
            var remainder = normalizedPath.Length == 6 ? string.Empty : normalizedPath[7..].Replace('/', '\\');
            return remainder.Length == 0 ? $"{driveLetter}:\\" : $"{driveLetter}:\\{remainder}";
        }

        return normalizedPath.Replace('/', Path.DirectorySeparatorChar);
    }
}
