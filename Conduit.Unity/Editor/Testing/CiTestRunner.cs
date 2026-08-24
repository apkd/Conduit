#nullable enable

#if UNITY_EDITOR
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using static UnityEditor.EnterPlayModeOptions;

namespace Conduit
{
    static class CiTestRunner
    {
        const string CommandLineFilterArgument = "-conduitTestFilter";
        const string CommandLineResultsArgument = "-conduitTestResults";
        const string TestResultsDirectory = "test-results";
        const string EditModeResultsFilename = "Edit mode tests.xml";
        const string PlayModeResultsFilename = "Play mode tests.xml";

        sealed class TestRunnerCallbacks : ICallbacks
        {
            internal Action<ITestResultAdaptor> RunFinished = static _ => { };
            internal Action<ITestResultAdaptor> TestFinished = static _ => { };

            void ICallbacks.RunStarted(ITestAdaptor testsToRun) { }

            void ICallbacks.RunFinished(ITestResultAdaptor result)
                => RunFinished?.Invoke(result);

            void ICallbacks.TestStarted(ITestAdaptor test) { }

            void ICallbacks.TestFinished(ITestResultAdaptor result)
                => TestFinished?.Invoke(result);
        }

        [SuppressMessage("ReSharper", "AsyncVoidMethod")]
        internal static async void RunAll()
        {
            EnsureConduitInitialized();
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = DisableDomainReload | DisableSceneReload;

            var testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
            var failCount = 0;

            try
            {
                Directory.CreateDirectory(TestResultsDirectory);

                var editModeResultsPath = Path.Combine(TestResultsDirectory, EditModeResultsFilename);
                var playModeResultsPath = Path.Combine(TestResultsDirectory, PlayModeResultsFilename);

                var editModeResult = await RunModeAsync(testRunnerApi, TestMode.EditMode, editModeResultsPath);
                failCount += editModeResult.FailCount;

                var playModeResult = await RunModeAsync(testRunnerApi, TestMode.PlayMode, playModeResultsPath);
                failCount += playModeResult.FailCount;
            }
            catch (Exception exception)
            {
                failCount = Math.Max(failCount, 1);
                Debug.LogException(exception);
            }
            finally
            {
                EditorApplication.Exit(failCount);
            }
        }

        [SuppressMessage("ReSharper", "AsyncVoidMethod")]
        internal static async void RunFilteredEditMode()
        {
            EnsureConduitInitialized();
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = DisableDomainReload | DisableSceneReload;

            var testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
            var resultsPath = CI.ResolveCommandLineValue(CommandLineResultsArgument) ?? Path.Combine(Application.persistentDataPath, "TestResults.xml");
            var testFilter = CI.ResolveCommandLineValue(CommandLineFilterArgument);
            var failCount = 0;

            try
            {
                EnsureOutputDirectoryExists(resultsPath);
                var result = await RunModeAsync(testRunnerApi, TestMode.EditMode, resultsPath, testFilter);
                failCount = result.FailCount;
            }
            catch (Exception exception)
            {
                failCount = Math.Max(failCount, 1);
                Debug.LogException(exception);
            }
            finally
            {
                EditorApplication.Exit(failCount);
            }
        }

        static async Task<ITestResultAdaptor> RunModeAsync(
            TestRunnerApi testRunnerApi,
            TestMode mode,
            string resultsPath,
            string? testFilter = null)
        {
            Console.WriteLine($"Running {mode} tests...");
            var completion = new TaskCompletionSource<ITestResultAdaptor>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            TestRunnerCallbacks callbacks = null!;
            callbacks = new()
            {
                TestFinished = static result =>
                {
                    Console.WriteLine($"[{result.Test.Name}] {result.ResultState}");
                    if (result.TestStatus is not TestStatus.Failed)
                        return;

                    if (!string.IsNullOrWhiteSpace(result.Message))
                        Console.WriteLine(result.Message);

                    if (!string.IsNullOrWhiteSpace(result.StackTrace))
                        Console.WriteLine(result.StackTrace);
                },
                RunFinished = result => completion.TrySetResult(result),
            };

            testRunnerApi.RegisterCallbacks(callbacks);
            Application.logMessageReceivedThreaded += ExpectUnityConnectLoginFailure;

            try
            {
                var filter = new Filter { testMode = mode };
                ApplyNameFilter(filter, testFilter);
                testRunnerApi.Execute(new(filter));

                var result = await completion.Task;
                Console.WriteLine($"Saving results to: {resultsPath}");
                TestRunnerApi.SaveResultToFile(result, resultsPath);
                return result;
            }
            finally
            {
                Application.logMessageReceivedThreaded -= ExpectUnityConnectLoginFailure;
                testRunnerApi.UnregisterCallbacks(callbacks);
            }

            static void ExpectUnityConnectLoginFailure(string message, string _, LogType type)
            {
                if (type is not LogType.Error
                    || message is not (
                        "UnityConnectLoginRequest: Failed to login - please check your username or password"
                        or "No sufficient permissions while processing request \"https://core.cloud.unity3d.com/api/login\", HTTP error code 401"
                    ))
                    return;

                // this listener is registered before the test framework's per-test listener,
                // so only the unavoidable authentication error enters its log scope as expected
                try
                {
                    UnityEngine.TestTools.LogAssert.Expect(type, message);
                }
                catch (InvalidOperationException) { } // no active test log scope
            }
        }

        static void EnsureOutputDirectoryExists(string outputPath)
        {
            if (Path.GetDirectoryName(outputPath) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);
        }

        static void ApplyNameFilter(Filter filter, string? rawTestFilter)
        {
            var testFilter = TestNameFilter.Normalize(rawTestFilter);
            if (testFilter is not { Length: > 0 })
                return;

            filter.groupNames = new[] { TestNameFilter.ToRegexPattern(testFilter) };
        }

        static void EnsureConduitInitialized()
        {
            ConduitToolRunner.Initialize();
        }
    }
}
#endif
