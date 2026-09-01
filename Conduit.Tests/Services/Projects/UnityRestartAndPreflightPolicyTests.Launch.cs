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

    [Test]
    public async Task SystemdServiceLaunchMovesEditorIntoManagerOwnedService()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "Unity", "Editor");
        const string systemdRunPath = "/run/current-system/sw/bin/systemd-run";
        const string setsidPath = "/run/current-system/sw/bin/setsid";
        const string bashPath = "/run/current-system/sw/bin/bash";
        var startInfo = new ProcessStartInfo(setsidPath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(bashPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("wait \"$child\"");
        startInfo.Environment.Clear();
        startInfo.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/run/user/1000/bus";
        startInfo.Environment["CONDUIT_LITERAL"] = "$HOME";

        var applied = SystemdProcessIsolation.TryApply(
            startInfo,
            "0::/user.slice/user-1000.slice/user@1000.service/app.slice/conduit.service\n",
            systemdRunPath,
            bashPath
        );

        await Assert.That(applied).IsTrue();
        await Assert.That(startInfo.FileName).IsEqualTo(bashPath);
        await Assert.That(startInfo.ArgumentList[1]).Contains("</dev/null >/dev/null 2>&1");
        await Assert.That(startInfo.ArgumentList).Contains("--wait");
        await Assert.That(startInfo.ArgumentList).Contains("--service-type=exec");
        await Assert.That(startInfo.ArgumentList).DoesNotContain("--scope");
        await Assert.That(startInfo.ArgumentList).Contains($"--working-directory={workingDirectory}");
        await Assert.That(startInfo.ArgumentList).Contains("--setenv=CONDUIT_LITERAL=$HOME");
        var commandIndex = startInfo.ArgumentList.IndexOf("--") + 1;
        await Assert.That(startInfo.ArgumentList[commandIndex]).IsEqualTo(setsidPath);
        await Assert.That(startInfo.ArgumentList[commandIndex + 1]).IsEqualTo("--wait");
        await Assert.That(startInfo.ArgumentList[^1]).IsEqualTo("wait \"$$child\"");
    }

    [Test]
    public async Task SystemdServiceDetectionRecognizesTheLeafService()
    {
        var detected = SystemdProcessIsolation.IsServiceCgroup(
            "0::/user.slice/user-1000.slice/user@1000.service/app.slice/conduit.service\n"
        );

        await Assert.That(detected).IsTrue();
    }

    [Test]
    public async Task SystemdServiceDetectionIgnoresAncestorServices()
    {
        var detected = SystemdProcessIsolation.IsServiceCgroup(
            "0::/user.slice/user-1000.slice/user@1000.service/app.slice/terminal.scope\n"
        );

        await Assert.That(detected).IsFalse();
        await Assert.That(SystemdProcessIsolation.IsServiceCgroup("")).IsFalse();
    }

    [Test]
    public async Task SystemdIsolationPreservesDirectLaunchWithoutUserManagerBus()
    {
        const string editorPath = "/opt/unity/Editor/Unity";
        var startInfo = new ProcessStartInfo(editorPath) { UseShellExecute = false };
        startInfo.ArgumentList.Add("-projectPath");
        startInfo.ArgumentList.Add("/tmp/project");
        startInfo.Environment.Clear();

        var applied = SystemdProcessIsolation.TryApply(
            startInfo,
            "0::/user.slice/user-1000.slice/user@1000.service/app.slice/conduit.service\n",
            "/usr/bin/systemd-run",
            "/bin/bash"
        );

        await Assert.That(applied).IsFalse();
        await Assert.That(startInfo.FileName).IsEqualTo(editorPath);
        await Assert.That(startInfo.ArgumentList.SequenceEqual(["-projectPath", "/tmp/project"])).IsTrue();
    }

}
