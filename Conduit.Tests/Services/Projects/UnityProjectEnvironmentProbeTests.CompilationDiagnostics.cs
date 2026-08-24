namespace Conduit;

public sealed partial class UnityProjectEnvironmentProbeTests
{
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

        var resolved = UnityEditorLogProbe.ResolveEditorLogPath(
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
    public async Task RememberedEditorLogPathOnlyAppliesToTheEditorThatReportedIt()
    {
        var projectPath = CreateProjectPath();
        var cachedLogPath = Path.Combine(projectPath, "Logs", "Cached.log");
        var expectedLogPath = Path.GetFullPath(Path.Combine(projectPath, "Logs", "Editor.log"));
        var inspector = new UnityProjectEnvironmentInspector();
        inspector.RememberEditorLogPath(projectPath, cachedLogPath, processId: 1234);

        var snapshot = new UnityProjectEnvironmentSnapshot(
            ProjectPathNormalizer.Normalize(projectPath),
            isUnityProject: true,
            editorVersion: "6000.5.0f1",
            lockfileState: UnityProjectLockfileState.Locked,
            runningUnityProcessCount: 1,
            matchedProcess: new(
                ProcessId: 5678,
                ExecutablePath: null,
                CommandLine: $"-projectPath \"{projectPath}\""
            )
        );

        await Assert.That(inspector.ResolveEditorLogPath(snapshot)).IsEqualTo(expectedLogPath);
        await Assert.That(
                inspector.ResolveEditorLogPath(
                    new(
                        snapshot.ProjectPath,
                        snapshot.IsUnityProject,
                        snapshot.EditorVersion,
                        UnityProjectLockfileState.Missing,
                        runningUnityProcessCount: 0,
                        matchedProcess: null
                    )
                )
            )
            .IsEqualTo(expectedLogPath);
    }

    [Test]
    public async Task ConfiguredEditorLogPathOverridesRememberedStatusPath()
    {
        var projectPath = CreateProjectPath();
        var configuredLogPath = Path.Combine(projectPath, "Logs", "Configured.log");
        var inspector = new UnityProjectEnvironmentInspector();
        inspector.RememberEditorLogPath(
            projectPath,
            Path.Combine(projectPath, "Logs", "Status.log"),
            processId: 1234
        );

        var snapshot = new UnityProjectEnvironmentSnapshot(
            ProjectPathNormalizer.Normalize(projectPath),
            isUnityProject: true,
            editorVersion: "6000.4.0f1",
            lockfileState: UnityProjectLockfileState.Locked,
            runningUnityProcessCount: 1,
            matchedProcess: new(
                ProcessId: 1234,
                ExecutablePath: null,
                CommandLine: $"-projectPath \"{projectPath}\" -logFile \"{configuredLogPath}\""
            )
        );

        await Assert.That(inspector.ResolveEditorLogPath(snapshot))
            .IsEqualTo(Path.GetFullPath(configuredLogPath));
    }

    [Test]
    public async Task RememberedEditorLogPathFeedsOfflineStatusDiagnostics()
    {
        const string compilationError =
            "Assets/Broken.cs(4,8): error CS0103: The name 'Missing' does not exist in the current context";
        var projectPath = CreateProjectPath();
        var logPath = CreateTempLog(
            $"""
             ## Script Compilation Error
             {compilationError}
             """
        );

        try
        {
            var inspector = new UnityProjectEnvironmentInspector();
            inspector.RememberEditorLogPath(projectPath, logPath, processId: 1234);
            var snapshot = new UnityProjectEnvironmentSnapshot(
                ProjectPathNormalizer.Normalize(projectPath),
                isUnityProject: true,
                editorVersion: "6000.4.0f1",
                lockfileState: UnityProjectLockfileState.Missing,
                runningUnityProcessCount: 0,
                matchedProcess: null
            );

            var report = inspector.FormatPingFailure(
                snapshot,
                ToolExecutionResult.NotConnected(projectPath)
            );

            await Assert.That(report).Contains($"Editor log: {logPath}");
            await Assert.That(report).Contains(compilationError);
        }
        finally
        {
            DeleteTempLog(logPath);
        }
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
            var diagnostics = new UnityCompilationDiagnosticsReader().ReadLatestCompilationDiagnostics(logPath);
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
            var diagnostics = new UnityCompilationDiagnosticsReader().ReadLatestCompilationDiagnostics(logPath);
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
    public async Task ReadCompilationDiagnosticsTracksAppendedBlocksWithoutRetainingOldDiagnostics()
    {
        const string compilationError = "Assets/Broken.cs(1,1): error CS0001: Broken";
        const string compilationWarning = "Assets/Fixed.cs(2,2): warning CS0002: Warning";
        var logPath = CreateTempLog($"## Script Compilation Error\n{compilationError}\n");

        try
        {
            var reader = new UnityCompilationDiagnosticsReader();
            var initial = reader.ReadLatestCompilationDiagnostics(logPath);
            File.AppendAllText(logPath, "Unrelated editor activity\n");
            var unchanged = reader.ReadLatestCompilationDiagnostics(logPath);
            File.AppendAllText(
                logPath,
                $"*** Tundra build finished\n{compilationWarning}\n"
            );
            var latest = reader.ReadLatestCompilationDiagnostics(logPath);

            await Assert.That(initial.ErrorText).IsEqualTo(compilationError);
            await Assert.That(unchanged.ErrorText).IsEqualTo(compilationError);
            await Assert.That(latest.ErrorCount).IsEqualTo(0);
            await Assert.That(latest.WarningText).IsEqualTo(compilationWarning);
        }
        finally
        {
            DeleteTempLog(logPath);
        }
    }

    [Test]
    public async Task ReadCompilationDiagnosticsRechecksAnIncompleteTrailingLine()
    {
        var logPath = CreateTempLog("## Script Compilation Error\nAssets/Broken.cs(1,1): err");

        try
        {
            var reader = new UnityCompilationDiagnosticsReader();
            var incomplete = reader.ReadLatestCompilationDiagnostics(logPath);
            File.AppendAllText(logPath, "or CS0001: Broken\n");
            var completed = reader.ReadLatestCompilationDiagnostics(logPath);

            await Assert.That(incomplete.ErrorCount).IsEqualTo(0);
            await Assert.That(completed.ErrorCount).IsEqualTo(1);
            await Assert.That(completed.ErrorText)
                .IsEqualTo("Assets/Broken.cs(1,1): error CS0001: Broken");
        }
        finally
        {
            DeleteTempLog(logPath);
        }
    }

    [Test]
    public async Task ReadCompilationDiagnosticsDetectsSameLengthRewriteWithUnchangedTimestamp()
    {
        const string initialLog = "## Script Compilation Error\nAssets/A.cs(1,1): error CS0001: Broken\n";
        const string rewrittenLog = "## Script Compilation Error\nAssets/A.cs(1,1): error CS0001: Fixed!\n";
        var logPath = CreateTempLog(initialLog);

        try
        {
            var reader = new UnityCompilationDiagnosticsReader();
            var initial = reader.ReadLatestCompilationDiagnostics(logPath);
            var timestamp = File.GetLastWriteTimeUtc(logPath);
            File.WriteAllText(logPath, rewrittenLog);
            File.SetLastWriteTimeUtc(logPath, timestamp);

            var rewritten = reader.ReadLatestCompilationDiagnostics(logPath);

            await Assert.That(initial.ErrorText).Contains("Broken");
            await Assert.That(rewritten.ErrorText).Contains("Fixed!");
        }
        finally
        {
            DeleteTempLog(logPath);
        }
    }

}
