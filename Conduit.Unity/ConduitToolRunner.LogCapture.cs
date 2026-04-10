#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Conduit
{
    static partial class ConduitToolRunner
    {
        static void EnsureTestCallbacksRegistered()
        {
            if (testCallbacksRegistered)
                return;

            testCallbacksRegistered = true;
            GetOrCreateTestRunnerApi().RegisterCallbacks(testCallbacks);
        }

        static TestRunnerApi GetOrCreateTestRunnerApi()
            => testRunnerApi != null ? testRunnerApi : testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();

        static void HandleTestRunFinished(ITestResultAdaptor result)
        {
            _ = CompleteCurrentAsync(
                new()
                {
                    outcome = result.FailCount > 0 ? ToolOutcome.TestFailed : ToolOutcome.Success,
                    diagnostic = run_tests.BuildFilteredTestRunDiagnostic(run_tests.BuildCompletionSummary(result)),
                }
            );
        }

        static void HandleTestStarted(ITestAdaptor test)
        {
            lock (stateGate)
            {
                if (!IsTestCommand(activeCommand.Kind))
                    return;

                activeTestScopes.Add(GetTestLabel(test));
                run_tests.RecordStartedFilteredTest(test);
            }
        }

        static void HandleTestFinished(ITestResultAdaptor result)
        {
            lock (stateGate)
            {
                if (!IsTestCommand(activeCommand.Kind))
                    return;

                var label = GetTestLabel(result);
                RemoveActiveTestScope(label);
                if (!activeTestLogTargets.TryGetValue(label, out var target))
                    return;

                activeTestLogTargets.Remove(label);
                if (result.FailCount > 0 && !HasChildResults(result))
                    failedTestLogTargets.Add(target);
                else
                    RecycleLogTarget(target);
            }
        }

        static void HandleTestRunError(string message)
        {
            if (run_tests.TryCreateUserStoppedPlayModeTestRunResult(
                    message,
                    activeCommand.Kind == ParsedBridgeCommandKind.RunTestsPlayMode,
                    out var stoppedResult
                ))
            {
                lock (stateGate)
                    discardCapturedLogsOnCompletion = true;

                _ = CompleteCurrentAsync(stoppedResult!);
                return;
            }

            _ = CompleteCurrentAsync(
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

        static BridgeExceptionInfo ToExceptionInfo(Exception exception)
            => ConduitUtility.ToExceptionInfo(exception);

        static void StartLogCapture()
        {
            lock (stateGate)
                ResetLogCaptureStateUnderLock();

            EnsureLogCaptureHooked();
        }

        static void EnsureLogCaptureHooked()
        {
            if (logCaptureHooked)
                return;

            logCaptureHooked = true;
            Application.logMessageReceivedThreaded += OnLogMessageReceived;
        }

        static string DrainLogs(string commandType, string outcome)
        {
            if (logCaptureHooked)
            {
                Application.logMessageReceivedThreaded -= OnLogMessageReceived;
                logCaptureHooked = false;
            }

            lock (stateGate)
            {
                var logs = IsTestCommand(ParseIncomingCommand(commandType).Kind)
                    ? BuildTestLogs(outcome)
                    : BuildCapturedLogs(commandLogTarget);

                ResetLogCaptureStateUnderLock();
                return logs;
            }
        }

        static void OnLogMessageReceived(string condition, string stackTrace, LogType logType)
        {
            var simplifiedStackTrace = TrimCommonLogTail(ConduitUtility.SimplifyStackTrace(stackTrace));
            lock (stateGate)
            {
                var target = ResolveLogTargetUnderLock();
                CaptureLogEntry(target, condition, simplifiedStackTrace);
            }
        }

        static CapturedLogTarget ResolveLogTargetUnderLock()
        {
            if (!IsTestCommand(activeCommand.Kind))
                return commandLogTarget;

            if (activeTestScopes.Count == 0)
                return testRunLogTarget;

            var label = activeTestScopes[^1];
            if (activeTestLogTargets.TryGetValue(label, out var target))
                return target;

            target = recycledTestLogTargets.Count > 0 ? recycledTestLogTargets.Pop() : new();
            target.Reset(label);
            activeTestLogTargets.Add(label, target);
            return target;
        }

        static void CaptureLogEntry(CapturedLogTarget target, string condition, string? simplifiedStackTrace)
        {
            var message = condition ?? string.Empty;
            var stack = simplifiedStackTrace ?? string.Empty;
            if (message.Length == 0 && stack.Length == 0)
                return;

            var signature = new LogSignature
            {
                Message = message,
                StackTrace = stack,
            };

            if (capturedLogEntryIndexes.TryGetValue(signature, out var entryIndex))
            {
                var existingEntry = capturedLogEntries[entryIndex];
                existingEntry.RepeatCount++;
                capturedLogEntries[entryIndex] = existingEntry;
                return;
            }

            entryIndex = capturedLogEntries.Count;
            capturedLogEntryIndexes.Add(signature, entryIndex);
            capturedLogEntries.Add(
                new()
                {
                    Message = message,
                    StackTrace = stack,
                    RepeatCount = 1,
                }
            );

            target.EntryIndexes.Add(entryIndex);
        }

        static void AppendQuotedLines(StringBuilder builder, string message)
        {
            if (string.IsNullOrEmpty(message))
                return;

            builder.Append("> ");
            for (var index = 0; index < message.Length; index++)
            {
                var character = message[index];
                if (character == '\r')
                    continue;

                builder.Append(character);
                if (character == '\n' && index + 1 < message.Length)
                    builder.Append("> ");
            }
        }

        internal static string? TrimCommonLogTail(string? simplifiedStackTrace)
        {
            if (simplifiedStackTrace == null || string.IsNullOrWhiteSpace(simplifiedStackTrace))
                return simplifiedStackTrace;

            var end = simplifiedStackTrace.Length;
            var removedAny = false;
            while (true)
            {
                while (end > 0 && (simplifiedStackTrace[end - 1] == '\r' || simplifiedStackTrace[end - 1] == '\n'))
                    end--;

                if (end <= 0)
                    return null;

                var lastLineBreak = simplifiedStackTrace.LastIndexOf('\n', end - 1);
                var lineStart = lastLineBreak < 0 ? 0 : lastLineBreak + 1;
                var frame = simplifiedStackTrace[lineStart..end].TrimEnd();
                if (!IsIgnorableLogTailFrame(frame))
                    break;

                removedAny = true;
                end = lineStart == 0 ? 0 : lineStart - 1;
            }

            if (!removedAny)
                return simplifiedStackTrace;

            var trimmed = simplifiedStackTrace[..end].TrimEnd('\r', '\n');
            return trimmed.Length == 0 ? null : trimmed;
        }

        static bool IsIgnorableLogTailFrame(string frame)
            => frame is "System.Reflection.MethodBase:Invoke"
                or "UnityEngine.UnitySynchronizationContext:ExecuteTasks"
                or "NUnit.Framework.Internal.MethodWrapper:Invoke"
                or "NUnit.Framework.Internal.Commands.TestMethodCommand:RunNonAsyncTestMethod"
                or "NUnit.Framework.Internal.Commands.TestMethodCommand:RunTestMethod"
                or "NUnit.Framework.Internal.Commands.TestMethodCommand:Execute"
                or "UnityEditor.EditorApplication:Internal_CallUpdateFunctions"
                || IsExecuteCodeCompilerCallbackFrame(frame);

        static bool IsExecuteCodeCompilerCallbackFrame(string frame)
            => frame.StartsWith("System.Runtime.CompilerServices.AsyncTaskMethodBuilder", StringComparison.Ordinal)
               && frame.EndsWith(":SetResult", StringComparison.Ordinal)
               || frame.StartsWith("System.Threading.Tasks.TaskCompletionSource", StringComparison.Ordinal)
               && frame.EndsWith(":TrySetResult", StringComparison.Ordinal)
               || frame is "UnityEditor.Scripting.ScriptCompilation.EditorCompilationInterface:IsCompiling";

        static void AppendCompilerMessage(string message)
        {
            lock (stateGate)
                compilerMessageBuffer.AppendLine(message);
        }

        static void ClearCompilerMessages()
        {
            lock (stateGate)
                compilerMessageBuffer.Clear();
        }

        static string GetCompilerMessages()
        {
            lock (stateGate)
                return compilerMessageBuffer.ToString().Trim();
        }

        static string BuildCapturedLogs(CapturedLogTarget target)
        {
            if (target.EntryIndexes.Count == 0)
                return string.Empty;

            var builder = new StringBuilder();
            AppendCapturedLogEntries(target, builder);
            return builder.ToString().Trim();
        }

        static string BuildTestLogs(string outcome)
        {
            if (outcome == ToolOutcome.Success)
                return string.Empty;

            var builder = new StringBuilder();
            foreach (var failedTestLogTarget in failedTestLogTargets)
            {
                if (failedTestLogTarget.EntryIndexes.Count == 0)
                    continue;

                AppendSectionSeparator(builder);
                builder.Append("FAILED TEST: ");
                builder.AppendLine(failedTestLogTarget.Label);
                AppendCapturedLogEntries(failedTestLogTarget, builder);
            }

            if (testRunLogTarget.EntryIndexes.Count > 0)
            {
                AppendSectionSeparator(builder);
                builder.AppendLine("TEST RUN:");
                AppendCapturedLogEntries(testRunLogTarget, builder);
            }

            return builder.ToString().Trim();
        }

        static void AppendCapturedLogEntries(CapturedLogTarget target, StringBuilder builder)
        {
            var isFirstEntry = true;
            foreach (var entryIndex in target.EntryIndexes)
            {
                if (!isFirstEntry)
                    AppendSectionSeparator(builder);

                AppendCapturedLogEntry(builder, capturedLogEntries[entryIndex]);
                isFirstEntry = false;
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

        static void RemoveActiveTestScope(string label)
        {
            for (var index = activeTestScopes.Count - 1; index >= 0; index--)
            {
                if (activeTestScopes[index] != label)
                    continue;

                activeTestScopes.RemoveAt(index);
                return;
            }
        }

        static void AppendCapturedLogEntry(StringBuilder builder, CapturedLogEntry entry)
        {
            AppendQuotedLines(builder, entry.Message);
            if (entry.StackTrace is { Length: > 0 })
            {
                if (builder.Length > 0 && builder[^1] != '\n')
                    builder.AppendLine();

                builder.Append(entry.StackTrace);
            }

            if (entry.RepeatCount > 1)
            {
                if (builder.Length > 0 && builder[^1] != '\n')
                    builder.AppendLine();

                builder.Append("(log repeated ");
                builder.Append(entry.RepeatCount);
                builder.Append(" times)");
            }
        }

        static void AppendSectionSeparator(StringBuilder builder)
        {
            if (builder.Length == 0)
                return;

            builder.AppendLine().AppendLine();
        }

        static void ResetLogCaptureStateUnderLock()
        {
            commandLogTarget.Reset();
            testRunLogTarget.Reset();
            activeTestScopes.Clear();
            discardCapturedLogsOnCompletion = false;
            run_tests.ResetState();
            foreach (var target in activeTestLogTargets.Values)
                RecycleLogTarget(target);

            activeTestLogTargets.Clear();
            foreach (var target in failedTestLogTargets)
                RecycleLogTarget(target);

            failedTestLogTargets.Clear();
            capturedLogEntryIndexes.Clear();
            capturedLogEntries.Clear();
            compilerMessageBuffer.Clear();
        }

        static void RecycleLogTarget(CapturedLogTarget target)
        {
            target.Reset();
            recycledTestLogTargets.Push(target);
        }

        sealed class TestRunCallbacks : IErrorCallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }
            public void RunFinished(ITestResultAdaptor result) => HandleTestRunFinished(result);
            public void TestStarted(ITestAdaptor test) => HandleTestStarted(test);
            public void TestFinished(ITestResultAdaptor result) => HandleTestFinished(result);
            public void OnError(string message) => HandleTestRunError(message);
        }

        struct CapturedLogEntry
        {
            public string Message;
            public string StackTrace;
            public int RepeatCount;
        }

        struct LogSignature
        {
            public string Message;
            public string StackTrace;
        }

        sealed class LogSignatureComparer : IEqualityComparer<LogSignature>
        {
            public static LogSignatureComparer Instance { get; } = new();

            public bool Equals(LogSignature x, LogSignature y)
                => x.Message == y.Message && x.StackTrace == y.StackTrace;

            public int GetHashCode(LogSignature obj)
            {
                unchecked
                {
                    var hashCode = StringComparer.Ordinal.GetHashCode(obj.Message);
                    return (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(obj.StackTrace);
                }
            }
        }

        sealed class CapturedLogTarget
        {
            public string Label { get; private set; } = string.Empty;
            public List<int> EntryIndexes { get; } = new();

            public void Reset(string label = "")
            {
                Label = label;
                EntryIndexes.Clear();
            }
        }
    }
}
