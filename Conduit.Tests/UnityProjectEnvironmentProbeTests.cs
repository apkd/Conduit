using JetBrains.Annotations;

namespace Conduit;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class UnityProjectEnvironmentProbeTests
{
    [Test]
    [Arguments("Enter Safe Mode?", true)]
    [Arguments("UNITY - SAFE MODE", true)]
    [Arguments("Exit Safe Mode", true)]
    [Arguments("Unity", false)]
    [Arguments("", false)]
    public async Task SafeModeWindowTitleRecognizesSafeModeText(string title, bool expected)
    {
        await Assert.That(SafeModeWindowProbe.IsSafeModeWindowTitle(title)).IsEqualTo(expected);
    }

    [Test]
    public async Task HyprlandSafeModeSignalMatchesTargetPidTitle()
    {
        const string json =
            """
            [
              { "pid": 1234, "class": "Unity", "title": "Unity", "initialTitle": "Unity" },
              { "pid": 1234, "class": "Unity", "title": "Enter Safe Mode?", "initialTitle": "Enter Safe Mode?" },
              { "pid": 5678, "class": "Unity", "title": "Enter Safe Mode?", "initialTitle": "Enter Safe Mode?" }
            ]
            """;

        var title = SafeModeWindowProbe.TryReadHyprlandClientsSafeModeWindowSignal(json, 1234);

        await Assert.That(title).IsEqualTo("Enter Safe Mode?");
    }

    [Test]
    public async Task HyprlandSafeModeSignalIgnoresOtherPids()
    {
        const string json =
            """
            [
              { "pid": 5678, "class": "Unity", "title": "Enter Safe Mode?", "initialTitle": "Enter Safe Mode?" }
            ]
            """;

        var title = SafeModeWindowProbe.TryReadHyprlandClientsSafeModeWindowSignal(json, 1234);

        await Assert.That(title).IsNull();
    }

    [Test]
    public async Task HyprlandSafeModeSignalUsesInitialTitle()
    {
        const string json =
            """
            [
              { "pid": 1234, "class": "Unity", "title": "Unity", "initialTitle": "Enter Safe Mode?" }
            ]
            """;

        var title = SafeModeWindowProbe.TryReadHyprlandClientsSafeModeWindowSignal(json, 1234);

        await Assert.That(title).IsEqualTo("Enter Safe Mode?");
    }

    [Test]
    public async Task SwaySafeModeSignalMatchesTargetPidName()
    {
        const string json =
            """
            {
              "nodes": [
                { "pid": 5678, "name": "Enter Safe Mode?" },
                {
                  "nodes": [
                    { "pid": 1234, "name": "Enter Safe Mode?" }
                  ]
                }
              ]
            }
            """;

        var title = SafeModeWindowProbe.TryReadSwayTreeSafeModeWindowSignal(json, 1234);

        await Assert.That(title).IsEqualTo("Enter Safe Mode?");
    }

    [Test]
    public async Task SwaySafeModeSignalMatchesFloatingWindowPropertyTitle()
    {
        const string json =
            """
            {
              "nodes": [],
              "floating_nodes": [
                {
                  "pid": 1234,
                  "name": "Unity",
                  "window_properties": {
                    "title": "Enter Safe Mode?",
                    "class": "Unity"
                  }
                }
              ]
            }
            """;

        var title = SafeModeWindowProbe.TryReadSwayTreeSafeModeWindowSignal(json, 1234);

        await Assert.That(title).IsEqualTo("Enter Safe Mode?");
    }

    [Test]
    public async Task SwaySafeModeSignalIgnoresOtherPids()
    {
        const string json =
            """
            {
              "nodes": [
                { "pid": 5678, "name": "Enter Safe Mode?" }
              ]
            }
            """;

        var title = SafeModeWindowProbe.TryReadSwayTreeSafeModeWindowSignal(json, 1234);

        await Assert.That(title).IsNull();
    }

