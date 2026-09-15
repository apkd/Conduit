namespace Conduit;

public sealed class UnityProjectStatusFormatterTests
{
    [Test]
    public async Task PingSnapshotParserReadsEditorLogPath()
    {
        var parsed = UnityPingSnapshotParser.TryParse(
            """
            {
              "unity_version": "6000.5.0f1",
              "editor_log_path": "/tmp/Unity/Editor.log"
            }
            """,
            out var snapshot
        );

        await Assert.That(parsed).IsTrue();
        await Assert.That(snapshot.EditorLogPath).IsEqualTo("/tmp/Unity/Editor.log");
    }

    [Test]
    public async Task PingReportIncludesEditorLogPathFromSnapshot()
    {
        var report = UnityProjectStatusFormatter.FormatPingReport(
            new()
            {
                UnityVersion = "6000.5.0f1",
                EditorLogPath = "/tmp/Unity/Editor.log",
            }
        );

        await Assert.That(report).Contains("Editor log: /tmp/Unity/Editor.log");
    }

    [Test]
    public async Task PingReportIncludesFallbackEditorLogPath()
    {
        var report = UnityProjectStatusFormatter.FormatPingReport(
            new() { UnityVersion = "6000.5.0f1" },
            "/tmp/Unity/Fallback.log"
        );

        await Assert.That(report).Contains("Editor log: /tmp/Unity/Fallback.log");
    }

    [Test]
    public async Task PingReportIncludesProfilerStatusLine()
    {
        var report = UnityProjectStatusFormatter.FormatPingReport(
            new()
            {
                UnityVersion = "6000.5.0f1",
                ProfilerStatusLine = "Profiler: not recording",
            }
        );

        await Assert.That(report).Contains("Profiler: not recording");
    }

    [Test]
    public async Task PingReportIncludesActiveRecordingStatusLine()
    {
        var report = UnityProjectStatusFormatter.FormatPingReport(
            new()
            {
                UnityVersion = "6000.4.5f1",
                RecordingStatusLine = "Recording: game_view -> Library/Recordings/0.mp4 (1.0/3.0s, libx265)",
            }
        );

        await Assert.That(report).Contains("Recording: game_view -> Library/Recordings/0.mp4");
    }

    [Test]
    public async Task PingReportShowsActiveTestRun()
    {
        var report = UnityProjectStatusFormatter.FormatPingReport(
            new()
            {
                UnityVersion = "6000.5.0f1",
                EditorMode = "play mode",
                IsTestRunnerActive = true,
                ActiveTestMode = "play mode",
            }
        );

        await Assert.That(report).Contains("Status: play mode (running play mode tests...)");
    }

    [Test]
    public async Task PingFailureIncludesEditorLogPath()
    {
        var snapshot = new UnityProjectEnvironmentSnapshot(
            "/tmp/SampleProject",
            isUnityProject: true,
            editorVersion: "6000.5.0f1",
            lockfileState: UnityProjectLockfileState.Missing,
            runningUnityProcessCount: 0,
            matchedProcess: null
        );

        var report = UnityProjectStatusFormatter.FormatPingFailure(
            snapshot,
            ToolExecutionResult.NotConnected(snapshot.ProjectPath),
            processRuntime: null,
            compilationDiagnostics: CompilationDiagnosticSummary.Empty,
            editorLogPath: "/tmp/Unity/Editor.log"
        );

        await Assert.That(report).Contains("Editor log: /tmp/Unity/Editor.log");
    }

    [Test]
    [Arguments(BridgeIdleCloseMarker.Diagnostic, "Status")]
    [Arguments(UnityProjectOfflinePreflight.OfflineDiagnostic, "Diagnostic")]
    public async Task PingFailureIncludesReasonWithoutReplacingReport(string diagnostic, string field)
    {
        var snapshot = new UnityProjectEnvironmentSnapshot(
            "/tmp/SampleProject",
            isUnityProject: true,
            editorVersion: "6000.5.0f1",
            lockfileState: UnityProjectLockfileState.Missing,
            runningUnityProcessCount: 0,
            matchedProcess: null
        );
        const string editorLogPath = "/tmp/Unity/Editor.log";
        const string compilationError = "Script compilation failed.";

        var report = UnityProjectStatusFormatter.FormatPingFailure(
            snapshot,
            ToolExecutionResult.NotConnected(snapshot.ProjectPath, diagnostic),
            processRuntime: null,
            compilationDiagnostics: new(1, 0, compilationError, null),
            editorLogPath
        );

        await Assert.That(report).Contains($"Project: {snapshot.ProjectPath}");
        await Assert.That(report).Contains($"Editor log: {editorLogPath}");
        await Assert.That(report).Contains(compilationError);
        await Assert.That(report).Contains($"{field}: {diagnostic}");
        await Assert.That(report.Split('\n').Count(line => line.Contains(diagnostic, StringComparison.Ordinal))).IsEqualTo(1);
    }
}
