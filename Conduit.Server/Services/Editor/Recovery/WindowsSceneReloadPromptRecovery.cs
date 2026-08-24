using System.Diagnostics;

namespace Conduit;

static class WindowsSceneReloadPromptRecovery
{
    internal static async Task<bool> TryDismissAsync(int processId, CancellationToken ct)
    {
        var powerShellPath = WindowsPowerShell.ExecutablePath;
        if (!File.Exists(powerShellPath))
            return false;

        // uia can invoke the button without relying on focus or keyboard state.
        using var process = UnitySceneReloadPromptRecovery.TryStartProcess(
            new(powerShellPath)
            {
                ArgumentList =
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-EncodedCommand",
                    WindowsPowerShell.EncodeScript(BuildScript(processId)),
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
        timeoutCts.CancelAfter(UnitySceneReloadPromptRecovery.RecoveryTimeoutMilliseconds);
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
            ProcessTermination.TryKillTree(process);
            return false;
        }

        return process.ExitCode == 0;
    }

    static string BuildScript(int processId) =>
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
}