    [Test]
    public async Task NiriSafeModeSignalMatchesTargetPidTitle()
    {
        const string json =
            """
            [
              { "id": 1, "pid": 5678, "title": "Enter Safe Mode?", "app_id": "Unity" },
              { "id": 2, "pid": 1234, "title": "Enter Safe Mode?", "app_id": "Unity" }
            ]
            """;

        var title = SafeModeWindowProbe.TryReadNiriWindowsSafeModeWindowSignal(json, 1234);

        await Assert.That(title).IsEqualTo("Enter Safe Mode?");
    }

    [Test]
    public async Task NiriSafeModeSignalHandlesRawSocketResponse()
    {
        const string json =
            """
            {
              "Ok": {
                "Windows": [
                  { "id": 2, "pid": 1234, "title": "Enter Safe Mode?", "app_id": "Unity" }
                ]
              }
            }
            """;

        var title = SafeModeWindowProbe.TryReadNiriWindowsSafeModeWindowSignal(json, 1234);

        await Assert.That(title).IsEqualTo("Enter Safe Mode?");
    }

    [Test]
    public async Task NiriSafeModeSignalIgnoresOtherPids()
    {
        const string json =
            """
            [
              { "id": 1, "pid": 5678, "title": "Enter Safe Mode?", "app_id": "Unity" }
            ]
            """;

        var title = SafeModeWindowProbe.TryReadNiriWindowsSafeModeWindowSignal(json, 1234);

        await Assert.That(title).IsNull();
    }

