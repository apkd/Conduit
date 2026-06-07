#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Conduit
{
    static class ConduitOpenSceneDiskChangeGuard
    {
        const double FileChangeSettleSeconds = 0.5d;
        const string AssetsPrefix = "Assets/";
        static readonly object gate = new();
        static readonly BindingFlags StaticNonPublic = BindingFlags.Static | BindingFlags.NonPublic;
        static readonly Dictionary<string, SceneFileStamp> knownSceneStamps = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, PendingSceneFileChange> pendingSceneFileChanges = new(StringComparer.OrdinalIgnoreCase);

        // unity keeps the prompt decision behind native editor bindings.
        // using them before refresh avoids entering the modal loop where conduit cannot pump commands.
        static readonly MethodInfo? reloadSceneMethod = typeof(EditorSceneManager).GetMethod(
            "ReloadScene",
            StaticNonPublic,
            null,
            new[] { typeof(Scene) },
            null
        );
        static readonly MethodInfo? clearOpenScenesChangedOnDiskMethod = typeof(EditorSceneManager).GetMethod(
            "ClearOpenScenesChangedOnDisk",
            StaticNonPublic
        );

        static bool initialized;
        static string? projectRootPath;
        static FileSystemWatcher? sceneFileWatcher;

        public static void Initialize()
        {
            if (initialized)
                return;

            initialized = true;
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;

            projectRootPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            SnapshotOpenSceneStamps();
            TryStartSceneFileWatcher();

            EditorApplication.update += OnEditorUpdate;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosed += OnSceneClosed;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
        }

        public static string? PrepareOpenScenesForAssetRefresh(string commandType)
        {
            Initialize();

            if (ReloadChangedOpenScenes(scanAllOpenScenes: true, respectSettleDelay: false, out var blockedScenes) is { Length: > 0 } report)
                ConduitDiagnostics.Info(report);

            return blockedScenes.Count == 0 ? null : BuildBlockedDirtySceneDiagnostic(commandType, blockedScenes);
        }

        static void Shutdown()
        {
            EditorApplication.update -= OnEditorUpdate;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneClosed -= OnSceneClosed;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;

            lock (gate)
            {
                pendingSceneFileChanges.Clear();
                knownSceneStamps.Clear();
            }

            try
            {
                sceneFileWatcher?.Dispose();
            }
            catch (Exception) { }

            sceneFileWatcher = null;
            initialized = false;
        }

        static void OnEditorUpdate()
        {
            try
            {
                if (ReloadChangedOpenScenes(scanAllOpenScenes: false, respectSettleDelay: true, out var blockedScenes) is { Length: > 0 } report)
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
            if (string.IsNullOrWhiteSpace(scene.path))
                return;

            lock (gate)
            {
                knownSceneStamps.Remove(scene.path);
                pendingSceneFileChanges.Remove(scene.path);
            }
        }

        static void OnSceneSaved(Scene scene) => RememberSceneStamp(scene);

        static void TryStartSceneFileWatcher()
        {
            if (string.IsNullOrWhiteSpace(Application.dataPath) || !Directory.Exists(Application.dataPath))
                return;

            try
            {
                var watcher = new FileSystemWatcher(Application.dataPath, "*.unity")
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
                QueueSceneFileChange(sceneAssetPath);
        }

        static void OnSceneFileRenamed(object sender, RenamedEventArgs args)
        {
            if (TryConvertAbsoluteScenePathToAssetPath(args.OldFullPath, out var oldSceneAssetPath))
                QueueSceneFileChange(oldSceneAssetPath);

            if (TryConvertAbsoluteScenePathToAssetPath(args.FullPath, out var newSceneAssetPath))
                QueueSceneFileChange(newSceneAssetPath);
        }

        static void OnSceneFileWatcherError(object sender, ErrorEventArgs args)
        {
            ConduitDiagnostics.Error("Open-scene disk change watcher reported an error.", args.GetException());
            lock (gate)
                pendingSceneFileChanges.Clear();
        }

        static void QueueSceneFileChange(string sceneAssetPath)
        {
            lock (gate)
            {
                pendingSceneFileChanges[sceneAssetPath] = new()
                {
                    observed_stamp = TryReadSceneFileStamp(sceneAssetPath),
                    last_changed_at = 0d,
                };
            }
        }

        static string ReloadChangedOpenScenes(bool scanAllOpenScenes, bool respectSettleDelay, out List<string> blockedScenes)
        {
            blockedScenes = new();

            using var pooledChangedScenePaths = ConduitUtility.GetPooledList<string>(out var changedScenePaths);
            CollectChangedOpenScenePaths(scanAllOpenScenes, respectSettleDelay, changedScenePaths);
            if (changedScenePaths.Count == 0)
                return string.Empty;

            using var pooledReloadedScenes = ConduitUtility.GetPooledList<string>(out var reloadedScenes);
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
                    blockedScenes.Add(scene.path);
                    continue;
                }

                if (!ReloadSceneFromDisk(scene))
                {
                    allChangedScenesHandled = false;
                    blockedScenes.Add(scene.path);
                    continue;
                }

                reloadedScenes.Add(scene.path);
                RememberSceneStamp(scene.path);
            }

            if (allChangedScenesHandled)
                TryClearOpenScenesChangedOnDisk();

            return BuildReloadReport(reloadedScenes);
        }

        static void CollectChangedOpenScenePaths(bool scanAllOpenScenes, bool respectSettleDelay, List<string> changedScenePaths)
        {
            var now = EditorApplication.timeSinceStartup;
            using var pooledPendingPaths = ConduitUtility.GetPooledList<string>(out var pendingPaths);

            lock (gate)
            {
                if (scanAllOpenScenes)
                {
                    for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
                    {
                        var scene = SceneManager.GetSceneAt(sceneIndex);
                        if (!string.IsNullOrWhiteSpace(scene.path))
                            pendingPaths.Add(scene.path);
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
                    pendingSceneFileChanges[scenePath] = new()
                    {
                        observed_stamp = null,
                        last_changed_at = now,
                    };

                    return false;
                }

                if (!pendingSceneFileChanges.TryGetValue(scenePath, out var pendingChange))
                {
                    // filesystem events often arrive before the writer has closed the new scene file.
                    pendingSceneFileChanges[scenePath] = new()
                    {
                        observed_stamp = currentStamp,
                        last_changed_at = now,
                    };

                    return false;
                }

                if (pendingChange.last_changed_at <= 0d)
                {
                    pendingSceneFileChanges[scenePath] = new()
                    {
                        observed_stamp = currentStamp,
                        last_changed_at = now,
                    };

                    return false;
                }

                if (!Nullable.Equals(pendingChange.observed_stamp, currentStamp))
                {
                    // wait for a stable size/time pair before asking unity to parse yaml from disk.
                    pendingSceneFileChanges[scenePath] = new()
                    {
                        observed_stamp = currentStamp,
                        last_changed_at = now,
                    };

                    return false;
                }

                return now - pendingChange.last_changed_at >= FileChangeSettleSeconds;
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
            if (string.IsNullOrWhiteSpace(scene.path))
                return false;

            // the public fallback restores the whole scene setup, so mixed dirty scenes are unsafe.
            if (AnyOpenSceneIsDirty())
                return false;

            try
            {
                if (SceneManager.sceneCount == 1 && SceneManager.GetActiveScene() == scene)
                {
                    EditorSceneManager.OpenScene(scene.path, OpenSceneMode.Single);
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
                ConduitDiagnostics.Error($"Unity public scene reload fallback failed for '{scene.path}'.", exception);
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
            for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
                if (SceneManager.GetSceneAt(sceneIndex).isDirty)
                    return true;

            return false;
        }

        static Scene FindOpenScene(string scenePath)
        {
            for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                if (string.Equals(scene.path, scenePath, StringComparison.OrdinalIgnoreCase))
                    return scene;
            }

            return default;
        }

        static bool IsOpenScenePath(string scenePath) => FindOpenScene(scenePath).IsValid();

        static void SnapshotOpenSceneStamps()
        {
            for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
                RememberSceneStamp(SceneManager.GetSceneAt(sceneIndex));
        }

        static void RememberSceneStamp(Scene scene)
        {
            if (!string.IsNullOrWhiteSpace(scene.path))
                RememberSceneStamp(scene.path);
        }

        static void RememberSceneStamp(string scenePath)
        {
            var stamp = TryReadSceneFileStamp(scenePath);
            lock (gate)
            {
                if (stamp == null)
                    knownSceneStamps.Remove(scenePath);
                else
                    knownSceneStamps[scenePath] = stamp.Value;

                pendingSceneFileChanges.Remove(scenePath);
            }
        }

        static void ForgetScenePath(string scenePath)
        {
            lock (gate)
            {
                knownSceneStamps.Remove(scenePath);
                pendingSceneFileChanges.Remove(scenePath);
            }
        }

        static void RemovePendingSceneFileChange(string scenePath)
        {
            lock (gate)
                pendingSceneFileChanges.Remove(scenePath);
        }

        static SceneFileStamp? TryReadSceneFileStamp(string scenePath)
        {
            if (!TryConvertAssetPathToAbsolutePath(scenePath, out var absolutePath))
                return null;

            try
            {
                if (!File.Exists(absolutePath))
                    return null;

                var fileInfo = new FileInfo(absolutePath);
                return new(fileInfo.Length, fileInfo.LastWriteTimeUtc);
            }
            catch
            {
                return null;
            }
        }

        static bool TryConvertAbsoluteScenePathToAssetPath(string absolutePath, out string sceneAssetPath)
        {
            sceneAssetPath = string.Empty;
            if (string.IsNullOrWhiteSpace(projectRootPath))
                return false;

            try
            {
                var fullPath = Path.GetFullPath(absolutePath);
                var rootPath = projectRootPath!;
                if (!rootPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                    && !rootPath.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    rootPath += Path.DirectorySeparatorChar;

                if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                    return false;

                var relativePath = fullPath[rootPath.Length..].Replace(Path.DirectorySeparatorChar, '/');
                if (!relativePath.StartsWith(AssetsPrefix, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (!relativePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    return false;

                sceneAssetPath = relativePath;
                return true;
            }
            catch
            {
                return false;
            }
        }

        static bool TryConvertAssetPathToAbsolutePath(string assetPath, out string absolutePath)
        {
            absolutePath = string.Empty;
            if (string.IsNullOrWhiteSpace(projectRootPath) || string.IsNullOrWhiteSpace(assetPath))
                return false;

            if (!assetPath.StartsWith(AssetsPrefix, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(assetPath, "Assets", StringComparison.OrdinalIgnoreCase))
                return false;

            absolutePath = Path.GetFullPath(Path.Combine(projectRootPath, assetPath.Replace('/', Path.DirectorySeparatorChar)));
            return true;
        }

        static string BuildReloadReport(List<string> reloadedScenes)
        {
            if (reloadedScenes.Count == 0)
                return string.Empty;

            reloadedScenes.Sort(StringComparer.Ordinal);
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.AppendLine("Reloaded open scene(s) changed on disk:");
            for (var index = 0; index < reloadedScenes.Count; index++)
            {
                builder.Append("- ");
                builder.AppendLine(reloadedScenes[index]);
            }

            return builder.TrimEnd().ToString();
        }

        static string BuildBlockedDirtySceneDiagnostic(string commandType, List<string> blockedScenes)
        {
            blockedScenes.Sort(StringComparer.Ordinal);
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.Append("Cannot run '");
            builder.Append(commandType);
            builder.AppendLine("' because open scene file(s) changed on disk and could not be reloaded automatically.");
            builder.AppendLine("Blocked scenes:");
            for (var index = 0; index < blockedScenes.Count; index++)
            {
                builder.Append("- ");
                builder.AppendLine(blockedScenes[index]);
            }

            builder.AppendLine("This usually means Unity has unsaved in-memory scene changes.");
            builder.Append("Use '");
            builder.Append(BridgeCommandTypes.DiscardScenes);
            builder.Append("' to reload the on-disk scene version, or '");
            builder.Append(BridgeCommandTypes.SaveScenes);
            builder.Append("' to keep Unity's in-memory version.");
            return builder.ToString();
        }

        readonly struct SceneFileStamp : IEquatable<SceneFileStamp>
        {
            readonly long length;
            readonly DateTime lastWriteTimeUtc;

            public SceneFileStamp(long length, DateTime lastWriteTimeUtc)
            {
                this.length = length;
                this.lastWriteTimeUtc = lastWriteTimeUtc;
            }

            public bool Equals(SceneFileStamp other)
                => length == other.length && lastWriteTimeUtc == other.lastWriteTimeUtc;

            public override bool Equals(object? obj)
                => obj is SceneFileStamp other && Equals(other);

            public override int GetHashCode()
                => unchecked((length.GetHashCode() * 397) ^ lastWriteTimeUtc.GetHashCode());

            public static bool operator ==(SceneFileStamp left, SceneFileStamp right) => left.Equals(right);

            public static bool operator !=(SceneFileStamp left, SceneFileStamp right) => !left.Equals(right);
        }

        struct PendingSceneFileChange
        {
            public SceneFileStamp? observed_stamp;
            public double last_changed_at;
        }
    }
}
