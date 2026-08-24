#nullable enable

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Conduit
{
    static partial class ConduitOpenSceneDiskChangeGuard
    {
        static string ReloadChangedOpenScenes(
            bool scanAllOpenScenes,
            bool respectSettleDelay,
            List<string> blockedScenes)
        {
            using var pooledChangedScenePaths = ConduitPool.GetPooledList<string>(out var changedScenePaths);
            CollectChangedOpenScenePaths(scanAllOpenScenes, respectSettleDelay, changedScenePaths);
            if (changedScenePaths.Count == 0)
                return string.Empty;

            using var pooledReloadedScenes = ConduitPool.GetPooledList<string>(out var reloadedScenes);
            var allChangedScenesHandled = true;
            for (var index = 0; index < changedScenePaths.Count; index++)
            {
                var scenePath = changedScenePaths[index];
                var scene = FindOpenScene(scenePath);
                if (!scene.IsValid())
                {
                    ForgetScenePath(scenePath);
                    continue;
                }

                if (scene.isDirty)
                {
                    // reloading here would choose disk over unsaved editor memory without user intent.
                    allChangedScenesHandled = false;
                    blockedScenes.Add(scenePath);
                    continue;
                }

                if (!ReloadSceneFromDisk(scene))
                {
                    allChangedScenesHandled = false;
                    blockedScenes.Add(scenePath);
                    continue;
                }

                reloadedScenes.Add(scenePath);
                RememberSceneStamp(scenePath);
            }

            if (allChangedScenesHandled)
                TryClearOpenScenesChangedOnDisk();

            return BuildReloadReport(reloadedScenes);
        }

        static void CollectChangedOpenScenePaths(bool scanAllOpenScenes, bool respectSettleDelay, List<string> changedScenePaths)
        {
            var now = EditorApplication.timeSinceStartup;
            using var pooledPendingPaths = ConduitPool.GetPooledList<string>(out var pendingPaths);

            lock (gate)
            {
                if (scanAllOpenScenes)
                {
                    var sceneCount = SceneManager.sceneCount;
                    for (var sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
                    {
                        var scene = SceneManager.GetSceneAt(sceneIndex);
                        var scenePath = scene.path;
                        if (!string.IsNullOrWhiteSpace(scenePath))
                            pendingPaths.Add(scenePath);
                    }
                }
                else
                {
                    foreach (var scenePath in pendingSceneFileChanges.Keys)
                        pendingPaths.Add(scenePath);
                }
            }

            for (var index = 0; index < pendingPaths.Count; index++)
            {
                var scenePath = pendingPaths[index];
                if (!IsOpenScenePath(scenePath))
                {
                    ForgetScenePath(scenePath);
                    continue;
                }

                if (!IsSceneFileChangeSettled(scenePath, now, respectSettleDelay))
                    continue;

                if (!HasSceneFileChangedSinceKnownStamp(scenePath))
                {
                    RemovePendingSceneFileChange(scenePath);
                    continue;
                }

                changedScenePaths.Add(scenePath);
                RemovePendingSceneFileChange(scenePath);
            }
        }

        static bool IsSceneFileChangeSettled(string scenePath, double now, bool respectSettleDelay)
        {
            if (!respectSettleDelay)
                return true;

            lock (gate)
            {
                var currentStamp = TryReadSceneFileStamp(scenePath);
                if (currentStamp == null)
                {
                    // atomic replace and vcs checkout operations can briefly remove the target file.
                    pendingSceneFileChanges[scenePath] = new(null, now);
                    UpdatePendingChangeCount();

                    return false;
                }

                if (!pendingSceneFileChanges.TryGetValue(scenePath, out var pendingChange))
                {
                    // filesystem events often arrive before the writer has closed the new scene file.
                    pendingSceneFileChanges[scenePath] = new(currentStamp, now);
                    UpdatePendingChangeCount();

                    return false;
                }

                if (pendingChange.LastChangedAt <= 0d)
                {
                    pendingSceneFileChanges[scenePath] = new(currentStamp, now);
                    UpdatePendingChangeCount();

                    return false;
                }

                if (!Nullable.Equals(pendingChange.ObservedStamp, currentStamp))
                {
                    // wait for a stable size/time pair before asking unity to parse yaml from disk.
                    pendingSceneFileChanges[scenePath] = new(currentStamp, now);

                    return false;
                }

                return now - pendingChange.LastChangedAt >= FileChangeSettleSeconds;
            }
        }

        static bool HasSceneFileChangedSinceKnownStamp(string scenePath)
        {
            var currentStamp = TryReadSceneFileStamp(scenePath);
            if (currentStamp == null)
                return false;

            lock (gate)
            {
                if (!knownSceneStamps.TryGetValue(scenePath, out var knownStamp))
                {
                    knownSceneStamps[scenePath] = currentStamp.Value;
                    return false;
                }

                return knownStamp != currentStamp.Value;
            }
        }

        static bool ReloadSceneFromDisk(Scene scene)
        {
            if (TryReloadSceneWithInternalApi(scene))
                return true;

            return TryReloadSceneWithPublicFallback(scene);
        }

        static bool TryReloadSceneWithInternalApi(Scene scene)
        {
            if (reloadSceneMethod == null)
                return false;

            try
            {
                var result = reloadSceneMethod.Invoke(null, new object[] { scene });
                return result is bool reloaded && reloaded;
            }
            catch (Exception exception)
            {
                ConduitDiagnostics.Error($"Unity internal scene reload failed for '{scene.path}'.", exception);
                return false;
            }
        }

        static bool TryReloadSceneWithPublicFallback(Scene scene)
        {
            var scenePath = scene.path;
            if (string.IsNullOrWhiteSpace(scenePath))
                return false;

            // the public fallback restores the whole scene setup, so mixed dirty scenes are unsafe.
            if (AnyOpenSceneIsDirty())
                return false;

            try
            {
                if (SceneManager.sceneCount == 1 && SceneManager.GetActiveScene() == scene)
                {
                    EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                    return true;
                }

                var currentSetup = EditorSceneManager.GetSceneManagerSetup();
                if (currentSetup.Length == 0)
                    return false;

                EditorSceneManager.RestoreSceneManagerSetup(currentSetup);
                return true;
            }
            catch (Exception exception)
            {
                ConduitDiagnostics.Error($"Unity public scene reload fallback failed for '{scenePath}'.", exception);
                return false;
            }
        }

        static void TryClearOpenScenesChangedOnDisk()
        {
            if (clearOpenScenesChangedOnDiskMethod == null)
                return;

            try
            {
                clearOpenScenesChangedOnDiskMethod.Invoke(null, null);
            }
            catch (Exception exception)
            {
                ConduitDiagnostics.Error("Unity internal changed-on-disk state clear failed.", exception);
            }
        }

        static bool AnyOpenSceneIsDirty()
        {
            var sceneCount = SceneManager.sceneCount;
            for (var sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
                if (SceneManager.GetSceneAt(sceneIndex).isDirty)
                    return true;

            return false;
        }

        static Scene FindOpenScene(string scenePath)
        {
            var sceneCount = SceneManager.sceneCount;
            for (var sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                if (string.Equals(scene.path, scenePath, StringComparison.OrdinalIgnoreCase))
                    return scene;
            }

            return default;
        }

        static bool IsOpenScenePath(string scenePath) => FindOpenScene(scenePath).IsValid();
    }
}
