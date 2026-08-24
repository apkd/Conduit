#nullable enable

#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Conduit
{
    static class CiPlayerBuilder
    {
        const string CommandLinePlayerTargetArgument = "-conduitPlayerTarget";
        const string CommandLinePlayerOutputArgument = "-conduitPlayerOutput";

        internal static void Build(bool includeRuntime)
        {
            var exitCode = 1;
            var previousBackend = PlayerSettings.GetScriptingBackend(
                NamedBuildTarget.Standalone
            );
            var previousSplash = PlayerSettings.SplashScreen.show;
            try
            {
                var targetName = CI.ResolveCommandLineValue(
                    CommandLinePlayerTargetArgument
                ) ?? throw new ArgumentException(
                    $"{CommandLinePlayerTargetArgument} is required."
                );
                var output = CI.ResolveCommandLineValue(
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
                            "Packages/dev.tryfinally.conduit/Tests/EditMode/TestAssets/BridgeFixtureScene.unity",
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
    }
}
#endif
