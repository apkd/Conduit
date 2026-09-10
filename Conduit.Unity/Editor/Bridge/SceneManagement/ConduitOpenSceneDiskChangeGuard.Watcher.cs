#nullable enable

using System;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Conduit
{
    static partial class ConduitOpenSceneDiskChangeGuard
    {
        static void OnEditorUpdate()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now < nextSceneFileCheck)
                return;

            nextSceneFileCheck = now + FileChangeSettleSeconds;
            CheckForSceneChanges(now);
        }

        internal static void CheckForSceneChanges(double now)
        {
            PollOpenSceneFiles(now);
            if (Volatile.Read(ref pendingSceneFileChangeCount) == 0)
                return;

            try
            {
                using var pooledBlockedScenes = ConduitPool.GetPooledList<string>(out var blockedScenes);
                if (ReloadChangedOpenScenes(
                        now,
                        scanAllOpenScenes: false,
                        respectSettleDelay: true,
                        blockedScenes: blockedScenes
                    ) is { Length: > 0 } report)
                    ConduitDiagnostics.Info(report);

                if (blockedScenes.Count > 0)
                    ConduitDiagnostics.Warn(BuildBlockedDirtySceneDiagnostic("automatic scene reload", blockedScenes));
            }
            catch (Exception exception)
            {
                ConduitDiagnostics.Error("Failed while checking for open scenes changed on disk.", exception);
            }
        }

        static void OnSceneOpened(Scene scene, OpenSceneMode mode) => RememberSceneStamp(scene);

        static void OnSceneClosed(Scene scene)
        {
            if (!string.IsNullOrWhiteSpace(scene.path))
                ForgetScenePath(scene.path);
        }

        static void OnSceneSaved(Scene scene) => RememberSceneStamp(scene);

        static void PollOpenSceneFiles(double now)
        {
            // mono's recursive filesystem watcher can throw outside its error handler during folder deletion.
            // checking only open scene files also avoids scanning the entire asset tree on its polling backend.
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scenePath = SceneManager.GetSceneAt(index).path;
                if (string.IsNullOrWhiteSpace(scenePath))
                    continue;

                var stamp = TryReadSceneFileStamp(scenePath);
                lock (gate)
                {
                    if (observedSceneStamps.TryGetValue(scenePath, out var previousStamp)
                        && Nullable.Equals(previousStamp, stamp))
                        continue;

                    observedSceneStamps[scenePath] = stamp;
                    pendingSceneFileChanges[scenePath] = new(stamp, now);
                    UpdatePendingChangeCount();
                }
            }
        }
    }
}
