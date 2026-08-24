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
    static partial class ConduitOpenSceneDiskChangeGuard
    {
        const double FileChangeSettleSeconds = 0.5d;
        const string AssetsPrefix = "Assets/";
        static readonly object gate = new();
        static readonly BindingFlags StaticNonPublic = BindingFlags.Static | BindingFlags.NonPublic;
        static readonly Dictionary<string, SceneFileStamp> knownSceneStamps = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, PendingSceneFileChange> pendingSceneFileChanges = new(StringComparer.OrdinalIgnoreCase);
        static int pendingSceneFileChangeCount;

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

        internal static void Initialize()
        {
            if (initialized)
                return;

            initialized = true;
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;

            var assetsPath = Application.dataPath;
            var rootPath = Path.GetFullPath(Path.Combine(assetsPath, ".."));
            projectRootPath = rootPath[^1] is '/' or '\\'
                ? rootPath
                : rootPath + Path.DirectorySeparatorChar;
            SnapshotOpenSceneStamps();
            TryStartSceneFileWatcher(assetsPath);

            EditorApplication.update += OnEditorUpdate;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosed += OnSceneClosed;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
        }

        internal static string? PrepareOpenScenesForAssetRefresh(string commandType)
        {
            Initialize();

            using var pooledBlockedScenes = ConduitPool.GetPooledList<string>(out var blockedScenes);
            if (ReloadChangedOpenScenes(
                    scanAllOpenScenes: true,
                    respectSettleDelay: false,
                    blockedScenes: blockedScenes
                ) is { Length: > 0 } report)
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
                UpdatePendingChangeCount();
            }

            try
            {
                sceneFileWatcher?.Dispose();
            }
            catch (Exception) { }

            sceneFileWatcher = null;
            initialized = false;
        }
    }
}
