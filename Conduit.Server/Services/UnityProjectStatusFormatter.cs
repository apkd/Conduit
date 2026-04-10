using Cysharp.Text;

namespace Conduit;

static class UnityProjectStatusFormatter
{
    public static string FormatPingFailure(
        UnityProjectEnvironmentSnapshot snapshot,
        ToolExecutionResult? bridgeResult,
        UnityEditorProcessRuntimeInfo? processRuntime,
        CompilationDiagnosticSummary compilationDiagnostics
    )
    {
        var builder = ZString.CreateStringBuilder();
        try
        {
            builder.Append("Project: ");
            builder.AppendLine(snapshot.ProjectPath);
            if (!snapshot.IsUnityProject)
            {
                builder.Append("Diagnostic: ");
                builder.AppendLine(UnityProjectOfflinePreflight.InvalidProjectDiagnostic);
                return ConduitUtility.FinishText(ref builder);
            }

            builder.Append("Bridge: ");
            builder.AppendLine(FormatBridgeStatus(bridgeResult));
            AppendProcessRuntime(ref builder, processRuntime, snapshot.MatchedProcess?.ProcessId);
            builder.Append("Unity.exe processes running: ");
            builder.AppendLine(snapshot.RunningUnityProcessCount);
            if (!string.IsNullOrWhiteSpace(bridgeResult?.Diagnostic))
            {
                builder.Append("Diagnostic: ");
                builder.AppendLine(bridgeResult.Diagnostic);
            }

            AppendCompilationDiagnosticsFooter(ref builder, compilationDiagnostics);
            return ConduitUtility.FinishText(ref builder);
        }
        finally
        {
            builder.Dispose();
        }
    }

    public static string FormatPingReachable(
        UnityProjectEnvironmentSnapshot snapshot,
        BridgeProjectHandshake handshake,
        UnityEditorProcessRuntimeInfo? processRuntime,
        CompilationDiagnosticSummary compilationDiagnostics,
        string diagnostic
    )
    {
        var builder = ZString.CreateStringBuilder();
        try
        {
            builder.Append("Project: ");
            builder.AppendLine(snapshot.ProjectPath);
            if (!snapshot.IsUnityProject)
            {
                builder.Append("Diagnostic: ");
                builder.AppendLine(UnityProjectOfflinePreflight.InvalidProjectDiagnostic);
            }

            builder.AppendLine("Bridge: reachable");
            if (!string.IsNullOrWhiteSpace(handshake.UnityVersion))
            {
                builder.Append("Unity: ");
                builder.AppendLine(handshake.UnityVersion);
            }

            AppendProcessRuntime(ref builder, processRuntime, handshake.EditorProcessId > 0 ? handshake.EditorProcessId : snapshot.MatchedProcess?.ProcessId);
            builder.Append("Unity.exe processes running: ");
            builder.AppendLine(snapshot.RunningUnityProcessCount);
            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                builder.Append("Diagnostic: ");
                builder.AppendLine(diagnostic);
            }

            AppendCompilationDiagnosticsFooter(ref builder, compilationDiagnostics);
            return ConduitUtility.FinishText(ref builder);
        }
        finally
        {
            builder.Dispose();
        }
    }

    public static string FormatPingReport(UnityPingSnapshot pingSnapshot)
    {
        var builder = ZString.CreateStringBuilder();
        try
        {
            builder.Append("Unity ");
            builder.Append(pingSnapshot.UnityVersion);
            if (!string.IsNullOrWhiteSpace(pingSnapshot.Platform))
            {
                builder.Append(" (");
                builder.Append(pingSnapshot.Platform);
                builder.Append(')');
            }

            builder.AppendLine();
            AppendCachedRuntime(ref builder, pingSnapshot);

            builder.AppendLine("Scenes:");
            if (pingSnapshot.Scenes.Length == 0)
                builder.AppendLine("- none");
            else
                foreach (var scene in pingSnapshot.Scenes)
                {
                    builder.Append("- ");
                    builder.AppendLine(scene);
                }

            if (pingSnapshot.DirtyScenes.Length > 0)
            {
                builder.AppendLine("Dirty Scenes:");
                foreach (var dirtyScene in pingSnapshot.DirtyScenes)
                {
                    builder.Append("- ");
                    builder.AppendLine(dirtyScene);
                }
            }

            builder.Append("Status: ");
            builder.AppendLine(BuildStatusLine(pingSnapshot));
            return ConduitUtility.FinishText(ref builder);
        }
        finally
        {
            builder.Dispose();
        }
    }

    static string FormatBridgeStatus(ToolExecutionResult? bridgeResult) =>
        bridgeResult?.Outcome switch
        {
            null                     => "not attempted",
            ToolOutcome.NotConnected => "unreachable",
            ToolOutcome.Timeout      => "timed out",
            _                        => bridgeResult.Outcome,
        };

    static void AppendProcessRuntime(ref Utf16ValueStringBuilder builder, UnityEditorProcessRuntimeInfo? processRuntime, int? fallbackProcessId)
    {
        var processId = processRuntime?.ProcessId ?? fallbackProcessId;
        if (processId is > 0)
        {
            builder.Append("PID: ");
            builder.AppendLine(processId.Value);
        }

        if (processRuntime is not null)
        {
            builder.Append("Uptime: ");
            builder.AppendLine(ConduitUtility.FormatDuration(DateTimeOffset.UtcNow - processRuntime.StartedAtUtc));
        }
    }

    static void AppendCachedRuntime(ref Utf16ValueStringBuilder builder, UnityPingSnapshot pingSnapshot)
    {
        if (pingSnapshot.EditorProcessId > 0)
        {
            builder.Append("PID: ");
            builder.AppendLine(pingSnapshot.EditorProcessId);
        }

        if (!string.IsNullOrWhiteSpace(pingSnapshot.Uptime))
        {
            builder.Append("Uptime: ");
            builder.AppendLine(pingSnapshot.Uptime);
        }
    }

    static string BuildStatusLine(UnityPingSnapshot pingSnapshot)
    {
        var commandKind = BridgeCommandKinds.Parse(pingSnapshot.ActiveCommandType);
        var detail = BridgeCommandKinds.IsTest(commandKind)
            ? "running tests..."
            : pingSnapshot.IsCompiling
                ? "compiling..."
                : pingSnapshot.IsUpdating || commandKind == BridgeCommandKind.RefreshAssetDatabase
                    ? "importing assets..."
                    : null;

        var mode = string.IsNullOrWhiteSpace(pingSnapshot.EditorMode) ? "edit mode" : pingSnapshot.EditorMode;
        if (!pingSnapshot.IsPaused)
            return detail is null ? mode : $"{mode} ({detail})";

        return detail is null ? $"{mode} (paused)" : $"{mode} (paused, {detail})";
    }

    static void AppendCompilationDiagnosticsFooter(ref Utf16ValueStringBuilder builder, CompilationDiagnosticSummary diagnostics)
    {
        var footer = diagnostics.ErrorText ?? diagnostics.WarningText;
        if (string.IsNullOrWhiteSpace(footer))
            return;

        builder.AppendLine();
        builder.AppendLine(footer);
    }
}
