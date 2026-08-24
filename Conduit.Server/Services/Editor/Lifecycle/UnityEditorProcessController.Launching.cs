using System.Diagnostics;

namespace Conduit;

public sealed partial class UnityEditorProcessController
{
    internal static string BuildLaunchArguments(string platformProjectPath, string logPath) =>
        $"-projectPath \"{platformProjectPath}\" -logFile \"{logPath}\"";

    internal static ProcessStartInfo CreateLaunchStartInfo(string editorPath, string platformProjectPath, string restartLogPath)
        => CreateLaunchStartInfo(
            editorPath,
            platformProjectPath,
            restartLogPath,
            OperatingSystem.IsLinux(),
            File.Exists("/etc/NIXOS"),
            FindExecutableOnPath,
            TryReadAllText
        );

    internal static ProcessStartInfo CreateLaunchStartInfo(
        string editorPath,
        string platformProjectPath,
        string restartLogPath,
        bool isLinux,
        bool isNixOs,
        Func<string, string?> findExecutableOnPath,
        Func<string, string?> readTextFile
    )
    {
        var launchArguments = BuildLaunchArguments(platformProjectPath, restartLogPath);
        if (isLinux)
        {
            var launchExecutablePath = isNixOs && ResolveNixOsUnityWrapper(findExecutableOnPath, readTextFile) is { Length: > 0 } wrapperPath
                ? wrapperPath
                : editorPath;

            var linuxStartInfo = CreateDetachedLinuxStartInfo(
                launchExecutablePath,
                editorPath,
                platformProjectPath,
                restartLogPath,
                findExecutableOnPath
            );
            if (linuxStartInfo is null)
            {
                linuxStartInfo = new(launchExecutablePath)
                {
                    Arguments = string.Equals(launchExecutablePath, editorPath, StringComparison.Ordinal)
                        ? launchArguments
                        : $"{QuoteArgument(editorPath)} {launchArguments}",
                    WorkingDirectory = Path.GetDirectoryName(editorPath) ?? AppContext.BaseDirectory,
                    UseShellExecute = false,
                };
            }

            UnityEditorLaunchEnvironment.ApplyGraphicalSessionEnvironment(linuxStartInfo);
            return linuxStartInfo;
        }

        var startInfo = new ProcessStartInfo(editorPath)
        {
            Arguments = launchArguments,
            WorkingDirectory = Path.GetDirectoryName(editorPath) ?? AppContext.BaseDirectory,
            UseShellExecute = true,
        };

        return startInfo;
    }

    static string? ResolveNixOsUnityWrapper(Func<string, string?> findExecutableOnPath, Func<string, string?> readTextFile)
    {
        if (FindUnityHubFhsEnv(findExecutableOnPath, readTextFile) is { Length: > 0 } unityHubFhsEnvPath)
            return unityHubFhsEnvPath;

        return findExecutableOnPath("steam-run");
    }

    static ProcessStartInfo? CreateDetachedLinuxStartInfo(
        string launchExecutablePath,
        string editorPath,
        string platformProjectPath,
        string restartLogPath,
        Func<string, string?> findExecutableOnPath
    )
    {
        var shellPath = ResolveExecutablePath("bash", findExecutableOnPath, "/bin/bash", "/usr/bin/bash")
                        ?? ResolveExecutablePath("sh", findExecutableOnPath, "/bin/sh", "/usr/bin/sh");
        if (string.IsNullOrWhiteSpace(shellPath))
            return null;

        var startInfo = new ProcessStartInfo(ResolveExecutablePath("setsid", findExecutableOnPath, "/usr/bin/setsid", "/bin/setsid") ?? shellPath)
        {
            WorkingDirectory = Path.GetDirectoryName(editorPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
        };

        if (!string.Equals(startInfo.FileName, shellPath, StringComparison.Ordinal))
            startInfo.ArgumentList.Add(shellPath);

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exec </dev/null >/dev/null 2>&1; \"$@\" & child=$!; wait \"$child\""); // keep Unity wrappers off the MCP stdio transport
        startInfo.ArgumentList.Add("conduit-unity-launch");
        startInfo.ArgumentList.Add(launchExecutablePath);
        if (!string.Equals(launchExecutablePath, editorPath, StringComparison.Ordinal))
            startInfo.ArgumentList.Add(editorPath);
        startInfo.ArgumentList.Add("-projectPath");
        startInfo.ArgumentList.Add(platformProjectPath);
        startInfo.ArgumentList.Add("-logFile");
        startInfo.ArgumentList.Add(restartLogPath);
        return startInfo;
    }

    static string? ResolveExecutablePath(string executableName, Func<string, string?> findExecutableOnPath, params string[] fallbackPaths)
    {
        if (findExecutableOnPath(executableName) is { Length: > 0 } path)
            return path;

        foreach (var fallbackPath in fallbackPaths)
            if (File.Exists(fallbackPath))
                return fallbackPath;

        return null;
    }

    static string? FindUnityHubFhsEnv(Func<string, string?> findExecutableOnPath, Func<string, string?> readTextFile)
    {
        if (findExecutableOnPath("unityhub-fhs-env") is { Length: > 0 } directPath)
            return directPath;

        var unityHubPath = findExecutableOnPath("unityhub");
        if (string.IsNullOrWhiteSpace(unityHubPath))
            return null;

        return TryExtractUnityHubFhsEnvPath(readTextFile(unityHubPath));
    }

    internal static string? TryExtractUnityHubFhsEnvPath(string? wrapperText)
    {
        if (string.IsNullOrWhiteSpace(wrapperText))
            return null;

        const string marker = "unityhub-fhs-env";
        var searchStart = 0;
        while (true)
        {
            var markerIndex = wrapperText.IndexOf(marker, searchStart, StringComparison.Ordinal);
            if (markerIndex < 0)
                return null;

            var start = markerIndex;
            while (start > 0 && !IsShellTokenBoundary(wrapperText[start - 1]))
                start--;

            var end = markerIndex + marker.Length;
            while (end < wrapperText.Length && !IsShellTokenBoundary(wrapperText[end]))
                end++;

            var candidate = wrapperText[start..end];
            if (candidate.EndsWith("/bin/unityhub-fhs-env", StringComparison.Ordinal)
                || string.Equals(Path.GetFileName(candidate), "unityhub-fhs-env", StringComparison.Ordinal))
                return candidate;

            searchStart = markerIndex + marker.Length;
        }
    }

    static bool IsShellTokenBoundary(char character) =>
        char.IsWhiteSpace(character) || character is '"' or '\'';

    static string? FindExecutableOnPath(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidatePath = Path.Combine(directory, executableName);
            if (File.Exists(candidatePath))
                return candidatePath;
        }

        return null;
    }

    static string? TryReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return null;
        }
    }

    static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
