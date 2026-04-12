#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor.TestTools.TestRunner.Api;

namespace Conduit
{
    static class run_tests
    {
        const string UserStoppedPlayModeTestRunSignal = "Playmode tests were aborted because the player was stopped.";
        static readonly List<string> filteredStartedTests = new();
        static readonly HashSet<string> filteredStartedTestSet = new(StringComparer.Ordinal);
        static string? activeTestFilterPattern;

        internal static void ApplyFilter(Filter filter, string? rawTestFilter)
        {
            ResetState();
            activeTestFilterPattern = NormalizeTestFilterPattern(rawTestFilter);
            if (activeTestFilterPattern is { Length: > 0 })
                filter.groupNames = new[] { BuildTestNameRegexPattern(activeTestFilterPattern) };
        }

        internal static bool ShouldBlockTestRun(bool isCompiling, bool isUpdating, bool isPlayingOrWillChangePlaymode)
            => isCompiling || isUpdating || isPlayingOrWillChangePlaymode;

        internal static bool ShouldFailTestRunForCompileErrors(bool scriptCompilationFailed)
            => scriptCompilationFailed;

        internal static string BuildBlockedTestRunDiagnostic(
            string commandType,
            bool isCompiling,
            bool isUpdating,
            bool isPlayingOrWillChangePlaymode)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.Append("Cannot start '");
            builder.Append(string.IsNullOrWhiteSpace(commandType) ? "run_tests" : commandType);
            builder.Append("' while Unity is busy:");

            var appendedReason = false;
            AppendReason("compiling scripts", isCompiling);
            AppendReason("updating assets", isUpdating);
            AppendReason("changing play mode", isPlayingOrWillChangePlaymode);
            builder.Append('.');
            return builder.ToString();

            void AppendReason(string reason, bool shouldAppend)
            {
                if (!shouldAppend)
                    return;

                builder.Append(appendedReason ? ", " : " ");
                builder.Append(reason);
                appendedReason = true;
            }
        }

        internal static string BuildCompileErrorTestRunDiagnostic(string commandType)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.Append("The project has compilation errors.");
            return builder.ToString();
        }

        internal static bool TryCreateUserStoppedPlayModeTestRunResult(
            string message,
            bool isPlayModeTestCommand,
            out BridgeCommandResult? result)
        {
            if (!IsUserStoppedPlayModeTestRun(message, isPlayModeTestCommand))
            {
                result = null;
                return false;
            }

            result = new()
            {
                outcome = ToolOutcome.Cancelled,
                diagnostic = "The user has manually stopped the play mode test run.",
            };
            return true;
        }

        internal static string BuildFailureSummary(ITestResultAdaptor result)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            AppendFailures(result, builder);
            return builder.Trim().ToString();
        }

        internal static string BuildCompletionSummary(ITestResultAdaptor result)
        {
            if (result.FailCount > 0)
                return BuildFailureSummary(result);

            if (result.PassCount > 0)
                return $"Passed {result.PassCount} tests.";

            if (result.SkipCount > 0 || result.TestStatus == TestStatus.Skipped)
            {
                var summary = $"Skipped {Math.Max(result.SkipCount, 1)} tests.";
                return TryFindSkippedMessage(result) is { Length: > 0 } message
                    ? $"{summary}\n\n{message}"
                    : summary;
            }

            if (result.InconclusiveCount > 0)
                return $"Inconclusive {result.InconclusiveCount} tests.";

            return "No tests ran.";
        }

        internal static void RecordStartedFilteredTest(ITestAdaptor test)
        {
            if (!HasActiveTestFilter() || test.IsSuite || test.HasChildren)
                return;

            RecordStartedFilteredTestLabel(GetTestLabel(test));
        }

        internal static void SetActiveFilterPattern(string? rawTestFilter)
            => activeTestFilterPattern = NormalizeTestFilterPattern(rawTestFilter);

        internal static void RecordStartedFilteredTestLabel(string label)
        {
            if (!HasActiveTestFilter() || string.IsNullOrWhiteSpace(label) || !filteredStartedTestSet.Add(label))
                return;

            filteredStartedTests.Add(label);
        }

        internal static string BuildFilteredTestRunDiagnostic(string diagnostic)
        {
            if (!HasActiveTestFilter())
                return diagnostic;

            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                builder.Append(diagnostic.TrimEnd());
                builder.AppendLine().AppendLine();
            }

            builder.AppendLine("RAN TESTS:");
            if (filteredStartedTests.Count == 0)
            {
                builder.Append("(none)");
                return builder.ToString();
            }

            foreach (var testLabel in filteredStartedTests)
                builder.AppendLine(testLabel);

            return builder.TrimEnd().ToString();
        }

        internal static string BuildTestNameRegexPattern(string testFilter)
        {
            var effectivePattern = testFilter.IndexOf('*') >= 0 || testFilter.IndexOf('?') >= 0
                ? testFilter
                : $"*{testFilter}*";
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.Append('^');
            foreach (var character in effectivePattern)
            {
                switch (character)
                {
                    case '*':
                        builder.Append(".*");
                        break;
                    case '?':
                        builder.Append('.');
                        break;
                    default:
                        AppendEscapedRegexCharacter(builder, character);
                        break;
                }
            }

            builder.Append('$');
            return builder.ToString();
        }

        internal static void ResetState()
        {
            activeTestFilterPattern = null;
            filteredStartedTests.Clear();
            filteredStartedTestSet.Clear();
        }

        static bool IsUserStoppedPlayModeTestRun(string message, bool isPlayModeTestCommand)
            => isPlayModeTestCommand
               && message is { Length: > 0 }
               && message.IndexOf(UserStoppedPlayModeTestRunSignal, StringComparison.Ordinal) >= 0;

        static bool AppendFailures(ITestResultAdaptor result, StringBuilder builder)
        {
            var appendedChildFailure = false;
            if (result.Children != null)
            {
                foreach (var child in result.Children)
                    appendedChildFailure |= AppendFailures(child, builder);
            }

            if (appendedChildFailure)
                return true;

            if (result.FailCount <= 0 || result.Message is not { Length: > 0 })
                return false;

            builder.AppendLine($"{result.Name}: {result.Message}");
            if (result.StackTrace is { Length: > 0 })
                builder.AppendLine(result.StackTrace);

            return true;
        }

        static string? TryFindSkippedMessage(ITestResultAdaptor result)
        {
            if (result.Children != null)
                foreach (var child in result.Children)
                    if (TryFindSkippedMessage(child) is { Length: > 0 } childMessage)
                        return childMessage;

            return result.TestStatus == TestStatus.Skipped && !string.IsNullOrWhiteSpace(result.Message)
                ? result.Message
                : null;
        }

        static bool HasActiveTestFilter()
            => !string.IsNullOrWhiteSpace(activeTestFilterPattern);

        static string? NormalizeTestFilterPattern(string? rawTestFilter)
        {
            if (rawTestFilter == null)
                return null;

            var trimmed = rawTestFilter.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        static string GetTestLabel(ITestAdaptor test)
            => string.IsNullOrWhiteSpace(test.FullName) ? test.Name : test.FullName;

        static void AppendEscapedRegexCharacter(StringBuilder builder, char character)
        {
            switch (character)
            {
                case '\\':
                case '.':
                case '$':
                case '^':
                case '{':
                case '[':
                case '(':
                case '|':
                case ')':
                case '+':
                case ']':
                case '}':
                    builder.Append('\\');
                    break;
            }

            builder.Append(character);
        }
    }
}
