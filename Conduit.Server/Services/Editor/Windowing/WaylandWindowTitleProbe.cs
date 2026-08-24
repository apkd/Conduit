using System.Diagnostics;

namespace Conduit;

static class WaylandWindowTitleProbe
{
    const int WindowProbeTimeoutMilliseconds = 2000;

    internal static void AddTitles(
        List<UnityWindowTitleSignal> titles,
        int processId,
        LinuxProcessEnvironment environment)
    {
        Add(TryReadHyprlandWindowTitles(processId, environment));
        Add(TryReadSwayWindowTitles(processId, environment));
        Add(TryReadNiriWindowTitles(processId, environment));

        void Add(IEnumerable<UnityWindowTitleSignal> source)
        {
            foreach (var signal in source)
                UnityWindowTitleProbe.AddTitle(titles, signal.Title, signal.IsFocused, signal.Source);
        }
    }

    static IReadOnlyList<UnityWindowTitleSignal> TryReadHyprlandWindowTitles(
        int processId,
        LinuxProcessEnvironment environment)
    {
        try
        {
            return RunWindowProbeCommand(CreateHyprlandClientsStartInfo(environment)) is { } output
                ? WaylandWindowTitleParser.ReadHyprlandClientsWindowTitles(output, processId)
                : [];
        }
        catch
        {
            return [];
        }
    }

    static IReadOnlyList<UnityWindowTitleSignal> TryReadSwayWindowTitles(
        int processId,
        LinuxProcessEnvironment environment)
    {
        try
        {
            return RunWindowProbeCommand(CreateSwayTreeStartInfo(environment)) is { } output
                ? WaylandWindowTitleParser.ReadSwayTreeWindowTitles(output, processId)
                : [];
        }
        catch
        {
            return [];
        }
    }

    static IReadOnlyList<UnityWindowTitleSignal> TryReadNiriWindowTitles(
        int processId,
        LinuxProcessEnvironment environment)
    {
        try
        {
            return RunWindowProbeCommand(CreateNiriWindowsStartInfo(environment)) is { } output
                ? WaylandWindowTitleParser.ReadNiriWindowsWindowTitles(output, processId)
                : [];
        }
        catch
        {
            return [];
        }
    }

    static string? RunWindowProbeCommand(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        if (process == null)
            return null;

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(WindowProbeTimeoutMilliseconds))
        {
            ProcessTermination.TryKillTree(process);
            return null;
        }

        _ = errorTask.GetAwaiter().GetResult();
        return process.ExitCode == 0 ? outputTask.GetAwaiter().GetResult() : null;
    }

    static ProcessStartInfo CreateHyprlandClientsStartInfo(LinuxProcessEnvironment environment)
    {
        var processPath = environment.GetValue("PATH");
        var startInfo = new ProcessStartInfo(
            WaylandCompositorDiscovery.ResolveExecutablePath(
                "hyprctl",
                processPath,
                Environment.GetEnvironmentVariable("PATH")
            )
        )
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("clients");
        startInfo.ArgumentList.Add("-j");
        if (!string.IsNullOrWhiteSpace(processPath))
            startInfo.Environment["PATH"] = processPath;

        var xdgRuntimeDirectory = WaylandCompositorDiscovery.ResolveXdgRuntimeDirectory(
            environment.GetValue("XDG_RUNTIME_DIR")
        );
        if (!string.IsNullOrWhiteSpace(xdgRuntimeDirectory))
            startInfo.Environment["XDG_RUNTIME_DIR"] = xdgRuntimeDirectory;

        var waylandDisplay = environment.GetValue("WAYLAND_DISPLAY")
                             ?? Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        if (!string.IsNullOrWhiteSpace(waylandDisplay))
            startInfo.Environment["WAYLAND_DISPLAY"] = waylandDisplay;

        var signature = environment.GetValue("HYPRLAND_INSTANCE_SIGNATURE")
                        ?? Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE");
        if (!string.IsNullOrWhiteSpace(signature))
            startInfo.Environment["HYPRLAND_INSTANCE_SIGNATURE"] = signature;
        else if (WaylandCompositorDiscovery.TryInferHyprlandInstanceSignature(
                     xdgRuntimeDirectory
                 )
                 is { } inferredSignature)
            startInfo.Environment["HYPRLAND_INSTANCE_SIGNATURE"] = inferredSignature;

        return startInfo;
    }

    static ProcessStartInfo CreateSwayTreeStartInfo(LinuxProcessEnvironment environment)
    {
        var processPath = environment.GetValue("PATH");
        var startInfo = new ProcessStartInfo(
            WaylandCompositorDiscovery.ResolveExecutablePath(
                "swaymsg",
                processPath,
                Environment.GetEnvironmentVariable("PATH")
            )
        )
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(processPath))
            startInfo.Environment["PATH"] = processPath;

        var xdgRuntimeDirectory = WaylandCompositorDiscovery.ResolveXdgRuntimeDirectory(
            environment.GetValue("XDG_RUNTIME_DIR")
        );
        if (!string.IsNullOrWhiteSpace(xdgRuntimeDirectory))
            startInfo.Environment["XDG_RUNTIME_DIR"] = xdgRuntimeDirectory;

        if (WaylandCompositorDiscovery.TryFindSwaySocket(
                xdgRuntimeDirectory,
                environment.GetValue("SWAYSOCK"),
                environment.GetValue("I3SOCK"),
                Environment.GetEnvironmentVariable("SWAYSOCK"),
                Environment.GetEnvironmentVariable("I3SOCK")
            )
            is { } socketPath)
        {
            startInfo.ArgumentList.Add("--socket");
            startInfo.ArgumentList.Add(socketPath);
        }

        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("get_tree");
        startInfo.ArgumentList.Add("--raw");
        return startInfo;
    }

    static ProcessStartInfo CreateNiriWindowsStartInfo(LinuxProcessEnvironment environment)
    {
        var processPath = environment.GetValue("PATH");
        var startInfo = new ProcessStartInfo(
            WaylandCompositorDiscovery.ResolveExecutablePath(
                "niri",
                processPath,
                Environment.GetEnvironmentVariable("PATH")
            )
        )
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(processPath))
            startInfo.Environment["PATH"] = processPath;

        var xdgRuntimeDirectory = WaylandCompositorDiscovery.ResolveXdgRuntimeDirectory(
            environment.GetValue("XDG_RUNTIME_DIR")
        );
        if (!string.IsNullOrWhiteSpace(xdgRuntimeDirectory))
            startInfo.Environment["XDG_RUNTIME_DIR"] = xdgRuntimeDirectory;

        var waylandDisplay = environment.GetValue("WAYLAND_DISPLAY")
                             ?? Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        if (!string.IsNullOrWhiteSpace(waylandDisplay))
            startInfo.Environment["WAYLAND_DISPLAY"] = waylandDisplay;

        if (WaylandCompositorDiscovery.TryFindNiriSocket(
                xdgRuntimeDirectory,
                waylandDisplay,
                environment.GetValue("NIRI_SOCKET"),
                Environment.GetEnvironmentVariable("NIRI_SOCKET")
            )
            is { } socketPath)
            startInfo.Environment["NIRI_SOCKET"] = socketPath;

        startInfo.ArgumentList.Add("msg");
        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add("windows");
        return startInfo;
    }
}
