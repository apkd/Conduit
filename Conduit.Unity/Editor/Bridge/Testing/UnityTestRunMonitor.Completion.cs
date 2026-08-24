#nullable enable

using System;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;

namespace Conduit
{
    sealed partial class UnityTestRunMonitor
    {
        internal static bool ShouldWaitForCompletion(
            bool isTestRunnerActive,
            bool isCompiling,
            bool isUpdating,
            bool isPlaying,
            bool isPlayingOrWillChangePlaymode,
            bool completeDespiteStuckTestRunner)
            => isCompiling
               || isUpdating
               || isPlaying
               || isPlayingOrWillChangePlaymode
               || isTestRunnerActive && !completeDespiteStuckTestRunner;

        void HandleRunFinished(ITestResultAdaptor result)
        {
            QueueCompletion(
                new()
                {
                    outcome = result.FailCount > 0 ? ToolOutcome.TestFailed : ToolOutcome.Success,
                    diagnostic = RunTestsTool.BuildFilteredTestRunDiagnostic(RunTestsTool.BuildCompletionSummary(result)),
                }
            );
        }

        void HandleTestStarted(ITestAdaptor test)
        {
            if (!BridgeCommandKinds.IsTest(activeCommandKind))
                return;

            logCapture.HandleTestStarted(test);
            RunTestsTool.RecordStartedFilteredTest(test);
        }

        void HandleTestFinished(ITestResultAdaptor result)
        {
            if (!BridgeCommandKinds.IsTest(activeCommandKind))
                return;

            var cancelledCompletion = RunTestsTool.IsCancelledTestResult(result)
                ? RunTestsTool.CreateCancelledTestRunResult(result)
                : null;

            logCapture.HandleTestFinished(result);

            // unity may stop a play mode run after the final Failed:Cancelled test without RunFinished
            if (cancelledCompletion != null)
                QueueCompletion(cancelledCompletion, discardLogs: true);
        }

        void HandleRunError(string message)
        {
            if (!string.IsNullOrEmpty(activeRunGuid))
                TestRunnerApi.CancelTestRun(activeRunGuid);

            if (RunTestsTool.TryCreateUserStoppedPlayModeTestRunResult(
                    message,
                    activeCommandKind == BridgeCommandKind.RunTestsPlayMode,
                    out var stoppedResult
                ))
            {
                QueueCompletion(stoppedResult!, discardLogs: true);
                return;
            }

            QueueCompletion(
                new()
                {
                    outcome = ToolOutcome.Exception,
                    diagnostic = RunTestsTool.BuildFilteredTestRunDiagnostic(message),
                    exception = new()
                    {
                        type = typeof(InvalidOperationException).FullName ?? nameof(InvalidOperationException),
                        message = message,
                    },
                }
            );
        }

        void QueueCompletion(BridgeCommandResult result, bool discardLogs = false)
        {
            if (!BridgeCommandKinds.IsTest(activeCommandKind))
                return;

            if (pendingResult != null && !ShouldReplacePendingResult(result))
                return;

            // cancellation and runner errors take precedence over a normal completion callback
            // callbacks can fire before play mode exits and before all log messages are delivered
            pendingResult = result;
            pendingCompletionRestored = false;
            checkpointCompletion(result);
            if (discardLogs)
                logCapture.DiscardOnCompletion();

            InstallCompletionHooks();
            TryCompletePendingRun();
        }

        static bool ShouldReplacePendingResult(BridgeCommandResult result)
            => result.outcome is ToolOutcome.Exception or ToolOutcome.Cancelled;

        void TryCompletePendingRun()
        {
            if (!BridgeCommandKinds.IsTest(activeCommandKind) || pendingResult == null)
                return;

            var isTestRunnerActive = IsTestRunStillActive();
            var isCompiling = EditorApplication.isCompiling;
            var isUpdating = EditorApplication.isUpdating;
            var isPlaying = EditorApplication.isPlaying;
            var isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode;
            if (ShouldWaitForCompletion(
                    isTestRunnerActive,
                    isCompiling,
                    isUpdating,
                    isPlaying,
                    isPlayingOrWillChangePlaymode,
                    pendingCompletionRestored || CanCompleteDespiteStuckTestRunner(pendingResult)
                ))
                return;

            var result = pendingResult;
            pendingResult = null;
            pendingCompletionRestored = false;
            RemoveCompletionHooks();
            _ = complete(result);
        }

        void TryCompleteAsyncStartup()
        {
            if (!asyncStartupPending || !BridgeCommandKinds.IsTest(activeCommandKind))
                return;

            if (!IsTestRunActive(activeRunGuid))
                return;

            if (activeCommandKind == BridgeCommandKind.RunTestsPlayMode && !EditorApplication.isPlaying)
                return;

            // async start is reported only after Unity has actually accepted the run
            var mode = activeCommandKind == BridgeCommandKind.RunTestsEditMode ? TestMode.EditMode : TestMode.PlayMode;
            _ = complete(CreateAsyncStartedResult(mode));
        }

        static bool CanCompleteDespiteStuckTestRunner(BridgeCommandResult result)
            => result.outcome is ToolOutcome.Cancelled or ToolOutcome.Exception;

        static BridgeCommandResult CreateAsyncStartedResult(TestMode mode)
            => new()
            {
                outcome = ToolOutcome.Success,
                return_value = $"{GetTestModeDisplayName(mode)} tests are running asynchronously. You can use other tools while the test run continues.",
            };

        static string GetTestModeDisplayName(TestMode mode)
            => mode == TestMode.EditMode ? "Edit mode" : "Play mode";

        bool TryCompleteDirtySceneBlock(string commandType)
        {
            if (ConduitSceneCommandUtility.BuildDirtySceneDiagnostic(commandType) is not { Length: > 0 } diagnostic)
                return false;

            _ = complete(
                new()
                {
                    outcome = ToolOutcome.DirtyScene,
                    diagnostic = diagnostic,
                }
            );
            return true;
        }

        bool TryCompleteBusyStartBlock(string commandType)
        {
            var isCompiling = EditorApplication.isCompiling;
            var isUpdating = EditorApplication.isUpdating;
            var isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode;
            if (!RunTestsTool.ShouldBlockTestRun(isCompiling, isUpdating, isPlayingOrWillChangePlaymode))
                return false;

            _ = complete(
                new()
                {
                    outcome = ToolOutcome.Exception,
                    diagnostic = RunTestsTool.BuildBlockedTestRunDiagnostic(
                        commandType,
                        isCompiling,
                        isUpdating,
                        isPlayingOrWillChangePlaymode
                    ),
                }
            );
            return true;
        }

        bool TryCompleteCompileErrorStartBlock(string commandType)
        {
            if (!RunTestsTool.ShouldFailTestRunForCompileErrors(EditorUtility.scriptCompilationFailed))
                return false;

            _ = complete(
                new()
                {
                    outcome = ToolOutcome.CompileError,
                    diagnostic = RunTestsTool.BuildCompileErrorTestRunDiagnostic(commandType),
                }
            );
            return true;
        }

        void InstallCompletionHooks()
        {
            if (completionHooksInstalled)
                return;

            completionHooksInstalled = true;
            EditorApplication.update += OnCompletionUpdate;
        }

        void RemoveCompletionHooks()
        {
            if (!completionHooksInstalled)
                return;

            completionHooksInstalled = false;
            EditorApplication.update -= OnCompletionUpdate;
        }

        void OnCompletionUpdate()
        {
            TryCompleteAsyncStartup();
            TryCompletePendingRun();
        }
    }
}
