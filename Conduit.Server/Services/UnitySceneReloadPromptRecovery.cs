using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Conduit;

/// <summary>Attempts to recover Unity editors blocked by the changed-open-scene reload prompt.</summary>
public sealed class UnitySceneReloadPromptRecovery(ILogger<UnitySceneReloadPromptRecovery> logger)
{
    const int RecoveryTimeoutMilliseconds = 5000;
    const string XdotoolExecutableName = "xdotool";

    /// <summary>Accepts the reload prompt for the specified Unity editor process when a safe match is found.</summary>
    public async Task<bool> TryDismissAsync(string projectPath, int? processId, CancellationToken ct)
    {
        if (processId is not > 0)
            return false;

        try
        {
            bool dismissed = false;
            if (OperatingSystem.IsWindows())
                dismissed = await TryDismissWindowsAsync(processId.Value, ct);

            if (OperatingSystem.IsLinux())
                dismissed = TryDismissLinuxAsync(processId.Value);

            if (dismissed)
                // every recovery action is logged because accepting reload can discard clean in-memory scene state.
                logger.ZLogInformation($"Accepted Unity scene reload prompt for project {projectPath} on Unity pid {processId.Value}.");

            return dismissed;
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            logger.ZLogDebug($"Scene reload prompt recovery failed for project {projectPath} on Unity pid {processId.Value}.", exception);
        }

        return false;
    }

    internal static bool IsSceneReloadPromptText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains("open scene", StringComparison.OrdinalIgnoreCase)
               && text.Contains("reload", StringComparison.OrdinalIgnoreCase)
               && (text.Contains("changed on disk", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("modified externally", StringComparison.OrdinalIgnoreCase));
    }

