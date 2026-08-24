#nullable enable

using System;
using System.Text;
using UnityEngine;

namespace Conduit
{
    sealed partial class ToolLogCapture
    {
        void EnsureHooked()
        {
            if (hooked)
                return;

            hooked = true;
            Application.logMessageReceivedThreaded += OnLogMessageReceived;
        }

        void OnLogMessageReceived(string condition, string stackTrace, LogType logType)
        {
            lock (gate)
            {
                // repeated logs bypass the comparatively expensive stack normalization path.
                if (hasLastRawEntry
                    && logType == lastRawLogType
                    && condition == lastRawMessage
                    && stackTrace == lastRawStackTrace)
                {
                    if (lastRawEntryIndex < 0)
                        return;

                    capturedLogEntries[lastRawEntryIndex].RepeatCount++;
                    ResolveLogTargetUnderLock().AddEntryIndex(lastRawEntryIndex);
                    return;
                }

                var rawCondition = condition;
                var rawStackTrace = stackTrace;
                if (ToolLogFormatter.ShouldSuppressCapturedLogEntry(
                        condition,
                        activeCommandKind
                    ))
                {
                    RememberRawEntry(-1);
                    return;
                }

                // burst diagnostics can embed assembly-qualified signatures longer than the useful error text
                condition = ToolLogFormatter.NormalizeCapturedLogMessage(condition);
                var target = ResolveLogTargetUnderLock();
                var simplifiedStackTrace = ToolLogFormatter.CleanCapturedStackTrace(
                    activeCommandKind,
                    stackTrace,
                    logType
                );
                RememberRawEntry(CaptureLogEntry(target, condition, simplifiedStackTrace, logType));

                void RememberRawEntry(int entryIndex)
                {
                    lastRawMessage = rawCondition;
                    lastRawStackTrace = rawStackTrace;
                    lastRawLogType = logType;
                    lastRawEntryIndex = entryIndex;
                    hasLastRawEntry = true;
                }
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

        int CaptureLogEntry(CapturedLogTarget target, string condition, string? simplifiedStackTrace, LogType logType)
        {
            var message = condition ?? string.Empty;
            var stack = simplifiedStackTrace ?? string.Empty;
            if (message.Length == 0 && stack.Length == 0)
                return -1;

            var signature = new LogSignature(message, stack, logType);
            if (capturedLogEntryIndexes.TryGetValue(signature, out var entryIndex))
            {
                capturedLogEntries[entryIndex].RepeatCount++;
                // entries are deduped globally while each target keeps its own first reference
                target.AddEntryIndex(entryIndex);
                return entryIndex;
            }

            entryIndex = capturedLogEntries.Count;
            capturedLogEntryIndexes.Add(signature, entryIndex);
            capturedLogEntries.Add(new(message, stack, logType));
            target.AddEntryIndex(entryIndex);
            return entryIndex;
        }

        string BuildCapturedLogs(CapturedLogTarget target, string? diagnostic)
        {
            if (target.EntryIndexes.Count == 0)
                return string.Empty;

            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            AppendCapturedLogEntries(target, builder, diagnostic);
            return builder.Trim().ToString();
        }

        string BuildTestLogs()
        {
            var includeAllLogs = RunTestsTool.ShouldIncludeAllTestLogs();
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            if (!includeAllLogs && HasAnyTestLogEntries())
            {
                builder.Append(RunTestsTool.LargeTestRunLogNote);
            }

            foreach (var testLogTarget in completedTestLogTargets)
            {
                if (!HasIncludedLogEntries(testLogTarget, includeAllLogs))
                    continue;

                ToolLogFormatter.AppendSectionSeparator(builder);
                builder.Append(testLogTarget.Failed ? "FAILED TEST: " : "TEST: ");
                builder.AppendLine(testLogTarget.Label);
                AppendCapturedLogEntries(testLogTarget, builder, includeAllLogs: includeAllLogs);
            }

            if (HasIncludedLogEntries(testRunLogTarget, includeAllLogs))
            {
                ToolLogFormatter.AppendSectionSeparator(builder);
                builder.AppendLine("TEST RUN:");
                AppendCapturedLogEntries(testRunLogTarget, builder, includeAllLogs: includeAllLogs);
            }

            return builder.Trim().ToString();
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
                if (ToolLogFormatter.ShouldIncludeTestLogEntry(
                        capturedLogEntries[entryIndex].LogType,
                        includeAllLogs
                    ))
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
                if (!ToolLogFormatter.ShouldIncludeTestLogEntry(entry.LogType, includeAllLogs)
                    || ToolLogFormatter.ShouldOmitDiagnosticLogEntry(entry.Message, diagnostic))
                    continue;

                if (!isFirstEntry)
                    ToolLogFormatter.AppendSectionSeparator(builder);

                ToolLogFormatter.AppendCapturedLogEntry(builder, entry);
                isFirstEntry = false;
            }
        }
    }
}
