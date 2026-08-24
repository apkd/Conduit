#if CONDUIT_LINUX
using System.Text;
namespace Conduit;

static class LinuxProcessQuery
{
    internal static bool TryQueryProcessesByName(string processName, out UnityProjectProcessInfo[] processes)
    {
        processes = [];
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(processName) || !Directory.Exists("/proc"))
            return false;

        try
        {
            var results = new List<UnityProjectProcessInfo>();
            foreach (var directoryPath in Directory.EnumerateDirectories("/proc"))
            {
                var processNameOffset = directoryPath.LastIndexOf(Path.DirectorySeparatorChar) + 1;
                if (!int.TryParse(directoryPath.AsSpan(processNameOffset), out var processId))
                    continue;

                if (!TryReadProcessInfo(directoryPath, processId, processName, out var processInfo))
                    continue;

                results.Add(processInfo);
            }

            processes = results.Count == 0 ? [] : results.ToArray();
            return true;
        }
        catch
        {
            return false;
        }
    }

    static bool TryReadProcessInfo(string directoryPath, int processId, string processName, out UnityProjectProcessInfo processInfo)
    {
        processInfo = default;
        var executablePath = TryReadExecutablePath(directoryPath);
        if (!MatchesProcessName(processName, TryReadComm(directoryPath), executablePath))
            return false;

        processInfo = new(processId, executablePath, TryReadCommandLine(directoryPath));
        return true;
    }

    static bool MatchesProcessName(string processName, string? comm, string? executablePath)
    {
        if (MatchesComm(processName, comm))
            return true;

        return MatchesProcessName(processName, Path.GetFileName(executablePath))
               || MatchesProcessName(processName, Path.GetFileNameWithoutExtension(executablePath));
    }

    static bool MatchesComm(string processName, string? comm)
    {
        var candidate = comm.AsSpan().Trim();
        if (candidate.Equals(processName.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return true;

        var extensionOffset = candidate.LastIndexOf('.');
        return extensionOffset > 0
               && candidate[..extensionOffset].Equals(
                   processName.AsSpan(),
                   StringComparison.OrdinalIgnoreCase
               );
    }

    static bool MatchesProcessName(string processName, string? candidate)
        => !string.IsNullOrWhiteSpace(candidate)
           && string.Equals(candidate, processName, StringComparison.OrdinalIgnoreCase);

    static string? TryReadExecutablePath(string directoryPath)
    {
        try
        {
            var target = File.ResolveLinkTarget(Path.Combine(directoryPath, "exe"), false);
            return target?.FullName;
        }
        catch
        {
            return null;
        }
    }

    static string? TryReadComm(string directoryPath)
    {
        try
        {
            var processName = File.ReadAllText(Path.Combine(directoryPath, "comm"));
            return string.IsNullOrWhiteSpace(processName) ? null : processName;
        }
        catch
        {
            return null;
        }
    }

    static string? TryReadCommandLine(string directoryPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(directoryPath, "cmdline"));
            if (bytes.Length == 0)
                return null;

            var builder = new StringBuilder(bytes.Length);
            var argumentStart = 0;
            for (var index = 0; index < bytes.Length; index++)
            {
                if (bytes[index] != 0)
                    continue;

                AppendArgument(bytes.AsSpan(argumentStart, index - argumentStart), builder);
                argumentStart = index + 1;
            }

            if (argumentStart < bytes.Length)
                AppendArgument(bytes.AsSpan(argumentStart), builder);

            return builder.Length == 0 ? null : builder.ToString();
        }
        catch
        {
            return null;
        }
    }

    static void AppendArgument(ReadOnlySpan<byte> argumentBytes, StringBuilder builder)
    {
        if (argumentBytes.Length == 0)
            return;

        if (builder.Length > 0)
            builder.Append(' ');

        var argument = Encoding.UTF8.GetString(argumentBytes);
        if (!RequiresQuoting(argument))
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        foreach (var character in argument)
        {
            if (character is '\\' or '"')
                builder.Append('\\');

            builder.Append(character);
        }

        builder.Append('"');
    }

    static bool RequiresQuoting(string value)
    {
        foreach (var character in value)
            if (char.IsWhiteSpace(character) || character is '"' or '\\')
                return true;

        return value.Length == 0;
    }
}
#endif
