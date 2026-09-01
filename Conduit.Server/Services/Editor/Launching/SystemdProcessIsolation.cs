using System.Diagnostics;

namespace Conduit;

/// <summary>Moves Unity launches into user-manager services that survive Conduit service restarts.</summary>
static class SystemdProcessIsolation
{
    internal static bool TryApply(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsLinux())
            return false;

        var systemdRunPath = ResolveExecutablePath(
            "systemd-run",
            "/usr/bin/systemd-run",
            "/bin/systemd-run",
            "/run/current-system/sw/bin/systemd-run"
        );
        var shellPath = ResolveExecutablePath("bash", "/bin/bash", "/usr/bin/bash", "/run/current-system/sw/bin/bash")
                        ?? ResolveExecutablePath("sh", "/bin/sh", "/usr/bin/sh");

        return TryApply(startInfo, TryReadCgroup(), systemdRunPath, shellPath);

        string? TryReadCgroup()
        {
            try
            {
                return File.ReadAllText("/proc/self/cgroup");
            }
            catch
            {
                return null; // restricted hosts retain the existing direct launch
            }
        }

        string? ResolveExecutablePath(string executableName, params string[] fallbackPaths)
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(path))
            {
                var directories = path.Split(
                    Path.PathSeparator,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                );
                foreach (var directory in directories)
                {
                    var candidatePath = Path.Combine(directory, executableName);
                    if (File.Exists(candidatePath))
                        return candidatePath;
                }
            }

            foreach (var fallbackPath in fallbackPaths)
                if (File.Exists(fallbackPath))
                    return fallbackPath;

            return null;
        }
    }

    internal static bool TryApply(
        ProcessStartInfo startInfo,
        string? cgroupText,
        string? systemdRunPath,
        string? shellPath
    )
    {
        // unsupported or restricted hosts keep the direct launch rather than failing to start Unity.
        if (!IsServiceCgroup(cgroupText))
            return false;

        if (string.IsNullOrWhiteSpace(systemdRunPath))
            return false;

        if (string.IsNullOrWhiteSpace(shellPath))
            return false;

        if (!startInfo.Environment.TryGetValue("DBUS_SESSION_BUS_ADDRESS", out var sessionBusAddress))
            return false;

        if (string.IsNullOrWhiteSpace(sessionBusAddress))
            return false;

        var commandPath = startInfo.FileName;
        var commandArguments = startInfo.ArgumentList.ToArray();
        var environment = startInfo.Environment.ToArray();

        // a transient scope follows its systemd-run client when conduit.service stops.
        // create a sibling service instead: conduit.service -> waiter | run-*.service -> Unity.
        startInfo.FileName = shellPath;
        startInfo.ArgumentList.Clear();
        startInfo.ArgumentList.Add("-c");
        // the waiting systemd-run client remains in conduit.service, so detach its MCP descriptors.
        startInfo.ArgumentList.Add("exec </dev/null >/dev/null 2>&1; exec \"$@\"");
        startInfo.ArgumentList.Add("conduit-unity-systemd-launch");
        startInfo.ArgumentList.Add(systemdRunPath);
        startInfo.ArgumentList.Add("--user");
        startInfo.ArgumentList.Add("--wait"); // preserve immediate editor launch failures
        startInfo.ArgumentList.Add("--quiet");
        startInfo.ArgumentList.Add("--collect");
        startInfo.ArgumentList.Add("--service-type=exec");

        // manager-started services otherwise lose the finalized restart environment and working directory.
        if (!string.IsNullOrWhiteSpace(startInfo.WorkingDirectory))
            startInfo.ArgumentList.Add($"--working-directory={startInfo.WorkingDirectory}");
        foreach (var (variableName, value) in environment)
            startInfo.ArgumentList.Add($"--setenv={variableName}={value}");

        // systemd-run expands dollars in ExecStart; doubling preserves paths and caller arguments verbatim.
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(EscapeCommandArgument(commandPath));
        // systemd may start ExecStart as a process-group leader, which makes setsid fork.
        // keep its parent as the service main process so systemd does not tear down Unity's cgroup.
        if (Path.GetFileName(commandPath) is "setsid")
            startInfo.ArgumentList.Add("--wait");
        foreach (var argument in commandArguments)
            startInfo.ArgumentList.Add(EscapeCommandArgument(argument));
        startInfo.UseShellExecute = false;
        return true;

        string EscapeCommandArgument(string argument) =>
            argument.Replace("$", "$$", StringComparison.Ordinal);
    }

    internal static bool IsServiceCgroup(string? cgroupText)
    {
        if (string.IsNullOrWhiteSpace(cgroupText))
            return false;

        var lines = cgroupText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // user@UID.service appears in every user cgroup ancestry, so inspect only the leaf unit.
        foreach (var line in lines)
        {
            var cgroupPath = line[(line.LastIndexOf(':') + 1)..].TrimEnd('/');
            if (cgroupPath.EndsWith(".service", StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
