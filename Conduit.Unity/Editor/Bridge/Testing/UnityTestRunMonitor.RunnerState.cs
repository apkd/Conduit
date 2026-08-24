#nullable enable

using System;
using System.Reflection;
using UnityEditor.TestTools.TestRunner.Api;

namespace Conduit
{
    sealed partial class UnityTestRunMonitor
    {
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
        [ThreadStatic] static object?[]? reusableInvokeArguments;

        internal bool IsAnyTestRunActive()
            => TryInvokeTestRunnerBoolMethod(testRunnerIsRunActiveMethod, out var isRunActive) && isRunActive;

        internal string? GetActiveTestRunMode()
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

        static bool TryInvokeTestRunnerBoolMethod(MethodInfo? method, out bool value)
            => TryInvokeTestRunnerBoolMethod(method, out value, null);

        static bool TryInvokeTestRunnerBoolMethod(
            MethodInfo? method,
            out bool value,
            object? argument)
        {
            value = false;
            if (method == null)
                return false;

            object?[]? arguments = null;
            try
            {
                if (argument != null)
                {
                    arguments = reusableInvokeArguments ?? new object?[1];
                    reusableInvokeArguments = null;
                    arguments[0] = argument;
                }

                if (method.Invoke(null, arguments) is bool result)
                {
                    value = result;
                    return true;
                }
            }
            catch (Exception)
            {
                // reflection failures are treated as unavailable test runner state
            }
            finally
            {
                if (arguments != null)
                {
                    arguments[0] = null;
                    reusableInvokeArguments ??= arguments;
                }
            }

            return false;
        }
    }
}
