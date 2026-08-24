using System.Diagnostics;

namespace Conduit;

static class LinuxSceneReloadPromptRecovery
{
    const string XdotoolExecutableName = "xdotool";

    internal static bool TryDismiss(int processId)
    {
        var processEnvironment = LinuxProcessEnvironment.Read(processId);
        var xdotoolPath = WaylandCompositorDiscovery.ResolveExecutablePath(
            XdotoolExecutableName,
            processEnvironment.GetValue("PATH"),
            Environment.GetEnvironmentVariable("PATH")
        );

        if (!File.Exists(xdotoolPath))
            return false;

        // xdotool is limited to x11/xwayland, so linux recovery is intentionally best-effort.
        foreach (var pattern in EnumeratePromptWindowTitlePatterns())
        {
            var windows = RunWindowProbeCommand(
                CreateSearchStartInfo(
                    xdotoolPath,
                    processId,
                    pattern,
                    processEnvironment
                )
            );
            foreach (var windowId in SplitLines(windows))
                if (RunWindowActionCommand(
                        CreateReloadStartInfo(
                            xdotoolPath,
                            processId,
                            windowId,
                            processEnvironment
                        )
                    ))
                    return true;
        }

        return false;
    }

    static IEnumerable<string> EnumeratePromptWindowTitlePatterns()
    {
        yield return "changed on disk";
        yield return "modified externally";
        yield return "reload the scene";
    }

    static ProcessStartInfo CreateSearchStartInfo(
        string xdotoolPath,
        int processId,
        string pattern,
        LinuxProcessEnvironment processEnvironment)
    {
        var startInfo = CreateStartInfo(xdotoolPath, processEnvironment);
        startInfo.ArgumentList.Add("search");
        startInfo.ArgumentList.Add("--pid");
        startInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--name");
        startInfo.ArgumentList.Add(pattern);
        return startInfo;
    }

    static ProcessStartInfo CreateReloadStartInfo(
        string xdotoolPath,
        int processId,
        string windowId,
        LinuxProcessEnvironment processEnvironment)
    {
        var startInfo = CreateStartInfo(xdotoolPath, processEnvironment);
        startInfo.ArgumentList.Add("windowactivate");
        startInfo.ArgumentList.Add("--sync");
        startInfo.ArgumentList.Add(windowId);
        startInfo.ArgumentList.Add("key");
        startInfo.ArgumentList.Add("Return");
        return startInfo;
    }

    static ProcessStartInfo CreateStartInfo(
        string xdotoolPath,
        LinuxProcessEnvironment processEnvironment)
    {
        var startInfo = new ProcessStartInfo(xdotoolPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var path = processEnvironment.GetValue("PATH");
        if (!string.IsNullOrWhiteSpace(path))
            startInfo.Environment["PATH"] = path;

        var display = processEnvironment.GetValue("DISPLAY")
                      ?? Environment.GetEnvironmentVariable("DISPLAY");
        if (!string.IsNullOrWhiteSpace(display))
            startInfo.Environment["DISPLAY"] = display;

        var xauthority = processEnvironment.GetValue("XAUTHORITY")
                         ?? Environment.GetEnvironmentVariable("XAUTHORITY");
        if (!string.IsNullOrWhiteSpace(xauthority))
            startInfo.Environment["XAUTHORITY"] = xauthority;

        return startInfo;
    }

    static string? RunWindowProbeCommand(ProcessStartInfo startInfo)
    {
        using var process = UnitySceneReloadPromptRecovery.TryStartProcess(startInfo);
        if (process == null)
            return null;

        var outputTask = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(UnitySceneReloadPromptRecovery.RecoveryTimeoutMilliseconds))
        {
            ProcessTermination.TryKillTree(process);
            return null;
        }

        return process.ExitCode == 0 ? outputTask.GetAwaiter().GetResult() : null;
    }

    static bool RunWindowActionCommand(ProcessStartInfo startInfo)
    {
        using var process = UnitySceneReloadPromptRecovery.TryStartProcess(startInfo);
        if (process == null)
            return false;

        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(UnitySceneReloadPromptRecovery.RecoveryTimeoutMilliseconds))
        {
            ProcessTermination.TryKillTree(process);
            return false;
        }

        return process.ExitCode == 0;
    }

    static IEnumerable<string> SplitLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (line.Length > 0)
                yield return line;
    }
}
