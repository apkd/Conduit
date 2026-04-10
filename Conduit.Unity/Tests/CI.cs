#nullable enable

#if UNITY_EDITOR
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;
using JetBrains.Annotations;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using static UnityEditor.EnterPlayModeOptions;

namespace Conduit
{
    [UsedImplicitly]
    public static class CI
    {
        const string CommandLineFilterArgument = "-conduitTestFilter";
        const string CommandLineResultsArgument = "-conduitTestResults";
        const string TestResultsDirectory = "test-results";
        const string EditModeResultsFilename = "Edit mode tests.xml";
        const string PlayModeResultsFilename = "Play mode tests.xml";

        sealed class TestRunnerCallbacks : ICallbacks
        {
            public Action<ITestResultAdaptor> RunFinished = static _ => { };
            public Action<ITestResultAdaptor> TestFinished = static _ => { };

            void ICallbacks.RunStarted(ITestAdaptor testsToRun) { }

            void ICallbacks.RunFinished(ITestResultAdaptor result)
                => RunFinished?.Invoke(result);

            void ICallbacks.TestStarted(ITestAdaptor test) { }

            void ICallbacks.TestFinished(ITestResultAdaptor result)
                => TestFinished?.Invoke(result);
        }

        [SuppressMessage("ReSharper", "AsyncVoidMethod")]
        public static async void RunTests()
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

        public static void RunFilteredEditModeTestsFromCommandLine()
            => RunFilteredEditModeTests();

        [SuppressMessage("ReSharper", "AsyncVoidMethod")]
        static async void RunFilteredEditModeTests()
        {
            EnsureConduitInitialized();
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = DisableDomainReload | DisableSceneReload;

            var testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
            var resultsPath = ResolveCommandLineValue(CommandLineResultsArgument) ?? Path.Combine(Application.persistentDataPath, "TestResults.xml");
            var testFilter = ResolveCommandLineValue(CommandLineFilterArgument);
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
            var completion = new TaskCompletionSource<ITestResultAdaptor>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                RunFinished = result =>
                {
                    try
                    {
                        WriteTestResults(resultsPath);
                        completion.TrySetResult(result);
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                    finally
                    {
                        testRunnerApi.UnregisterCallbacks(callbacks);
                    }
                },
            };

            testRunnerApi.RegisterCallbacks(callbacks);

            try
            {
                var filter = new Filter { testMode = mode };
                ApplyNameFilter(filter, testFilter);
                testRunnerApi.Execute(new(filter));
            }
            catch
            {
                testRunnerApi.UnregisterCallbacks(callbacks);
                throw;
            }

            return await completion.Task;
        }

        static void WriteTestResults(string outputPath)
        {
            var sourcePath = Path.Combine(Application.persistentDataPath, "TestResults.xml");
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("Unity did not produce a test results XML file.", sourcePath);

            Console.WriteLine($"Saving results to: {outputPath}");
            if (PathsEqual(sourcePath, outputPath))
                return;

            File.Copy(sourcePath, outputPath, overwrite: true);
        }

        static void EnsureOutputDirectoryExists(string outputPath)
        {
            if (Path.GetDirectoryName(outputPath) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);
        }

        static void ApplyNameFilter(Filter filter, string? rawTestFilter)
        {
            var testFilter = NormalizeFilter(rawTestFilter);
            if (testFilter is not { Length: > 0 })
                return;

            filter.groupNames = new[] { BuildTestNameRegexPattern(testFilter) };
        }

        static string? ResolveCommandLineValue(string argumentName)
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index < arguments.Length - 1; index++)
            {
                if (arguments[index] != argumentName)
                    continue;

                var value = arguments[index + 1].Trim();
                return value.Length == 0 ? null : value;
            }

            return null;
        }

        static string? NormalizeFilter(string? rawTestFilter)
        {
            if (rawTestFilter == null)
                return null;

            var trimmed = rawTestFilter.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        static string BuildTestNameRegexPattern(string testFilter)
        {
            var effectivePattern = testFilter.IndexOf('*') >= 0 || testFilter.IndexOf('?') >= 0
                ? testFilter
                : $"*{testFilter}*";
            var builder = new System.Text.StringBuilder("^");
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

        static void EnsureConduitInitialized()
        {
            ConduitToolRunner.Initialize();
        }

        static void AppendEscapedRegexCharacter(System.Text.StringBuilder builder, char character)
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

        static bool PathsEqual(string left, string right)
            => string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }
}
#endif
