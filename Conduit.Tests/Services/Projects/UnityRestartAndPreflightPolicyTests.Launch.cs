using System.Diagnostics;
using System.Text;

namespace Conduit;

public sealed partial class UnityRestartAndPreflightPolicyTests
{
    [Test]
    public async Task RestartUsageTimestampReplacesInheritedStateOnlyForTrackedRestarts()
    {
        var startInfo = new ProcessStartInfo { UseShellExecute = false };
        startInfo.Environment[
            UnityEditorLaunchEnvironment.RestartStartedUtcTicksEnvironmentVariable
        ] = "stale";

        UnityEditorLaunchEnvironment.ApplyRestartUsageTracking(
            startInfo,
            startedUtcTicks: null
        );
        await Assert.That(
            startInfo.Environment.ContainsKey(
                UnityEditorLaunchEnvironment.RestartStartedUtcTicksEnvironmentVariable
            )
        ).IsFalse();

        UnityEditorLaunchEnvironment.ApplyRestartUsageTracking(
            startInfo,
            startedUtcTicks: 123L
        );
        await Assert.That(
            startInfo.Environment[
                UnityEditorLaunchEnvironment.RestartStartedUtcTicksEnvironmentVariable
            ]
        ).IsEqualTo("123");
    }

    [Test]
    public async Task RestartLaunchArgumentsPreserveCallerTokenBoundaries()
    {
        var projectPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"conduit-project-{Guid.NewGuid():N}"));
        var logPath = Path.GetFullPath(Path.Combine(projectPath, "Logs", "Editor.log"));

        var startInfo = UnityEditorProcessController.CreateLaunchStartInfo(
            "/opt/unity/Editor/Unity",
            projectPath,
            logPath,
            isLinux: false,
            isNixOs: false,
            findExecutableOnPath: static _ => null,
            readTextFile: static _ => null,
            editorArguments: ["-diagnostic-flag", "value with spaces"]
        );

        await Assert.That(
            startInfo.ArgumentList.SequenceEqual(
                ["-projectPath", projectPath, "-logFile", logPath, "-diagnostic-flag", "value with spaces"]
            )
        ).IsTrue();
    }

    [Test]
    public async Task NixOsLaunchPrefersUnityHubFhsEnvWrapper()
    {
        var editorPath = Path.Combine(Path.GetTempPath(), "Unity", "Editor", "Unity");
        var projectPath = Path.Combine(Path.GetTempPath(), "project");
        var logPath = Path.Combine(projectPath, "Logs", "Editor.log");
        const string unityHubPath = "/run/current-system/sw/bin/unityhub";
        const string unityHubFhsEnvPath = "/nix/store/hash-unityhub-fhs-env-3.16.2/bin/unityhub-fhs-env";
        const string steamRunPath = "/run/current-system/sw/bin/steam-run";
        const string setsidPath = "/run/current-system/sw/bin/setsid";
        const string bashPath = "/run/current-system/sw/bin/bash";

        var startInfo = UnityEditorProcessController.CreateLaunchStartInfo(
            editorPath,
            projectPath,
            logPath,
            isLinux: true,
            isNixOs: true,
            findExecutableOnPath: FindExecutableOnPath,
            readTextFile: static path => path == unityHubPath
                ? $"exec -a \"unityhub\" \"{unityHubFhsEnvPath}\" /nix/store/hash-unityhub-bin \"$@\""
                : null
        );

        await Assert.That(startInfo.FileName).IsEqualTo(setsidPath);
        await Assert.That(startInfo.Arguments).IsEmpty();
        await Assert.That(startInfo.ArgumentList[0]).IsEqualTo(bashPath);
        await Assert.That(startInfo.ArgumentList[1]).IsEqualTo("-c");
        await Assert.That(startInfo.ArgumentList[2]).StartsWith("exec </dev/null >/dev/null 2>&1;");
        await Assert.That(startInfo.ArgumentList[3]).IsEqualTo("conduit-unity-launch");
        await Assert.That(startInfo.ArgumentList[4]).IsEqualTo(unityHubFhsEnvPath);
        await Assert.That(startInfo.ArgumentList[5]).IsEqualTo(editorPath);
        await Assert.That(startInfo.ArgumentList[6]).IsEqualTo("-projectPath");
        await Assert.That(startInfo.ArgumentList[7]).IsEqualTo(projectPath);
        await Assert.That(startInfo.ArgumentList[8]).IsEqualTo("-logFile");
        await Assert.That(startInfo.ArgumentList[9]).IsEqualTo(logPath);
        await Assert.That(startInfo.UseShellExecute).IsFalse();

        static string? FindExecutableOnPath(string executableName) =>
            executableName switch
            {
                "unityhub" => unityHubPath,
                "steam-run" => steamRunPath,
                "setsid" => setsidPath,
                "bash" => bashPath,
                _ => null,
            };
    }

    [Test]
    public async Task NixOsLaunchFallsBackToSteamRunWhenUnityHubFhsEnvIsUnavailable()
    {
        var editorPath = Path.Combine(Path.GetTempPath(), "Unity", "Editor", "Unity");
        var projectPath = Path.Combine(Path.GetTempPath(), "project");
        var logPath = Path.Combine(projectPath, "Logs", "Editor.log");
        const string steamRunPath = "/run/current-system/sw/bin/steam-run";
        const string setsidPath = "/run/current-system/sw/bin/setsid";
        const string bashPath = "/run/current-system/sw/bin/bash";

        var startInfo = UnityEditorProcessController.CreateLaunchStartInfo(
            editorPath,
            projectPath,
            logPath,
            isLinux: true,
            isNixOs: true,
            findExecutableOnPath: static executableName => executableName switch
            {
                "steam-run" => steamRunPath,
                "setsid" => setsidPath,
                "bash" => bashPath,
                _ => null,
            },
            readTextFile: static _ => null
        );

        await Assert.That(startInfo.FileName).IsEqualTo(setsidPath);
        await Assert.That(startInfo.Arguments).IsEmpty();
        await Assert.That(startInfo.ArgumentList[0]).IsEqualTo(bashPath);
        await Assert.That(startInfo.ArgumentList[2]).StartsWith("exec </dev/null >/dev/null 2>&1;");
        await Assert.That(startInfo.ArgumentList[4]).IsEqualTo(steamRunPath);
        await Assert.That(startInfo.ArgumentList[5]).IsEqualTo(editorPath);
        await Assert.That(startInfo.ArgumentList[6]).IsEqualTo("-projectPath");
        await Assert.That(startInfo.ArgumentList[7]).IsEqualTo(projectPath);
        await Assert.That(startInfo.ArgumentList[8]).IsEqualTo("-logFile");
        await Assert.That(startInfo.ArgumentList[9]).IsEqualTo(logPath);
        await Assert.That(startInfo.UseShellExecute).IsFalse();
    }

    [Test]
    public async Task NonNixLinuxLaunchUsesDetachedEditorWithExplicitEnvironment()
    {
        var editorPath = Path.Combine(Path.GetTempPath(), "Unity", "Editor", "Unity");
        var projectPath = Path.Combine(Path.GetTempPath(), "project");
        var logPath = Path.Combine(projectPath, "Logs", "Editor.log");
        const string setsidPath = "/run/current-system/sw/bin/setsid";
        const string bashPath = "/run/current-system/sw/bin/bash";

        var startInfo = UnityEditorProcessController.CreateLaunchStartInfo(
            editorPath,
            projectPath,
            logPath,
            isLinux: true,
            isNixOs: false,
            findExecutableOnPath: static executableName => executableName switch
            {
                "setsid" => setsidPath,
                "bash" => bashPath,
                _ => null,
            },
            readTextFile: static _ => null,
            editorArguments: ["-diagnostic-flag", "value with spaces"]
        );

        await Assert.That(startInfo.FileName).IsEqualTo(setsidPath);
        await Assert.That(startInfo.Arguments).IsEmpty();
        await Assert.That(startInfo.ArgumentList[0]).IsEqualTo(bashPath);
        await Assert.That(startInfo.ArgumentList[1]).IsEqualTo("-c");
        await Assert.That(startInfo.ArgumentList[2]).StartsWith("exec </dev/null >/dev/null 2>&1;");
        await Assert.That(startInfo.ArgumentList[3]).IsEqualTo("conduit-unity-launch");
        await Assert.That(startInfo.ArgumentList[4]).IsEqualTo(editorPath);
        await Assert.That(startInfo.ArgumentList[5]).IsEqualTo("-projectPath");
        await Assert.That(startInfo.ArgumentList[6]).IsEqualTo(projectPath);
        await Assert.That(startInfo.ArgumentList[7]).IsEqualTo("-logFile");
        await Assert.That(startInfo.ArgumentList[8]).IsEqualTo(logPath);
        await Assert.That(startInfo.ArgumentList[9]).IsEqualTo("-diagnostic-flag");
        await Assert.That(startInfo.ArgumentList[10]).IsEqualTo("value with spaces");
        await Assert.That(startInfo.UseShellExecute).IsFalse();
    }

    [Test]
    public async Task NonLinuxLaunchUsesEditorDirectlyThroughShellExecute()
    {
        var editorPath = Path.Combine(Path.GetTempPath(), "Unity", "Editor", "Unity");
        var projectPath = Path.Combine(Path.GetTempPath(), "project");
        var logPath = Path.Combine(projectPath, "Logs", "Editor.log");

        var startInfo = UnityEditorProcessController.CreateLaunchStartInfo(
            editorPath,
            projectPath,
            logPath,
            isLinux: false,
            isNixOs: false,
            findExecutableOnPath: static _ => "/run/current-system/sw/bin/steam-run",
            readTextFile: static _ => null
        );

        await Assert.That(startInfo.FileName).IsEqualTo(editorPath);
        await Assert.That(startInfo.Arguments).IsEmpty();
        await Assert.That(
            startInfo.ArgumentList.SequenceEqual(
                ["-projectPath", projectPath, "-logFile", logPath]
            )
        ).IsTrue();
        await Assert.That(startInfo.UseShellExecute).IsTrue();
    }

}
