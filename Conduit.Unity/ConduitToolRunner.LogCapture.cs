#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Conduit
{
    sealed class UnityTestRunMonitor
    {
        internal const int PlayerHeartbeatTimeoutSeconds = 30 * 60;
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

        static readonly MethodInfo? testRunnerIsRunActiveMethod = typeof(TestRunnerApi).GetMethod(
            "IsRunActive",
            BindingFlags.Static | BindingFlags.NonPublic);
        static readonly MethodInfo? testRunnerIsRunningMethod = typeof(TestRunnerApi).GetMethod(
            "IsRunning",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            new[] { typeof(string) },
            null);
        static readonly PropertyInfo? testRunnerJobDataHolderProperty = typeof(TestRunnerApi).GetProperty(
            "m_testJobDataHolder",
            BindingFlags.Static | BindingFlags.NonPublic);
        static readonly MethodInfo? testJobDataHolderGetAllRunnersMethod = typeof(TestRunnerApi).Assembly
            .GetType("UnityEditor.TestTools.TestRunner.TestRun.ITestJobDataHolder")
            ?.GetMethod("GetAllRunners", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly MethodInfo? testJobRunnerGetDataMethod = typeof(TestRunnerApi).Assembly
            .GetType("UnityEditor.TestTools.TestRunner.TestRun.ITestJobRunner")
            ?.GetMethod("GetData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo? testJobDataIsRunningField = typeof(TestRunnerApi).Assembly
            .GetType("UnityEditor.TestTools.TestRunner.TestRun.TestJobData")
            ?.GetField("isRunning", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo? testJobDataExecutionSettingsField = typeof(TestRunnerApi).Assembly
            .GetType("UnityEditor.TestTools.TestRunner.TestRun.TestJobData")
            ?.GetField("executionSettings", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo? executionSettingsHasTargetPlatformField = typeof(ExecutionSettings)
            .GetField("m_HasTargetPlatform", BindingFlags.Instance | BindingFlags.NonPublic);

        public UnityTestRunMonitor(
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

        public void EnsureCallbacksRegistered()
        {
            if (callbacksRegistered)
                return;

            callbacksRegistered = true;
            GetOrCreateTestRunnerApi().RegisterCallbacks(callbacks);
        }

        public void Start(PendingOperationState operation, BridgeCommandKind commandKind, TestMode mode, bool playerRun)
        {
            EnsureCallbacksRegistered();
            activeCommandKind = commandKind;
            asyncStartupPending = operation.@async;
            pendingCompletionRestored = false;

            if (TryCompleteDirtySceneBlock(operation.command_type)
                || TryCompleteBusyStartBlock(operation.command_type)
                || TryCompleteCompileErrorStartBlock(operation.command_type))
                return;

            var filter = new Filter { testMode = mode };
            run_tests.ApplyFilter(filter, operation.test_filter);
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

            if (operation.@async)
            {
                InstallCompletionHooks();
                TryCompleteAsyncStartup();
            }
        }

        public void ResumeRestored(BridgeCommandKind commandKind, BridgeCommandResult? restoredPendingResult)
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

        public void Stop()
        {
            RemoveCompletionHooks();
            activeCommandKind = BridgeCommandKind.Unknown;
            activeRunGuid = null;
            pendingResult = null;
            asyncStartupPending = false;
            pendingCompletionRestored = false;
        }

        public bool Cancel()
        {
            if (!BridgeCommandKinds.IsTest(activeCommandKind)
                || string.IsNullOrEmpty(activeRunGuid)
                || !TestRunnerApi.CancelTestRun(activeRunGuid))
                return false;

            QueueCompletion(
                run_tests.CreateRequestCancelledResult(),
                discardLogs: true
            );
            return true;
        }

        public bool IsAnyTestRunActive()
            => TryInvokeTestRunnerBoolMethod(testRunnerIsRunActiveMethod, out var isRunActive) && isRunActive;

        public string? GetActiveTestRunMode()
        {
            if (TryGetActiveTestExecutionSettings() is not { } settings)
                return null;

            var filters = settings.filters ?? Array.Empty<Filter>();
            var hasEditMode = false;
            var hasPlayMode = false;
            foreach (var filter in filters)
            {
                hasEditMode |= IncludesTestMode(filter.testMode, TestMode.EditMode);
                hasPlayMode |= IncludesTestMode(filter.testMode, TestMode.PlayMode);
            }

            if (hasPlayMode)
                return HasTargetPlatform(settings) ? "player" : "play mode";

            return hasEditMode ? "edit mode" : null;
        }

        public static bool ShouldWaitForCompletion(
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
                    diagnostic = run_tests.BuildFilteredTestRunDiagnostic(run_tests.BuildCompletionSummary(result)),
                }
            );
        }

        void HandleTestStarted(ITestAdaptor test)
        {
            if (!BridgeCommandKinds.IsTest(activeCommandKind))
                return;

            logCapture.HandleTestStarted(test);
            run_tests.RecordStartedFilteredTest(test);
        }

        void HandleTestFinished(ITestResultAdaptor result)
        {
            if (!BridgeCommandKinds.IsTest(activeCommandKind))
                return;

            var cancelledCompletion = run_tests.IsCancelledTestResult(result)
                ? run_tests.CreateCancelledTestRunResult(result)
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

            if (run_tests.TryCreateUserStoppedPlayModeTestRunResult(
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
                    diagnostic = run_tests.BuildFilteredTestRunDiagnostic(message),
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

        bool IsTestRunStillActive()
        {
            if (!string.IsNullOrEmpty(activeRunGuid)
                && TryInvokeTestRunnerBoolMethod(testRunnerIsRunningMethod, out var isRunning, activeRunGuid))
                return isRunning;

            return IsAnyTestRunActive();
        }

        static bool IsTestRunActive(string? runGuid)
        {
            if (!string.IsNullOrEmpty(runGuid)
                && TryInvokeTestRunnerBoolMethod(testRunnerIsRunningMethod, out var isRunning, runGuid))
                return isRunning;

            return TryInvokeTestRunnerBoolMethod(testRunnerIsRunActiveMethod, out var isRunActive) && isRunActive;
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
            if (!run_tests.ShouldBlockTestRun(isCompiling, isUpdating, isPlayingOrWillChangePlaymode))
                return false;

            _ = complete(
                new()
                {
                    outcome = ToolOutcome.Exception,
                    diagnostic = run_tests.BuildBlockedTestRunDiagnostic(
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
            if (!run_tests.ShouldFailTestRunForCompileErrors(EditorUtility.scriptCompilationFailed))
                return false;

            _ = complete(
                new()
                {
                    outcome = ToolOutcome.CompileError,
                    diagnostic = run_tests.BuildCompileErrorTestRunDiagnostic(commandType),
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

        TestRunnerApi GetOrCreateTestRunnerApi()
            => testRunnerApi != null ? testRunnerApi : testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();

        static ExecutionSettings? TryGetActiveTestExecutionSettings()
        {
            try
            {
                var holder = testRunnerJobDataHolderProperty?.GetValue(null);
                if (holder == null || testJobDataHolderGetAllRunnersMethod == null)
                    return null;

                if (testJobDataHolderGetAllRunnersMethod.Invoke(holder, null) is not Array runners)
                    return null;

                foreach (var runner in runners)
                {
                    var data = testJobRunnerGetDataMethod?.Invoke(runner, null);
                    if (data == null || testJobDataIsRunningField?.GetValue(data) is not true)
                        continue;

                    if (testJobDataExecutionSettingsField?.GetValue(data) is ExecutionSettings settings)
                        return settings;
                }
            }
            catch (Exception)
            {
                // unity test runner internals vary by version; status can fall back to unknown mode
            }

            return null;
        }

        static bool IncludesTestMode(TestMode testMode, TestMode mode)
            => (testMode & mode) == mode;

        static bool HasTargetPlatform(ExecutionSettings settings)
            => executionSettingsHasTargetPlatformField?.GetValue(settings) is true;

        static bool TryInvokeTestRunnerBoolMethod(MethodInfo? method, out bool value, params object?[] args)
        {
            value = false;
            if (method == null)
                return false;

            try
            {
                if (method.Invoke(null, args) is bool result)
                {
                    value = result;
                    return true;
                }
            }
            catch (Exception)
            {
                // reflection failures are treated as unavailable test runner state
            }

            return false;
        }

        sealed class TestRunCallbacks : IErrorCallbacks
        {
            readonly UnityTestRunMonitor owner;

            public TestRunCallbacks(UnityTestRunMonitor owner)
                => this.owner = owner;

            public void RunStarted(ITestAdaptor testsToRun) { }
            public void RunFinished(ITestResultAdaptor result) => owner.HandleRunFinished(result);
            public void TestStarted(ITestAdaptor test) => owner.HandleTestStarted(test);
            public void TestFinished(ITestResultAdaptor result) => owner.HandleTestFinished(result);
            public void OnError(string message) => owner.HandleRunError(message);
        }

        sealed class PlayerTestRunSettings : ITestRunSettings
        {
            const string TestPlayerEnvironmentVariable = "CONDUIT_TEST_PLAYER";
            const string VideoDriverEnvironmentVariable = "SDL_VIDEODRIVER";
            const string XInput2EnvironmentVariable = "SDL_VIDEO_X11_XINPUT2";
            readonly bool configureLinuxDisplay;
            string? previousTestPlayer;
            string? previousVideoDriver;
            string? previousXInput2;

            public PlayerTestRunSettings(bool configureLinuxDisplay)
                => this.configureLinuxDisplay = configureLinuxDisplay;

            public void Apply()
            {
                previousTestPlayer = Environment.GetEnvironmentVariable(TestPlayerEnvironmentVariable);
                // inherited by the launched process so the server never shuts down an unrelated development player
                Environment.SetEnvironmentVariable(TestPlayerEnvironmentVariable, "1");
                if (!configureLinuxDisplay)
                    return;

                previousVideoDriver = Environment.GetEnvironmentVariable(VideoDriverEnvironmentVariable);
                previousXInput2 = Environment.GetEnvironmentVariable(XInput2EnvironmentVariable);
                // Unity's bundled SDL crashes in the XInput2 touch path under XWayland; prefer native Wayland there.
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                    Environment.SetEnvironmentVariable(VideoDriverEnvironmentVariable, "wayland");
                Environment.SetEnvironmentVariable(XInput2EnvironmentVariable, "0");
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(TestPlayerEnvironmentVariable, previousTestPlayer);
                if (!configureLinuxDisplay)
                    return;

                Environment.SetEnvironmentVariable(VideoDriverEnvironmentVariable, previousVideoDriver);
                Environment.SetEnvironmentVariable(XInput2EnvironmentVariable, previousXInput2);
            }
        }
    }

    sealed class ToolLogCapture
    {
        // unity invokes logMessageReceivedThreaded from worker threads
        readonly object gate = new();
        readonly CapturedLogTarget commandLogTarget = new();
        readonly CapturedLogTarget testRunLogTarget = new();
        readonly Dictionary<string, CapturedLogTarget> activeTestLogTargets = new(StringComparer.Ordinal);
        readonly List<CapturedLogTarget> completedTestLogTargets = new();
        // nested test callbacks make the latest started test the owner of subsequent logs
        readonly List<string> activeTestScopes = new();
        readonly Dictionary<LogSignature, int> capturedLogEntryIndexes = new(LogSignatureComparer.Instance);
        readonly List<CapturedLogEntry> capturedLogEntries = new();
        BridgeCommandKind activeCommandKind;
        bool hooked;
        bool discardOnCompletion;

        public void Start(BridgeCommandKind commandKind)
        {
            lock (gate)
            {
                ResetStateUnderLock();
                activeCommandKind = commandKind;
            }

            EnsureHooked();
        }

        public string Drain(BridgeCommandKind commandKind, string outcome, string? diagnostic, out bool discardLogs)
        {
            if (hooked)
            {
                Application.logMessageReceivedThreaded -= OnLogMessageReceived;
                hooked = false;
            }

            lock (gate)
            {
                discardLogs = discardOnCompletion;
                var logs = BridgeCommandKinds.IsTest(commandKind)
                    ? BuildTestLogs()
                    : BuildCapturedLogs(commandLogTarget, diagnostic);

                ResetStateUnderLock();
                return logs;
            }
        }

        public void DiscardOnCompletion()
        {
            lock (gate)
                discardOnCompletion = true;
        }

        public void HandleTestStarted(ITestAdaptor test)
        {
            lock (gate)
            {
                if (!BridgeCommandKinds.IsTest(activeCommandKind))
                    return;

                activeTestScopes.Add(GetTestLabel(test));
            }
        }

        public void HandleTestFinished(ITestResultAdaptor result)
        {
            lock (gate)
            {
                if (!BridgeCommandKinds.IsTest(activeCommandKind))
                    return;

                var label = GetTestLabel(result);
                RemoveActiveTestScope(label);
                if (!activeTestLogTargets.TryGetValue(label, out var target))
                    return;

                activeTestLogTargets.Remove(label);
                if (HasChildResults(result))
                    return;

                target.Failed = result.FailCount > 0;
                completedTestLogTargets.Add(target);
            }
        }

        public static string? TrimCommonTail(string? simplifiedStackTrace)
            => BridgeExceptionFormatter.TrimCommonLogTail(simplifiedStackTrace);

        public static string? CleanCapturedStackTrace(BridgeCommandKind commandKind, string? stackTrace, LogType logType)
        {
            bool isTestCommand = BridgeCommandKinds.IsTest(commandKind);
            string? cleanedStackTrace = isTestCommand
                ? TrimCommonTail(ConduitUtility.SimplifyStackTrace(stackTrace))
                : CleanCommandStackTrace(commandKind, stackTrace);

            return logType == LogType.Log
                   && !isTestCommand
                   && FirstFrameEquals(cleanedStackTrace, "UnityEngine.Debug:Log")
                ? null
                : cleanedStackTrace;
        }

        public static string FormatCapturedLogEntryForTest(string message, string? stackTrace, int repeatCount = 1)
        {
            var builder = new StringBuilder();
            var entry = new CapturedLogEntry(message, stackTrace ?? string.Empty, LogType.Log)
            {
                RepeatCount = repeatCount,
            };
            AppendCapturedLogEntry(builder, entry);
            return builder.ToString();
        }

        public static bool ShouldOmitDiagnosticLogEntry(string message, string? diagnostic)
        {
            if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(diagnostic))
                return false;

            // compiler diagnostics can arrive through the result and Unity's log callback
            return IsCompilerDiagnosticLogMessage(message)
                   && diagnostic!.Contains(message, StringComparison.Ordinal);
        }

        public static bool ShouldSuppressCapturedLogEntry(string message)
            => view_burst_asm.ShouldSuppressBurstDiagnostic(message);

        public static bool ShouldSuppressCapturedLogEntry(string message, BridgeCommandKind commandKind)
            => ShouldSuppressCapturedLogEntry(message)
               || commandKind == BridgeCommandKind.ExecuteCode
                  && execute_code.ShouldSuppressCompilerWarning(message);

        public static string NormalizeCapturedLogMessage(string message)
            => view_burst_asm.IsBurstDiagnostic(message)
                ? view_burst_asm.SimplifyBurstDiagnostic(message)
                : message;

        public static bool ShouldIncludeTestLogEntry(LogType logType, bool includeAllLogs)
            => includeAllLogs || IsErrorLogType(logType);

        void EnsureHooked()
        {
            if (hooked)
                return;

            hooked = true;
            Application.logMessageReceivedThreaded += OnLogMessageReceived;
        }

        void OnLogMessageReceived(string condition, string stackTrace, LogType logType)
        {
            if (ShouldSuppressCapturedLogEntry(condition, activeCommandKind))
                return;

            // burst diagnostics can embed assembly-qualified signatures longer than the useful error text
            condition = NormalizeCapturedLogMessage(condition);
            lock (gate)
            {
                var target = ResolveLogTargetUnderLock();
                var simplifiedStackTrace = CleanCapturedStackTrace(activeCommandKind, stackTrace, logType);
                CaptureLogEntry(target, condition, simplifiedStackTrace, logType);
            }
        }

        CapturedLogTarget ResolveLogTargetUnderLock()
        {
            if (!BridgeCommandKinds.IsTest(activeCommandKind))
                return commandLogTarget;

            if (activeTestScopes.Count == 0)
                return testRunLogTarget;

            var label = activeTestScopes[^1];
            if (activeTestLogTargets.TryGetValue(label, out var target))
                return target;

            target = new(label);
            activeTestLogTargets.Add(label, target);
            return target;
        }

        void CaptureLogEntry(CapturedLogTarget target, string condition, string? simplifiedStackTrace, LogType logType)
        {
            var message = condition ?? string.Empty;
            var stack = simplifiedStackTrace ?? string.Empty;
            if (message.Length == 0 && stack.Length == 0)
                return;

            var signature = new LogSignature(message, stack, logType);
            if (capturedLogEntryIndexes.TryGetValue(signature, out var entryIndex))
            {
                capturedLogEntries[entryIndex].RepeatCount++;
                // entries are deduped globally while each target keeps its own first reference
                AddTargetEntryIndex(target, entryIndex);
                return;
            }

            entryIndex = capturedLogEntries.Count;
            capturedLogEntryIndexes.Add(signature, entryIndex);
            capturedLogEntries.Add(new(message, stack, logType));
            target.EntryIndexes.Add(entryIndex);
        }

        static void AddTargetEntryIndex(CapturedLogTarget target, int entryIndex)
        {
            foreach (var existingEntryIndex in target.EntryIndexes)
                if (existingEntryIndex == entryIndex)
                    return;

            target.EntryIndexes.Add(entryIndex);
        }

        string BuildCapturedLogs(CapturedLogTarget target, string? diagnostic)
        {
            if (target.EntryIndexes.Count == 0)
                return string.Empty;

            var builder = new StringBuilder();
            AppendCapturedLogEntries(target, builder, diagnostic);
            return builder.ToString().Trim();
        }

        string BuildTestLogs()
        {
            var includeAllLogs = run_tests.ShouldIncludeAllTestLogs();
            var builder = new StringBuilder();
            if (!includeAllLogs && HasAnyTestLogEntries())
            {
                builder.Append(run_tests.LargeTestRunLogNote);
            }

            foreach (var testLogTarget in completedTestLogTargets)
            {
                if (!HasIncludedLogEntries(testLogTarget, includeAllLogs))
                    continue;

                AppendSectionSeparator(builder);
                builder.Append(testLogTarget.Failed ? "FAILED TEST: " : "TEST: ");
                builder.AppendLine(testLogTarget.Label);
                AppendCapturedLogEntries(testLogTarget, builder, includeAllLogs: includeAllLogs);
            }

            if (HasIncludedLogEntries(testRunLogTarget, includeAllLogs))
            {
                AppendSectionSeparator(builder);
                builder.AppendLine("TEST RUN:");
                AppendCapturedLogEntries(testRunLogTarget, builder, includeAllLogs: includeAllLogs);
            }

            return builder.ToString().Trim();
        }

        bool HasAnyTestLogEntries()
        {
            if (testRunLogTarget.EntryIndexes.Count > 0)
                return true;

            foreach (var testLogTarget in completedTestLogTargets)
                if (testLogTarget.EntryIndexes.Count > 0)
                    return true;

            return false;
        }

        bool HasIncludedLogEntries(CapturedLogTarget target, bool includeAllLogs)
        {
            foreach (var entryIndex in target.EntryIndexes)
                if (ShouldIncludeTestLogEntry(capturedLogEntries[entryIndex].LogType, includeAllLogs))
                    return true;

            return false;
        }

        void AppendCapturedLogEntries(
            CapturedLogTarget target,
            StringBuilder builder,
            string? diagnostic = null,
            bool includeAllLogs = true)
        {
            var isFirstEntry = true;
            foreach (var entryIndex in target.EntryIndexes)
            {
                var entry = capturedLogEntries[entryIndex];
                if (!ShouldIncludeTestLogEntry(entry.LogType, includeAllLogs)
                    || ShouldOmitDiagnosticLogEntry(entry.Message, diagnostic))
                    continue;

                if (!isFirstEntry)
                    AppendSectionSeparator(builder);

                AppendCapturedLogEntry(builder, entry);
                isFirstEntry = false;
            }
        }

        static void AppendCapturedLogEntry(StringBuilder builder, CapturedLogEntry entry)
            => BridgeLogFormatter.Append(
                builder,
                entry.Message,
                entry.StackTrace,
                entry.RepeatCount
            );

        static void AppendSectionSeparator(StringBuilder builder)
        {
            if (builder.Length == 0)
                return;

            builder.Append("\n\n");
        }

        static string? CleanCommandStackTrace(BridgeCommandKind commandKind, string? stackTrace)
        {
            if (commandKind == BridgeCommandKind.ExecuteCode
                && TryTrimExecuteCodeInvocationStack(stackTrace, out string executeCodeStackTrace))
                return ConduitUtility.SimplifyStackTrace(executeCodeStackTrace);

            return TrimCommonTail(ConduitUtility.SimplifyStackTrace(stackTrace));
        }

        static bool TryTrimExecuteCodeInvocationStack(string? stackTrace, out string trimmedStackTrace)
        {
            trimmedStackTrace = string.Empty;
            if (string.IsNullOrWhiteSpace(stackTrace))
                return false;

            // execute_code runner frames are hidden from simplified stacks, so the boundary must be found
            // from raw unity frames before conduit/generated frames are removed.
            using var pooledFrames = ConduitUtility.GetPooledList<string>(out var frames);
            using var reader = new StringReader(stackTrace);
            while (reader.ReadLine() is { } line)
                if (line.Trim() is { Length: > 0 } frame)
                    frames.Add(frame);

            for (int index = frames.Count - 1; index >= 0; index--)
            {
                if (!IsMethodBaseInvokeFrame(frames[index])
                    || (!HasCompilerMessageCompletionEvidence(frames, index)
                        && !HasExecuteCodeRunnerEvidence(frames, index)))
                    continue;

                trimmedStackTrace = JoinStackFrames(frames, index);
                return true;
            }

            return false;
        }

        static bool HasCompilerMessageCompletionEvidence(List<string> frames, int methodBaseInvokeIndex)
        {
            // unity compiler callbacks can append unrelated update frames after task completion frames.
            // limiting the search window keeps ordinary reflection stacks from being classified as execute_code.
            int end = Math.Min(frames.Count, methodBaseInvokeIndex + 5);
            for (int index = methodBaseInvokeIndex + 1; index < end; index++)
                if (IsCompilerMessageCompletionFrame(frames[index]))
                    return true;

            return false;
        }

        static bool HasExecuteCodeRunnerEvidence(List<string> frames, int methodBaseInvokeIndex)
        {
            for (int index = methodBaseInvokeIndex + 1; index < frames.Count; index++)
            {
                if (IsMethodBaseInvokeFrame(frames[index]))
                    return false;

                if (IsExecuteCodeRunnerFrame(frames[index]))
                    return true;
            }

            return false;
        }

        static string JoinStackFrames(List<string> frames, int count)
        {
            if (count <= 0)
                return string.Empty;

            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            for (int index = 0; index < count; index++)
            {
                if (builder.Length > 0)
                    builder.AppendLine();

                builder.Append(frames[index]);
            }

            return builder.ToString();
        }

        void ResetStateUnderLock()
        {
            activeCommandKind = BridgeCommandKind.Unknown;
            commandLogTarget.Reset();
            testRunLogTarget.Reset();
            activeTestScopes.Clear();
            activeTestLogTargets.Clear();
            completedTestLogTargets.Clear();
            capturedLogEntryIndexes.Clear();
            capturedLogEntries.Clear();
            discardOnCompletion = false;
            run_tests.ResetState();
        }

        void RemoveActiveTestScope(string label)
        {
            // callbacks are stack-like; preserve outer fixtures when an inner test finishes
            for (var index = activeTestScopes.Count - 1; index >= 0; index--)
            {
                if (activeTestScopes[index] != label)
                    continue;

                activeTestScopes.RemoveAt(index);
                return;
            }
        }

        static string GetTestLabel(ITestAdaptor test)
            => string.IsNullOrWhiteSpace(test.FullName) ? test.Name : test.FullName;

        static string GetTestLabel(ITestResultAdaptor result)
            => string.IsNullOrWhiteSpace(result.FullName) ? result.Name : result.FullName;

        static bool HasChildResults(ITestResultAdaptor result)
        {
            if (result.Children == null)
                return false;

            foreach (var _ in result.Children)
                return true;

            return false;
        }

        static bool IsMethodBaseInvokeFrame(string frame)
            => FrameNameEquals(frame, "System.Reflection.MethodBase:Invoke")
               || FrameNameEquals(frame, "System.Reflection.MethodBase.Invoke");

        static bool IsCompilerMessageCompletionFrame(string frame)
            => frame.Contains("UnityEditor.Compilation.CompilerMessage[]", StringComparison.Ordinal)
               && (frame.StartsWith("System.Runtime.CompilerServices.AsyncTaskMethodBuilder", StringComparison.Ordinal)
                   && (frame.Contains(":SetResult", StringComparison.Ordinal)
                       || frame.Contains(".SetResult", StringComparison.Ordinal))
                   || frame.StartsWith("System.Threading.Tasks.TaskCompletionSource", StringComparison.Ordinal)
                   && (frame.Contains(":TrySetResult", StringComparison.Ordinal)
                       || frame.Contains(".TrySetResult", StringComparison.Ordinal)));

        static bool IsExecuteCodeRunnerFrame(string frame)
            => frame.Contains("Conduit.execute_code", StringComparison.Ordinal)
               && (frame.Contains("InvokeAsync", StringComparison.Ordinal)
                   || frame.Contains("ExecuteCachedCompilationAsync", StringComparison.Ordinal)
                   || frame.Contains("ExecuteAsync", StringComparison.Ordinal));

        static bool FirstFrameEquals(string? stackTrace, string frameName)
        {
            if (string.IsNullOrWhiteSpace(stackTrace))
                return false;

            string value = stackTrace!;
            int lineEnd = value.IndexOf('\n');
            string firstFrame = lineEnd < 0 ? value : value[..lineEnd];
            return firstFrame.TrimEnd() == frameName;
        }

        static bool FrameNameEquals(string frame, string frameName)
        {
            int start = frame.StartsWith("at ", StringComparison.Ordinal) ? 3 : 0;
            if (frame.Length - start < frameName.Length
                || string.CompareOrdinal(frame, start, frameName, 0, frameName.Length) != 0)
                return false;

            int next = start + frameName.Length;
            return next == frame.Length || char.IsWhiteSpace(frame[next]) || frame[next] is '(' or '[';
        }

        static bool IsCompilerDiagnosticLogMessage(string message)
            => message.Contains("): error ", StringComparison.Ordinal)
               || message.Contains("): warning ", StringComparison.Ordinal);

        static bool IsErrorLogType(LogType logType)
            => logType is LogType.Error or LogType.Assert or LogType.Exception;

        sealed class CapturedLogEntry
        {
            public CapturedLogEntry(string message, string stackTrace, LogType logType)
            {
                Message = message;
                StackTrace = stackTrace;
                LogType = logType;
                RepeatCount = 1;
            }

            public string Message { get; }
            public string StackTrace { get; }
            public LogType LogType { get; }
            public int RepeatCount { get; set; }
        }

        readonly struct LogSignature
        {
            public LogSignature(string message, string stackTrace, LogType logType)
            {
                Message = message;
                StackTrace = stackTrace;
                LogType = logType;
            }

            public string Message { get; }
            public string StackTrace { get; }
            public LogType LogType { get; }
        }

        sealed class LogSignatureComparer : IEqualityComparer<LogSignature>
        {
            public static readonly LogSignatureComparer Instance = new();

            public bool Equals(LogSignature x, LogSignature y)
                => x.Message == y.Message && x.StackTrace == y.StackTrace && x.LogType == y.LogType;

            public int GetHashCode(LogSignature obj)
            {
                unchecked
                {
                    var hashCode = StringComparer.Ordinal.GetHashCode(obj.Message);
                    hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(obj.StackTrace);
                    return (hashCode * 397) ^ (int)obj.LogType;
                }
            }
        }

        sealed class CapturedLogTarget
        {
            public CapturedLogTarget() { }

            public CapturedLogTarget(string label)
                => Label = label;

            public string Label { get; private set; } = string.Empty;
            public bool Failed { get; set; }
            public List<int> EntryIndexes { get; } = new();

            public void Reset(string label = "")
            {
                Label = label;
                Failed = false;
                EntryIndexes.Clear();
            }
        }
    }
}
