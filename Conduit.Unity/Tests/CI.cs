#nullable enable

#if UNITY_EDITOR
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;
using JetBrains.Annotations;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
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
        const string CommandLinePlayerTargetArgument = "-conduitPlayerTarget";
        const string CommandLinePlayerOutputArgument = "-conduitPlayerOutput";
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

        /// <summary>Builds the development Mono player used by transport E2E jobs.</summary>
        public static void BuildPlayer()
            => BuildPlayer(includeRuntime: true);

        /// <summary>Builds a production Mono player and verifies that the opt-in runtime bridge is excluded.</summary>
        public static void BuildConsumerPlayer()
            => BuildPlayer(includeRuntime: false);

        static void BuildPlayer(bool includeRuntime)
        {
            var exitCode = 1;
            var previousBackend = PlayerSettings.GetScriptingBackend(
                NamedBuildTarget.Standalone
            );
            var previousSplash = PlayerSettings.SplashScreen.show;
            try
            {
                var targetName = ResolveCommandLineValue(
                    CommandLinePlayerTargetArgument
                ) ?? throw new ArgumentException(
                    $"{CommandLinePlayerTargetArgument} is required."
                );
                var output = ResolveCommandLineValue(
                    CommandLinePlayerOutputArgument
                ) ?? throw new ArgumentException(
                    $"{CommandLinePlayerOutputArgument} is required."
                );
                var target = targetName switch
                {
                    "linux" => BuildTarget.StandaloneLinux64,
                    "windows" => BuildTarget.StandaloneWindows64,
                    _ => throw new ArgumentException(
                        $"Unsupported player target '{targetName}'."
                    ),
                };

                if (Path.GetDirectoryName(output) is { Length: > 0 } directory)
                    Directory.CreateDirectory(directory);

                PlayerSettings.SetScriptingBackend(
                    NamedBuildTarget.Standalone,
                    ScriptingImplementation.Mono2x
                );
                PlayerSettings.SplashScreen.show = false;
                if (!includeRuntime)
                    EnsureRuntimeOptInDisabled();

                var report = BuildPipeline.BuildPlayer(
                    new BuildPlayerOptions
                    {
                        scenes = new[]
                        {
                            "Packages/dev.tryfinally.conduit/Tests/EditMode/TestAssets/Scenes/BridgeFixtureScene.unity",
                        },
                        locationPathName = output,
                        target = target,
                        options = includeRuntime
                            ? BuildOptions.Development
                            : BuildOptions.None,
                        extraScriptingDefines = includeRuntime
                            ? new[] { "CONDUIT_INCLUDE_IN_DEBUG_BUILDS" }
                            : Array.Empty<string>(),
                    }
                );
                if (report.summary.result != BuildResult.Succeeded)
                    throw new BuildFailedException(
                        $"Player build failed with {report.summary.totalErrors} error(s)."
                    );

                if (!includeRuntime)
                    EnsureRuntimeAssemblyExcluded(output);

                Console.WriteLine(
                    $"Built {targetName} player: {Path.GetFullPath(output)}"
                );
                exitCode = 0;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                PlayerSettings.SetScriptingBackend(
                    NamedBuildTarget.Standalone,
                    previousBackend
                );
                PlayerSettings.SplashScreen.show = previousSplash;
                EditorApplication.Exit(exitCode);
            }
        }

        static void EnsureRuntimeOptInDisabled()
        {
            var defines = PlayerSettings.GetScriptingDefineSymbols(
                NamedBuildTarget.Standalone
            );
            foreach (var define in defines.Split(';'))
                if (define.Trim() == "CONDUIT_INCLUDE_IN_DEBUG_BUILDS")
                    throw new BuildFailedException(
                        "The production consumer project enables CONDUIT_INCLUDE_IN_DEBUG_BUILDS."
                    );
        }

        static void EnsureRuntimeAssemblyExcluded(string output)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var roots = new[]
            {
                Path.Combine(projectRoot, "Library", "ScriptAssemblies"),
                Path.GetDirectoryName(Path.GetFullPath(output))!,
            };
            foreach (var root in roots)
            {
                if (!Directory.Exists(root))
                    continue;

                foreach (var assemblyPath in Directory.EnumerateFiles(
                             root,
                             "Conduit.Unity.Runtime.dll",
                             SearchOption.AllDirectories
                         ))
                    throw new BuildFailedException(
                        $"Production build contains the runtime bridge assembly: {assemblyPath}"
                    );
            }
        }

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
    }
}
#endif
