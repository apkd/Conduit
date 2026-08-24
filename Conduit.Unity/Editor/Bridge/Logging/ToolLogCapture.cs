#nullable enable

using System;
using System.Collections.Generic;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Conduit
{
    sealed partial class ToolLogCapture
    {
        // unity invokes logMessageReceivedThreaded from worker threads
        readonly object gate = new();
        readonly CapturedLogTarget commandLogTarget = new();
        readonly CapturedLogTarget testRunLogTarget = new();
        readonly Dictionary<string, CapturedLogTarget> activeTestLogTargets = new(StringComparer.Ordinal);
        readonly List<CapturedLogTarget> completedTestLogTargets = new();
        // nested test callbacks make the latest started test the owner of subsequent logs
        readonly List<string> activeTestScopes = new();
        readonly Dictionary<LogSignature, int> capturedLogEntryIndexes = new();
        readonly List<CapturedLogEntry> capturedLogEntries = new();
        string? lastRawMessage;
        string? lastRawStackTrace;
        int lastRawEntryIndex;
        LogType lastRawLogType;
        bool hasLastRawEntry;
        BridgeCommandKind activeCommandKind;
        bool hooked;
        bool discardOnCompletion;

        internal void Start(BridgeCommandKind commandKind)
        {
            lock (gate)
            {
                ResetStateUnderLock();
                activeCommandKind = commandKind;
            }

            EnsureHooked();
        }

        internal string Drain(BridgeCommandKind commandKind, string outcome, string? diagnostic, out bool discardLogs)
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

        internal void DiscardOnCompletion()
        {
            lock (gate)
                discardOnCompletion = true;
        }

        internal void HandleTestStarted(ITestAdaptor test)
        {
            lock (gate)
            {
                if (!BridgeCommandKinds.IsTest(activeCommandKind))
                    return;

                activeTestScopes.Add(ToolLogFormatter.GetTestLabel(test));
            }
        }

        internal void HandleTestFinished(ITestResultAdaptor result)
        {
            lock (gate)
            {
                if (!BridgeCommandKinds.IsTest(activeCommandKind))
                    return;

                var label = ToolLogFormatter.GetTestLabel(result);
                RemoveActiveTestScope(label);
                if (!activeTestLogTargets.TryGetValue(label, out var target))
                    return;

                activeTestLogTargets.Remove(label);
                if (ToolLogFormatter.HasChildResults(result))
                    return;

                target.Failed = result.FailCount > 0;
                completedTestLogTargets.Add(target);
            }
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
            lastRawMessage = null;
            lastRawStackTrace = null;
            lastRawEntryIndex = 0;
            hasLastRawEntry = false;
            discardOnCompletion = false;
            RunTestsTool.ResetState();
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

        internal sealed class CapturedLogEntry
        {
            internal CapturedLogEntry(string message, string stackTrace, LogType logType)
            {
                Message = message;
                StackTrace = stackTrace;
                LogType = logType;
                RepeatCount = 1;
            }

            internal string Message { get; }
            internal string StackTrace { get; }
            internal LogType LogType { get; }
            internal int RepeatCount { get; set; }
        }

        readonly struct LogSignature : IEquatable<LogSignature>
        {
            internal LogSignature(string message, string stackTrace, LogType logType)
            {
                Message = message;
                StackTrace = stackTrace;
                LogType = logType;
                unchecked
                {
                    var hashCode = StringComparer.Ordinal.GetHashCode(message);
                    hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(stackTrace);
                    HashCode = (hashCode * 397) ^ (int)logType;
                }
            }

            internal string Message { get; }
            internal string StackTrace { get; }
            internal LogType LogType { get; }
            internal int HashCode { get; }

            public bool Equals(LogSignature other)
                => Message == other.Message
                   && StackTrace == other.StackTrace
                   && LogType == other.LogType;

            public override bool Equals(object? value)
                => value is LogSignature other && Equals(other);

            public override int GetHashCode() => HashCode;
        }

        sealed class CapturedLogTarget
        {
            readonly HashSet<int> entryIndexSet = new();
            int lastEntryIndex = -1;

            internal CapturedLogTarget() { }

            internal CapturedLogTarget(string label)
                => Label = label;

            internal string Label { get; private set; } = string.Empty;
            internal bool Failed { get; set; }
            internal List<int> EntryIndexes { get; } = new();

            internal void AddEntryIndex(int entryIndex)
            {
                if (entryIndex == lastEntryIndex || !entryIndexSet.Add(entryIndex))
                    return;

                EntryIndexes.Add(entryIndex);
                lastEntryIndex = entryIndex;
            }

            internal void Reset(string label = "")
            {
                Label = label;
                Failed = false;
                EntryIndexes.Clear();
                entryIndexSet.Clear();
                lastEntryIndex = -1;
            }
        }
    }
}
