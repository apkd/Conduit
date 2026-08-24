#nullable enable

using System;
using System.IO;
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
            if (Volatile.Read(ref pendingSceneFileChangeCount) == 0)
                return;

            try
            {
                using var pooledBlockedScenes = ConduitPool.GetPooledList<string>(out var blockedScenes);
                if (ReloadChangedOpenScenes(
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
            var scenePath = scene.path;
            if (string.IsNullOrWhiteSpace(scenePath))
                return;

            lock (gate)
            {
                knownSceneStamps.Remove(scenePath);
                pendingSceneFileChanges.Remove(scenePath);
                UpdatePendingChangeCount();
            }
        }

        static void OnSceneSaved(Scene scene) => RememberSceneStamp(scene);

        static void TryStartSceneFileWatcher(string assetsPath)
        {
            if (string.IsNullOrWhiteSpace(assetsPath) || !Directory.Exists(assetsPath))
                return;

            try
            {
                var watcher = new FileSystemWatcher(assetsPath, "*.unity")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };

                watcher.Changed += OnSceneFileWatcherEvent;
                watcher.Created += OnSceneFileWatcherEvent;
                watcher.Renamed += OnSceneFileRenamed;
                watcher.Deleted += OnSceneFileWatcherEvent;
                watcher.Error += OnSceneFileWatcherError;
                watcher.EnableRaisingEvents = true;
                sceneFileWatcher = watcher;
            }
            catch (Exception exception)
            {
                // refresh-time scanning still protects conduit commands when the watcher is unavailable.
                ConduitDiagnostics.Error("Failed to start open-scene disk change watcher.", exception);
            }
        }

        static void OnSceneFileWatcherEvent(object sender, FileSystemEventArgs args)
        {
            if (TryConvertAbsoluteScenePathToAssetPath(args.FullPath, out var sceneAssetPath))
                QueueSceneFileChange(sceneAssetPath, args.FullPath);
        }

        static void OnSceneFileRenamed(object sender, RenamedEventArgs args)
        {
            if (TryConvertAbsoluteScenePathToAssetPath(args.OldFullPath, out var oldSceneAssetPath))
                QueueSceneFileChange(oldSceneAssetPath, args.OldFullPath);

            if (TryConvertAbsoluteScenePathToAssetPath(args.FullPath, out var newSceneAssetPath))
                QueueSceneFileChange(newSceneAssetPath, args.FullPath);
        }

        static void OnSceneFileWatcherError(object sender, ErrorEventArgs args)
        {
            ConduitDiagnostics.Error("Open-scene disk change watcher reported an error.", args.GetException());
            lock (gate)
            {
                pendingSceneFileChanges.Clear();
                UpdatePendingChangeCount();
            }
        }

        static void QueueSceneFileChange(string sceneAssetPath, string absolutePath)
        {
            lock (gate)
            {
                pendingSceneFileChanges[sceneAssetPath] = new(
                    TryReadSceneFileStampFromAbsolutePath(absolutePath),
                    0d
                );
                UpdatePendingChangeCount();
            }
        }
    }
}
