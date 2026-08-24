#nullable enable

using System.Threading.Tasks;
using UnityEngine;

namespace Conduit
{
    static class ConduitToolRunner
    {
        static readonly CommandScheduler scheduler = new();
        internal const int ReimportIdleSettleUpdates = AssetImportMonitor.IdleSettleUpdates;
        static bool initialized;

        internal static void Initialize()
        {
            if (initialized)
                return;

            initialized = true;
            ConduitOpenSceneDiskChangeGuard.Initialize();
            scheduler.Initialize();
        }

        internal static Task OnConnectedAsync()
        {
            scheduler.EnqueueConnected();
            return Task.CompletedTask;
        }

        internal static string? GetActiveCommandType()
            => scheduler.ActiveCommandType;

        internal static bool IsTestRunnerActive()
            => scheduler.IsTestRunnerActive();

        internal static string? GetActiveTestRunMode()
            => scheduler.GetActiveTestRunMode();

        internal static bool HasOutstandingClientWork(int clientId)
            => scheduler.HasOutstandingClientWork(clientId);

        internal static bool HasReconnectableWorkForAnyClient()
            => scheduler.HasReconnectableWorkForAnyClient();

        internal static void PumpQueuedCommands()
        {
            Initialize();
            scheduler.Pump();
        }

        internal static void HandleIncomingCommand(int clientId, BridgeMessage message)
        {
            Initialize();
            scheduler.EnqueueIncomingCommand(clientId, message);
        }

        internal static void HandleClientDisconnected(int clientId)
            => scheduler.EnqueueClientDisconnected(clientId);

        internal static void PrepareForAssemblyReload()
            => scheduler.PrepareForAssemblyReload();

        internal static BridgeCommandKind ParseIncomingCommand(string commandType)
            => BridgeCommandKinds.Parse(commandType);

        internal static string BuildAssemblyReloadInterruptionDiagnostic(string commandType)
            => $"'{commandType}' was interrupted by a Unity domain reload; side effects may have occurred. "
               + "The command was not re-executed.";

        internal static bool ShouldWaitForTestRunCompletion(
            bool isTestRunnerActive,
            bool isCompiling,
            bool isUpdating,
            bool isPlaying,
            bool isPlayingOrWillChangePlaymode)
            => UnityTestRunMonitor.ShouldWaitForCompletion(
                isTestRunnerActive,
                isCompiling,
                isUpdating,
                isPlaying,
                isPlayingOrWillChangePlaymode,
                completeDespiteStuckTestRunner: false
            );

        internal static bool ShouldWaitForTestRunCompletion(
            bool isTestRunnerActive,
            bool isCompiling,
            bool isUpdating,
            bool isPlaying,
            bool isPlayingOrWillChangePlaymode,
            bool completeDespiteStuckTestRunner)
            => UnityTestRunMonitor.ShouldWaitForCompletion(
                isTestRunnerActive,
                isCompiling,
                isUpdating,
                isPlaying,
                isPlayingOrWillChangePlaymode,
                completeDespiteStuckTestRunner
            );

        internal static bool ShouldWaitToEnterPlayMode(bool isCompiling, bool isUpdating, bool isPlayingOrWillChangePlaymode)
            => EditorModeTransition.ShouldWaitToEnterPlayMode(isCompiling, isUpdating, isPlayingOrWillChangePlaymode);

        internal static bool ShouldFailEnterPlayForCompileErrors(bool scriptCompilationFailed)
            => EditorModeTransition.ShouldFailEnterPlayForCompileErrors(scriptCompilationFailed);

        internal static string BuildEnterPlayBusyDiagnostic(bool isCompiling, bool isUpdating, bool isPlayingOrWillChangePlaymode)
            => EditorModeTransition.BuildEnterPlayBusyDiagnostic(isCompiling, isUpdating, isPlayingOrWillChangePlaymode);

        internal static string BuildEnterPlayCompileErrorDiagnostic()
            => EditorModeTransition.BuildEnterPlayCompileErrorDiagnostic();

        internal static string BuildPlayCompletionDiagnostic(bool targetPlayMode, bool changedMode, bool isPaused)
            => EditorModeTransition.BuildCompletionDiagnostic(targetPlayMode, changedMode, isPaused);

        internal static bool ShouldBlockReimportForPlayMode(bool isPlaying)
            => AssetImportMonitor.ShouldBlockForPlayMode(isPlaying);

        internal static string BuildReimportPlayModeDiagnostic(string commandType = BridgeCommandTypes.RefreshAssetDatabase)
            => AssetImportMonitor.BuildPlayModeDiagnostic(commandType);

        internal static bool ShouldWaitForReimportIdle(bool refreshReturned, bool isCompiling, bool isUpdating, int idleUpdateCount)
            => AssetImportMonitor.ShouldWaitForIdle(refreshReturned, isCompiling, isUpdating, idleUpdateCount);

        internal static string FormatReimportedAssetFilenames(string? assetPathPayload)
            => AssetImportMonitor.FormatReimportedAssetFilenames(assetPathPayload);

        internal static string BuildRestoredReimportCompileErrorDiagnostic()
            => AssetImportMonitor.BuildRestoredCompileErrorDiagnostic();

    }
}