    async Task<bool> TryDismissWindowsAsync(int processId, CancellationToken ct)
    {
        var powerShellPath = GetPowerShellPath();
        if (!File.Exists(powerShellPath))
            return false;

        // uia can invoke the button without relying on focus or keyboard state.
        using var process = StartProcess(
            new(powerShellPath)
            {
                ArgumentList =
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-EncodedCommand",
                    EncodePowerShellScript(BuildWindowsSceneReloadPromptScript(processId)),
                },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        );

        if (process == null)
            return false;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RecoveryTimeoutMilliseconds);
        var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            _ = await outputTask;
            _ = await errorTask;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            return false;
        }

        return process.ExitCode == 0;
    }

    static string BuildWindowsSceneReloadPromptScript(int processId) =>
        $$"""
          $ErrorActionPreference = 'Stop'
          Add-Type -AssemblyName UIAutomationClient
          Add-Type -AssemblyName UIAutomationTypes

          $targetProcessId = {{processId}}
          $processCondition = New-Object System.Windows.Automation.PropertyCondition(
              [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
              $targetProcessId
          )
          $elements = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
              [System.Windows.Automation.TreeScope]::Subtree,
              $processCondition
          )

          $names = New-Object System.Text.StringBuilder
          $reloadButtons = New-Object 'System.Collections.Generic.List[System.Windows.Automation.AutomationElement]'

          foreach ($element in $elements) {
              try { $name = $element.Current.Name } catch { continue }
              if ([string]::IsNullOrWhiteSpace($name)) { continue }

              [void]$names.AppendLine($name)

              try {
                  if ($element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $name -eq 'Reload') {
                      $reloadButtons.Add($element)
                  }
              } catch { }
          }

          $text = $names.ToString()
          $matchesPrompt = $text -match '(?is)open scene' `
              -and $text -match '(?is)reload' `
              -and ($text -match '(?is)changed on disk' -or $text -match '(?is)modified externally')

          # pid + body text + button label prevent clicking unrelated unity dialogs.
          if (-not $matchesPrompt -or $reloadButtons.Count -eq 0) {
              exit 2
          }

          foreach ($button in $reloadButtons) {
              try {
                  $pattern = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                  $pattern.Invoke()
                  [Console]::Out.Write('Reload')
                  exit 0
              } catch { }
          }

          exit 3
          """;

    static bool TryDismissLinuxAsync(int processId)
    {
        var xdotoolPath = SafeModeWindowProbe.ResolveExecutablePath(
            XdotoolExecutableName,
            TryReadProcessEnvironmentValue(processId, "PATH"),
            Environment.GetEnvironmentVariable("PATH")
        );

        if (!File.Exists(xdotoolPath))
            return false;

        // xdotool is limited to x11/xwayland, so linux recovery is intentionally best-effort.
        foreach (var pattern in EnumerateLinuxPromptWindowTitlePatterns())
        {
            var windows = RunWindowProbeCommand(CreateXdotoolSearchStartInfo(xdotoolPath, processId, pattern));
            foreach (var windowId in SplitLines(windows))
                if (RunWindowActionCommand(CreateXdotoolReloadStartInfo(xdotoolPath, processId, windowId)))
                    return true;
        }

        return false;
    }

    static IEnumerable<string> EnumerateLinuxPromptWindowTitlePatterns()
    {
        yield return "changed on disk";
        yield return "modified externally";
        yield return "reload the scene";
    }

    static ProcessStartInfo CreateXdotoolSearchStartInfo(string xdotoolPath, int processId, string pattern)
    {
        var startInfo = CreateXdotoolStartInfo(xdotoolPath, processId);
        startInfo.ArgumentList.Add("search");
        startInfo.ArgumentList.Add("--pid");
        startInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--name");
        startInfo.ArgumentList.Add(pattern);
        return startInfo;
    }

    static ProcessStartInfo CreateXdotoolReloadStartInfo(string xdotoolPath, int processId, string windowId)
    {
        var startInfo = CreateXdotoolStartInfo(xdotoolPath, processId);
        startInfo.ArgumentList.Add("windowactivate");
        startInfo.ArgumentList.Add("--sync");
        startInfo.ArgumentList.Add(windowId);
        startInfo.ArgumentList.Add("key");
        startInfo.ArgumentList.Add("Return");
        return startInfo;
    }

    static ProcessStartInfo CreateXdotoolStartInfo(string xdotoolPath, int processId)
    {
        var startInfo = new ProcessStartInfo(xdotoolPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var processPath = TryReadProcessEnvironmentValue(processId, "PATH");
        if (!string.IsNullOrWhiteSpace(processPath))
            startInfo.Environment["PATH"] = processPath;

        var display = TryReadProcessEnvironmentValue(processId, "DISPLAY") ?? Environment.GetEnvironmentVariable("DISPLAY");
        if (!string.IsNullOrWhiteSpace(display))
            startInfo.Environment["DISPLAY"] = display;

        var xauthority = TryReadProcessEnvironmentValue(processId, "XAUTHORITY") ?? Environment.GetEnvironmentVariable("XAUTHORITY");
        if (!string.IsNullOrWhiteSpace(xauthority))
            startInfo.Environment["XAUTHORITY"] = xauthority;

        return startInfo;
    }

    static string? RunWindowProbeCommand(ProcessStartInfo startInfo)
    {
        using var process = StartProcess(startInfo);
        if (process == null)
            return null;

        var outputTask = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(RecoveryTimeoutMilliseconds))
        {
            TryKillProcessTree(process);
            return null;
        }

        return process.ExitCode == 0 ? outputTask.GetAwaiter().GetResult() : null;
    }

    static bool RunWindowActionCommand(ProcessStartInfo startInfo)
    {
        using var process = StartProcess(startInfo);
        if (process == null)
            return false;

        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(RecoveryTimeoutMilliseconds))
        {
            TryKillProcessTree(process);
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

    static Process? StartProcess(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo);
        }
        catch
        {
            return null;
        }
    }

    static string EncodePowerShellScript(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    static string GetPowerShellPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe"
    );

    static void TryKillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    static string? TryReadProcessEnvironmentValue(int processId, string name)
    {
        if (!OperatingSystem.IsLinux())
            return null;

        try
        {
            var bytes = File.ReadAllBytes($"/proc/{processId}/environ");
            var prefix = Encoding.UTF8.GetBytes($"{name}=");
            var offset = 0;
            while (offset < bytes.Length)
            {
                var terminatorOffset = Array.IndexOf(bytes, (byte)0, offset);
                if (terminatorOffset < 0)
                    terminatorOffset = bytes.Length;

                var length = terminatorOffset - offset;
                if (length > prefix.Length
                    && bytes.AsSpan(offset, prefix.Length).SequenceEqual(prefix))
                    return Encoding.UTF8.GetString(bytes, offset + prefix.Length, length - prefix.Length);

                offset = terminatorOffset + 1;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