    [Test]
    public async Task ResolveExecutablePathPrefersPrimaryPath()
    {
        var primaryDirectory = Directory.CreateTempSubdirectory("conduit-primary-path-");
        var fallbackDirectory = Directory.CreateTempSubdirectory("conduit-fallback-path-");
        try
        {
            var primaryExecutable = Path.Combine(primaryDirectory.FullName, "hyprctl");
            await File.WriteAllTextAsync(primaryExecutable, string.Empty);
            await File.WriteAllTextAsync(Path.Combine(fallbackDirectory.FullName, "hyprctl"), string.Empty);

            var resolved = SafeModeWindowProbe.ResolveExecutablePath(
                "hyprctl",
                primaryDirectory.FullName,
                fallbackDirectory.FullName
            );

            await Assert.That(resolved).IsEqualTo(primaryExecutable);
        }
        finally
        {
            Directory.Delete(primaryDirectory.FullName, recursive: true);
            Directory.Delete(fallbackDirectory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task TryFindSwaySocketPrefersExplicitSocket()
    {
        var runtimeDirectory = Directory.CreateTempSubdirectory("conduit-sway-runtime-");
        try
        {
            var discoveredSocket = Path.Combine(runtimeDirectory.FullName, "sway-ipc.1000.1.sock");
            var explicitSocket = Path.Combine(runtimeDirectory.FullName, "explicit.sock");
            await File.WriteAllTextAsync(discoveredSocket, string.Empty);
            await File.WriteAllTextAsync(explicitSocket, string.Empty);

            var socket = SafeModeWindowProbe.TryFindSwaySocket(runtimeDirectory.FullName, explicitSocket);

            await Assert.That(socket).IsEqualTo(explicitSocket);
        }
        finally
        {
            Directory.Delete(runtimeDirectory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task TryFindSwaySocketDiscoversRuntimeSocket()
    {
        var runtimeDirectory = Directory.CreateTempSubdirectory("conduit-sway-runtime-");
        try
        {
            var socketPath = Path.Combine(runtimeDirectory.FullName, "sway-ipc.1000.1.sock");
            await File.WriteAllTextAsync(socketPath, string.Empty);

            var socket = SafeModeWindowProbe.TryFindSwaySocket(runtimeDirectory.FullName);

            await Assert.That(socket).IsEqualTo(socketPath);
        }
        finally
        {
            Directory.Delete(runtimeDirectory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task TryFindNiriSocketPrefersExplicitSocket()
    {
        var runtimeDirectory = Directory.CreateTempSubdirectory("conduit-niri-runtime-");
        try
        {
            var discoveredSocket = Path.Combine(runtimeDirectory.FullName, "niri.wayland-1.1000.sock");
            var explicitSocket = Path.Combine(runtimeDirectory.FullName, "explicit.sock");
            await File.WriteAllTextAsync(discoveredSocket, string.Empty);
            await File.WriteAllTextAsync(explicitSocket, string.Empty);

            var socket = SafeModeWindowProbe.TryFindNiriSocket(runtimeDirectory.FullName, "wayland-1", explicitSocket);

            await Assert.That(socket).IsEqualTo(explicitSocket);
        }
        finally
        {
            Directory.Delete(runtimeDirectory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task TryFindNiriSocketDiscoversWaylandDisplaySocket()
    {
        var runtimeDirectory = Directory.CreateTempSubdirectory("conduit-niri-runtime-");
        try
        {
            var socketPath = Path.Combine(runtimeDirectory.FullName, "niri.wayland-1.1000.sock");
            await File.WriteAllTextAsync(socketPath, string.Empty);
            await File.WriteAllTextAsync(Path.Combine(runtimeDirectory.FullName, "niri.wayland-2.1000.sock"), string.Empty);

            var socket = SafeModeWindowProbe.TryFindNiriSocket(runtimeDirectory.FullName, "wayland-1");

            await Assert.That(socket).IsEqualTo(socketPath);
        }
        finally
        {
            Directory.Delete(runtimeDirectory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task TryInferHyprlandInstanceSignatureUsesSocketDirectory()
    {
        var runtimeDirectory = Directory.CreateTempSubdirectory("conduit-runtime-");
        try
        {
            var instanceDirectory = Directory.CreateDirectory(
                Path.Combine(runtimeDirectory.FullName, "hypr", "instance_1")
            );
            await File.WriteAllTextAsync(Path.Combine(instanceDirectory.FullName, ".socket.sock"), string.Empty);

            var signature = SafeModeWindowProbe.TryInferHyprlandInstanceSignature(runtimeDirectory.FullName);

            await Assert.That(signature).IsEqualTo("instance_1");
        }
        finally
        {
            Directory.Delete(runtimeDirectory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task HasConduitPackageSignalReturnsFalseWhenPackageIsAbsent()
    {
        var projectPath = CreateTempProject();
        try
        {
            var probe = new UnityProjectEnvironmentProbe();

            await Assert.That(probe.HasConduitPackageSignal(projectPath)).IsFalse();
        }
        finally
        {
            Directory.Delete(projectPath, recursive: true);
        }
    }

    [Test]
    public async Task HasConduitPackageSignalDetectsManifestDependency()
    {
        var projectPath = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectPath, "Packages"));
            await File.WriteAllTextAsync(
                Path.Combine(projectPath, "Packages", "manifest.json"),
                """
                {
                  "dependencies": {
                    "dev.tryfinally.conduit": "https://github.com/apkd/Conduit.git?path=/Conduit.Unity#release"
                  }
                }
                """
            );
            var probe = new UnityProjectEnvironmentProbe();

            await Assert.That(probe.HasConduitPackageSignal(projectPath)).IsTrue();
        }
        finally
        {
            Directory.Delete(projectPath, recursive: true);
        }
    }

    [Test]
    public async Task HasConduitPackageSignalDetectsEmbeddedPackage()
    {
        var projectPath = CreateTempProject();
        try
        {
            var embeddedPackagePath = Path.Combine(projectPath, "Packages", "dev.tryfinally.conduit");
            Directory.CreateDirectory(embeddedPackagePath);
            await File.WriteAllTextAsync(Path.Combine(embeddedPackagePath, "package.json"), "{}");
            var probe = new UnityProjectEnvironmentProbe();

            await Assert.That(probe.HasConduitPackageSignal(projectPath)).IsTrue();
        }
        finally
        {
            Directory.Delete(projectPath, recursive: true);
        }
    }

    [Test]
    [Arguments("6000.4.0f1", "-projectPath \"{project}\" -logFile \"{absolute}\"", "{absolute}")]
    [Arguments("6000.4.0f1", "-projectPath \"{project}\" -logFile Logs/Custom.log", "{project}/Logs/Custom.log")]
    [Arguments("6000.4.0f1", null, "{legacy}")]
    [Arguments("6000.5.0f1", null, "{project}/Logs/Editor.log")]
    [Arguments("6000.4.0f1", "-projectPath \"{project}\" -logFile -", null)]
    public async Task ResolveEditorLogPathResolvesConfiguredAndDefaultLocations(
        string unityVersion,
        string? commandLineTemplate,
        string? expectedPathTemplate
    )
    {
        var projectPath = CreateProjectPath();
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
        var legacyLogPath = Path.Combine(Path.GetTempPath(), "unity-editor.log");
        var absoluteLogPath = Path.GetFullPath(Path.Combine(projectPath, "CustomLogs", "Editor.log"));

        var resolved = UnityProjectEnvironmentProbe.ResolveEditorLogPath(
            normalizedProjectPath,
            unityVersion,
            ReplaceTokens(commandLineTemplate, projectPath, legacyLogPath, absoluteLogPath),
            legacyLogPath
        );

        await Assert.That(resolved)
            .IsEqualTo(
                ReplaceTokens(expectedPathTemplate, projectPath, legacyLogPath, absoluteLogPath)
            );
    }

    [Test]
    public async Task ReadCompilationDiagnosticsDeduplicatesRepeatedErrorsInLatestBlock()
    {
        const string duplicateError = @"Assets\ConduitManagedFieldBurstJob.cs(4,8): error CS1029: #error: 'CONDUIT_INTENTIONAL_SAFE_MODE_TEST'";
        const string distinctError = @"Assets\Other.cs(10,12): error CS0103: The name 'Missing' does not exist in the current context";
        var logPath = CreateTempLog(
            $$"""
            ## Script Compilation Error
            {{duplicateError}}
            *** Tundra build failed (0.66 seconds), 2 items updated, 636 evaluated
            ## Script Compilation Error
            {{duplicateError}}
            {{distinctError}}
            {{duplicateError}}
            """
        );

        try
        {
            var diagnostics = new UnityProjectEnvironmentProbe().ReadLatestCompilationDiagnostics(logPath);
            var lines = Lines(diagnostics.ErrorText);

            await Assert.That(diagnostics.ErrorCount).IsEqualTo(2);
            await Assert.That(lines.Length).IsEqualTo(2);
            await Assert.That(lines[0]).IsEqualTo(duplicateError);
            await Assert.That(lines[1]).IsEqualTo(distinctError);
        }
        finally
        {
            DeleteTempLog(logPath);
        }
    }

    [Test]
    public async Task ReadCompilationDiagnosticsDeduplicatesRepeatedWarningsInLatestBlock()
    {
        const string duplicateWarning = @"Assets\Foo.cs(2,16): warning CS0168: The variable 'exception' is declared but never used";
        const string distinctWarning = @"Assets\Bar.cs(5,13): warning CS0219: The variable 'unused' is assigned but its value is never used";
        var logPath = CreateTempLog(
            $$"""
            ## Script Compilation Warning
            {{duplicateWarning}}
            {{duplicateWarning}}
            {{distinctWarning}}
            """
        );

        try
        {
            var diagnostics = new UnityProjectEnvironmentProbe().ReadLatestCompilationDiagnostics(logPath);
            var lines = Lines(diagnostics.WarningText);

            await Assert.That(diagnostics.WarningCount).IsEqualTo(2);
            await Assert.That(lines.Length).IsEqualTo(2);
            await Assert.That(lines[0]).IsEqualTo(duplicateWarning);
            await Assert.That(lines[1]).IsEqualTo(distinctWarning);
        }
        finally
        {
            DeleteTempLog(logPath);
        }
    }

    [Test]
    public async Task ResolveUnityEditorPathUsesMatchedProcessPathFirst()
    {
        var processPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Unity-from-process"));
        var overridePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Unity-from-env"));
        var existingPaths = new HashSet<string> { processPath, overridePath };

        var resolved = UnityProjectEnvironmentProbe.ResolveUnityEditorPath(
            "6000.4.0f1",
            processPath,
            overridePath,
            null,
            userHome: "",
            programFiles: "",
            isWindows: false,
            isLinux: true,
            fileExists: existingPaths.Contains
        );

        await Assert.That(resolved).IsEqualTo(processPath);
    }

    [Test]
    public async Task ResolveUnityEditorPathUsesConduitOverrideBeforeUnityOverride()
    {
        var conduitOverride = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Unity-conduit-env"));
        var unityOverride = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Unity-generic-env"));
        var existingPaths = new HashSet<string> { conduitOverride, unityOverride };

        var resolved = UnityProjectEnvironmentProbe.ResolveUnityEditorPath(
            "6000.4.0f1",
            processPath: null,
            conduitOverride,
            unityOverride,
            userHome: "",
            programFiles: "",
            isWindows: false,
            isLinux: true,
            fileExists: existingPaths.Contains
        );

        await Assert.That(resolved).IsEqualTo(conduitOverride);
    }

    [Test]
    public async Task ResolveUnityEditorPathFindsLinuxHubInstall()
    {
        const string unityVersion = "6000.4.5f1";
        var userHome = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"conduit-home-{Guid.NewGuid():N}"));
        var expectedPath = Path.Combine(userHome, "Unity", "Hub", "Editor", unityVersion, "Editor", "Unity");

        var resolved = UnityProjectEnvironmentProbe.ResolveUnityEditorPath(
            unityVersion,
            processPath: null,
            conduitEditorOverride: null,
            unityEditorOverride: null,
            userHome,
            programFiles: "",
            isWindows: false,
            isLinux: true,
            fileExists: path => path == expectedPath
        );

        await Assert.That(resolved).IsEqualTo(expectedPath);
    }

    static string CreateProjectPath()
        => Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"conduit-project-{Guid.NewGuid():N}"));

    static string CreateTempProject()
    {
        var projectPath = CreateProjectPath();
        Directory.CreateDirectory(Path.Combine(projectPath, "Assets"));
        Directory.CreateDirectory(Path.Combine(projectPath, "Packages"));
        Directory.CreateDirectory(Path.Combine(projectPath, "ProjectSettings"));
        File.WriteAllText(
            Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt"),
            "m_EditorVersion: 6000.4.0f1"
        );
        return projectPath;
    }

    static string CreateTempLog(string content)
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "Conduit.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        var logPath = Path.Combine(directoryPath, "Editor.log");
        File.WriteAllText(logPath, content);
        return logPath;
    }

    static void DeleteTempLog(string logPath)
    {
        var directoryPath = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath))
            Directory.Delete(directoryPath, recursive: true);
    }

    static string? ReplaceTokens(string? value, string projectPath, string legacyLogPath, string absoluteLogPath)
    {
        if (value is null)
            return null;

        return value
            .Replace("{project}", projectPath, StringComparison.Ordinal)
            .Replace("{legacy}", legacyLogPath, StringComparison.Ordinal)
            .Replace("{absolute}", absoluteLogPath, StringComparison.Ordinal)
            .Replace('/', Path.DirectorySeparatorChar);
    }

    static string[] Lines(string? text) =>
        text?.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [];
}
