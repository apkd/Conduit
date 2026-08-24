#nullable enable

using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Conduit
{
    sealed partial class UnityTestRunMonitor
    {
        const int PlayerHeartbeatTimeoutSeconds = 30 * 60;
        readonly ToolLogCapture logCapture;
        readonly Func<BridgeCommandResult, Task> complete;
        readonly Action<BridgeCommandResult> checkpointCompletion;
        readonly TestRunCallbacks callbacks;
        TestRunnerApi? testRunnerApi;
        BridgeCommandKind activeCommandKind;
        string? activeRunGuid;
        BridgeCommandResult? pendingResult;
        bool callbacksRegistered;
        bool completionHooksInstalled;
        bool asyncStartupPending;
        bool pendingCompletionRestored;

        internal UnityTestRunMonitor(
            ToolLogCapture logCapture,
            Func<BridgeCommandResult, Task> complete,
            Action<BridgeCommandResult> checkpointCompletion
        )
        {
            this.logCapture = logCapture;
            this.complete = complete;
            this.checkpointCompletion = checkpointCompletion;
            callbacks = new(this);
        }

        internal void EnsureCallbacksRegistered()
        {
            if (callbacksRegistered)
                return;

            callbacksRegistered = true;
            GetOrCreateTestRunnerApi().RegisterCallbacks(callbacks);
        }

        internal void Start(PendingOperationState operation, BridgeCommandKind commandKind, TestMode mode, bool playerRun)
        {
            EnsureCallbacksRegistered();
            activeCommandKind = commandKind;
            asyncStartupPending = operation.IsAsync;
            pendingCompletionRestored = false;

            if (TryCompleteDirtySceneBlock(operation.CommandType)
                || TryCompleteBusyStartBlock(operation.CommandType)
                || TryCompleteCompileErrorStartBlock(operation.CommandType))
                return;

            var filter = new Filter { testMode = mode };
            RunTestsTool.ApplyFilter(filter, operation.TestFilter);
            if (playerRun)
                filter.targetPlatform = EditorUserBuildSettings.activeBuildTarget;

            if (mode == TestMode.PlayMode && !playerRun)
                ConduitGameView.PrepareForPlayMode();

            var settings = new ExecutionSettings(filter)
            {
                playerHeartbeatTimeout = PlayerHeartbeatTimeoutSeconds,
                overloadTestRunSettings = playerRun
                    ? new PlayerTestRunSettings(
                        EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneLinux64
                    )
                    : null,
            };
            activeRunGuid = GetOrCreateTestRunnerApi().Execute(settings);

            if (operation.IsAsync)
            {
                InstallCompletionHooks();
                TryCompleteAsyncStartup();
            }
        }

        internal void ResumeRestored(BridgeCommandKind commandKind, BridgeCommandResult? restoredPendingResult)
        {
            EnsureCallbacksRegistered();
            activeCommandKind = commandKind;
            activeRunGuid = null;
            pendingResult = restoredPendingResult;
            asyncStartupPending = false;
            pendingCompletionRestored = restoredPendingResult != null;

            if (pendingResult == null)
                return;

            InstallCompletionHooks();
            TryCompletePendingRun();
        }

        internal void Stop()
        {
            RemoveCompletionHooks();
            activeCommandKind = BridgeCommandKind.Unknown;
            activeRunGuid = null;
            pendingResult = null;
            asyncStartupPending = false;
            pendingCompletionRestored = false;
        }

        internal bool Cancel()
        {
            if (!BridgeCommandKinds.IsTest(activeCommandKind)
                || string.IsNullOrEmpty(activeRunGuid)
                || !TestRunnerApi.CancelTestRun(activeRunGuid))
                return false;

            QueueCompletion(
                RunTestsTool.CreateRequestCancelledResult(),
                discardLogs: true
            );
            return true;
        }

        TestRunnerApi GetOrCreateTestRunnerApi()
            => testRunnerApi != null ? testRunnerApi : testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
    }
}
