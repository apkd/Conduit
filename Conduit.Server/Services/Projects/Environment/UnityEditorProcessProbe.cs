using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Conduit;

static partial class UnityEditorProcessProbe
{
    [GeneratedRegex("-(?:projectPath|createproject)\\s+(?:\"(?<path>[^\"]+)\"|(?<path>\\S+))", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex EditorProjectPathArgumentRegex();

    [GeneratedRegex("(?:^|\\s)(?:-adb2(?:\\s|$)|-parentPid(?:\\s|$)|-name\\s+\"?AssetImportWorker)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AuxiliaryUnityProcessArgumentRegex();

    internal static string? TryReadSafeModeDiagnostic(UnityProjectEnvironmentSnapshot snapshot)
    {
        if (snapshot.MatchedProcess is not { } matchedProcess)
            return null;

        var mainWindowTitle = UnityWindowTitleProbe.TryReadMainWindowTitle(matchedProcess.ProcessId) ?? "";
        if (SafeModeWindowProbe.IsSafeModeWindowTitle(mainWindowTitle))
            return UnityProjectEnvironmentProbe.SafeModeDiagnostic;

        if (SafeModeWindowProbe.TryReadSafeModeWindowSignal(
                matchedProcess.ProcessId,
                mainWindowTitle
            ) is not null)
            return UnityProjectEnvironmentProbe.SafeModeDiagnostic;

        return null;
    }

    internal static int? ResolveEditorProcessId(UnityProjectEnvironmentSnapshot snapshot, BridgeProjectHandshake? handshake = null)
    {
        if (handshake?.EditorProcessId > 0)
            return handshake.EditorProcessId;

        return snapshot.MatchedProcess?.ProcessId;
    }

    internal static UnityEditorProcessRuntimeInfo? TryReadProcessRuntime(int? processId)
    {
        if (processId is not > 0)
            return null;

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            if (process.HasExited)
                return null;

            return new(process.Id, new DateTimeOffset(process.StartTime));
        }
        catch
        {
            return null;
        }
    }

    internal static UnityProjectProcessInfo? FindMatchingProjectProcess(
        IReadOnlyList<UnityProjectProcessInfo> runningUnityProcesses,
        string projectPath
    )
    {
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
        foreach (var processInfo in runningUnityProcesses)
        {
            // import workers use the editor executable and repeat their parent's project path
            if (AuxiliaryUnityProcessArgumentRegex().IsMatch(processInfo.CommandLine ?? ""))
                continue;

            var processProjectPath = UnityProjectMetadata.TryExtractProjectPathFromCommandLine(
                processInfo.CommandLine,
                EditorProjectPathArgumentRegex()
            );
            if (string.Equals(processProjectPath, normalizedProjectPath, StringComparison.OrdinalIgnoreCase))
                return processInfo;
        }

        return null;
    }

    internal static UnityProjectProcessInfo[] QueryUnityProcesses()
    {
        try
        {
            if (ProcessQuery.TryQueryProcessesByName("Unity", out var nativeProcesses))
                return nativeProcesses;

            if (!OperatingSystem.IsWindows())
                return [];

            var powerShellPath = WindowsPowerShell.ExecutablePath;
            using var process = Process.Start(
                new ProcessStartInfo(powerShellPath)
                {
                    Arguments = "-NoProfile -NonInteractive -Command \"$ErrorActionPreference='Stop'; Get-CimInstance Win32_Process -Filter \\\"name = 'Unity.exe'\\\" | Select-Object ProcessId,ExecutablePath,CommandLine | ConvertTo-Json -Compress\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            );

            if (process == null)
                return [];

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return [];

            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return ToProcessInfo(document.RootElement) is { } processInfo ? [processInfo] : [];

            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var processes = new List<UnityProjectProcessInfo>();
            foreach (var element in document.RootElement.EnumerateArray())
                if (ToProcessInfo(element) is { } processInfo)
                    processes.Add(processInfo);

            return processes.Count == 0 ? [] : processes.ToArray();
        }
        catch
        {
            return [];
        }
    }

    static UnityProjectProcessInfo? ToProcessInfo(JsonElement element)
    {
        if (!element.TryGetProperty("ProcessId", out var processIdElement))
            return null;

        var processId = processIdElement.GetInt32();
        var executablePath = element.TryGetProperty("ExecutablePath", out var executablePathElement)
            ? executablePathElement.GetString()
            : null;

        var commandLine = element.TryGetProperty("CommandLine", out var commandLineElement)
            ? commandLineElement.GetString()
            : null;

        return new(processId, executablePath, commandLine);
    }
}
