using System.Diagnostics;

namespace Conduit;

public sealed class AgentInstructionsCommandTests
{
    [Test]
    public async Task InstructionsPrintOnlyFromAUnityProject()
    {
        var root = Path.Combine(Path.GetTempPath(), $"conduit-agent-instructions-{Guid.NewGuid():N}");
        var unityProject = Path.Combine(root, "unity-project");
        var ordinaryDirectory = Path.Combine(root, "ordinary-directory");
        Directory.CreateDirectory(Path.Combine(unityProject, "Assets"));
        Directory.CreateDirectory(Path.Combine(unityProject, "Packages"));
        Directory.CreateDirectory(Path.Combine(unityProject, "ProjectSettings"));
        Directory.CreateDirectory(ordinaryDirectory);
        File.WriteAllText(Path.Combine(unityProject, "ProjectSettings", "ProjectVersion.txt"), string.Empty);

        try
        {
            var unityResult = await RunAsync(unityProject);
            var ordinaryResult = await RunAsync(ordinaryDirectory);

            await Assert.That(unityResult.ExitCode).IsEqualTo(0);
            await Assert.That(unityResult.StandardOutput).StartsWith("# Unity MCP usage instructions");
            await Assert.That(unityResult.StandardError).IsEmpty();
            await Assert.That(ordinaryResult.ExitCode).IsEqualTo(0);
            await Assert.That(ordinaryResult.StandardOutput).IsEmpty();
            await Assert.That(ordinaryResult.StandardError).IsEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(
            string workingDirectory
        )
        {
            var executable = Path.Combine(
                AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "conduit.exe" : "conduit"
            );
            var startInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--print-agent-instructions");

            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("The Conduit test process did not start.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await standardOutput, await standardError);
        }
    }
}
