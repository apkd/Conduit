#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Conduit
{
    static class ToolLogFormatter
    {
        internal static string? TrimCommonTail(string? simplifiedStackTrace)
            => BridgeExceptionFormatter.TrimCommonLogTail(simplifiedStackTrace);

        internal static string? CleanCapturedStackTrace(BridgeCommandKind commandKind, string? stackTrace, LogType logType)
        {
            bool isTestCommand = BridgeCommandKinds.IsTest(commandKind);
            string? cleanedStackTrace = isTestCommand
                ? TrimCommonTail(BridgeExceptionFormatter.SimplifyStackTrace(stackTrace))
                : CleanCommandStackTrace(commandKind, stackTrace);

            return logType == LogType.Log
                   && !isTestCommand
                   && FirstFrameEquals(cleanedStackTrace, "UnityEngine.Debug:Log")
                ? null
                : cleanedStackTrace;
        }

        internal static string FormatCapturedLogEntryForTest(string message, string? stackTrace, int repeatCount = 1)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            var entry = new ToolLogCapture.CapturedLogEntry(
                message,
                stackTrace ?? string.Empty,
                LogType.Log
            )
            {
                RepeatCount = repeatCount,
            };
            AppendCapturedLogEntry(builder, entry);
            return builder.ToString();
        }

        internal static bool ShouldOmitDiagnosticLogEntry(string message, string? diagnostic)
        {
            if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(diagnostic))
                return false;

            // compiler diagnostics can arrive through the result and Unity's log callback
            return IsCompilerDiagnosticLogMessage(message)
                   && diagnostic!.Contains(message, StringComparison.Ordinal);
        }

        internal static bool ShouldSuppressCapturedLogEntry(string message)
            => BurstOutputFormatter.ShouldSuppressBurstDiagnostic(message);

        internal static bool ShouldSuppressCapturedLogEntry(string message, BridgeCommandKind commandKind)
            => ShouldSuppressCapturedLogEntry(message)
               || commandKind == BridgeCommandKind.ExecuteCode
                  && ExecuteCodeTool.ShouldSuppressCompilerWarning(message);

        internal static string NormalizeCapturedLogMessage(string message)
            => BurstOutputFormatter.IsBurstDiagnostic(message)
                ? BurstOutputFormatter.SimplifyBurstDiagnostic(message)
                : message;

        internal static bool ShouldIncludeTestLogEntry(LogType logType, bool includeAllLogs)
            => includeAllLogs || IsErrorLogType(logType);

        internal static void AppendCapturedLogEntry(
            StringBuilder builder,
            ToolLogCapture.CapturedLogEntry entry)
            => BridgeLogFormatter.Append(
                builder,
                entry.Message,
                entry.StackTrace,
                entry.RepeatCount
            );

        internal static void AppendSectionSeparator(StringBuilder builder)
        {
            if (builder.Length == 0)
                return;

            builder.Append("\n\n");
        }

        static string? CleanCommandStackTrace(BridgeCommandKind commandKind, string? stackTrace)
        {
            if (commandKind == BridgeCommandKind.ExecuteCode
                && TryTrimExecuteCodeInvocationStack(stackTrace, out string executeCodeStackTrace))
                return BridgeExceptionFormatter.SimplifyStackTrace(executeCodeStackTrace);

            return TrimCommonTail(BridgeExceptionFormatter.SimplifyStackTrace(stackTrace));
        }

        static bool TryTrimExecuteCodeInvocationStack(string? stackTrace, out string trimmedStackTrace)
        {
            trimmedStackTrace = string.Empty;
            if (string.IsNullOrWhiteSpace(stackTrace))
                return false;

            // execute_code runner frames are hidden from simplified stacks, so the boundary must be found
            // from raw unity frames before conduit/generated frames are removed.
            using var pooledFrames = ConduitPool.GetPooledList<string>(out var frames);
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

            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            for (int index = 0; index < count; index++)
            {
                if (builder.Length > 0)
                    builder.AppendLine();

                builder.Append(frames[index]);
            }

            return builder.ToString();
        }

        internal static string GetTestLabel(ITestAdaptor test)
            => string.IsNullOrWhiteSpace(test.FullName) ? test.Name : test.FullName;

        internal static string GetTestLabel(ITestResultAdaptor result)
            => string.IsNullOrWhiteSpace(result.FullName) ? result.Name : result.FullName;

        internal static bool HasChildResults(ITestResultAdaptor result)
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
            => frame.Contains("Conduit.ExecuteCodeTool", StringComparison.Ordinal)
               && (frame.Contains("InvokeAsync", StringComparison.Ordinal)
                   || frame.Contains("ExecuteCachedCompilationAsync", StringComparison.Ordinal)
                   || frame.Contains("ExecuteAsync", StringComparison.Ordinal));

        static bool FirstFrameEquals(string? stackTrace, string frameName)
        {
            if (string.IsNullOrWhiteSpace(stackTrace))
                return false;

            var value = stackTrace.AsSpan();
            var lineEnd = value.IndexOf('\n');
            var firstFrame = lineEnd < 0 ? value : value[..lineEnd];
            while (!firstFrame.IsEmpty && char.IsWhiteSpace(firstFrame[^1]))
                firstFrame = firstFrame[..^1];

            return firstFrame.SequenceEqual(frameName.AsSpan());
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
    }
}
