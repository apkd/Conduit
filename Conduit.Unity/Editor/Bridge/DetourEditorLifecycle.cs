#nullable enable

using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Conduit
{
    [InitializeOnLoad]
    static class DetourEditorLifecycle
    {
        const string PendingSnapshotsKey = "Conduit.Detours.PendingSnapshots";
        const string RestoredOnExitKey = "Conduit.Detours.RestoredOnExit";

        static DetourEditorLifecycle()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += RestoreUnexpectedDetours;
            AssemblyReloadEvents.beforeAssemblyReload += PreserveBeforeAssemblyReload;
            EditorApplication.delayCall += ReapplyPendingSnapshots;
        }

        internal static string BuildCompletionSuffix(bool targetPlayMode)
        {
            if (targetPlayMode)
            {
                int count = DetourRuntime.ActiveCount;
                return count == 0 ? string.Empty : $" Active detours: {count}.";
            }

            int restored = SessionState.GetInt(RestoredOnExitKey, 0);
            SessionState.EraseInt(RestoredOnExitKey);
            return restored == 0 ? string.Empty : $" Restored detours: {restored}.";
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                    if (!EditorSettings.enterPlayModeOptionsEnabled
                        || (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0)
                        PreserveForPlayMode();
                    break;
                case PlayModeStateChange.EnteredPlayMode:
                    ReapplyPendingSnapshots();
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    RestoreForPlayModeExit();
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    RestoreUnexpectedDetours();
                    ClearPendingSnapshots();
                    break;
            }
        }

        static void PreserveForPlayMode()
        {
            if (DetourRuntime.ActiveCount == 0)
            {
                ClearPendingSnapshots();
                return;
            }

            // native jumps must be removed before Mono tears down the domain that owns their code.
            PersistSnapshots();
            DetourRuntime.RestoreAll();
        }

        static void PreserveBeforeAssemblyReload()
        {
            if (DetourRuntime.ActiveCount == 0)
                return;

            if (EditorApplication.isPlayingOrWillChangePlaymode)
                PersistSnapshots();
            else
                ClearPendingSnapshots();

            DetourRuntime.RestoreAll();
        }

        static void PersistSnapshots()
        {
            var manifest = new SnapshotManifest
            {
                snapshots = DetourRuntime.GetSnapshots()
                    .Select(static snapshot => SnapshotRecord.FromSnapshot(snapshot))
                    .ToArray(),
            };
            SessionState.SetString(PendingSnapshotsKey, JsonUtility.ToJson(manifest));
        }

        static void ReapplyPendingSnapshots()
        {
            var json = SessionState.GetString(PendingSnapshotsKey, string.Empty);
            if (json.Length == 0)
                return;

            if (!EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            try
            {
                var manifest = JsonUtility.FromJson<SnapshotManifest>(json);
                foreach (var record in manifest.snapshots)
                    DetourRuntime.Reapply(record.ToSnapshot());
                ClearPendingSnapshots();
            }
            catch (Exception exception)
            {
                ClearPendingSnapshots();
                RestoreUnexpectedDetours();
                Debug.LogError($"Conduit could not reapply detours after the script domain reload: {exception}");
            }
        }

        static void RestoreForPlayModeExit()
        {
            var restored = DetourRuntime.RestoreAll();
            if (restored > 0)
                SessionState.SetInt(RestoredOnExitKey, restored);
            ClearPendingSnapshots();
        }

        static void RestoreUnexpectedDetours()
        {
            try
            {
                DetourRuntime.RestoreAll();
            }
            catch (Exception exception)
            {
                Debug.LogError($"Conduit could not restore one or more detours: {exception}");
            }
        }

        static void ClearPendingSnapshots() => SessionState.EraseString(PendingSnapshotsKey);

        [Serializable]
        sealed class SnapshotManifest
        {
            public SnapshotRecord[] snapshots = Array.Empty<SnapshotRecord>();
        }

        [Serializable]
        sealed class SnapshotRecord
        {
            public string moduleVersionId = string.Empty;
            public string metadataToken = string.Empty;
            public string signatureHash = string.Empty;
            public string canonicalName = string.Empty;
            public string declaration = string.Empty;
            public string assembly = string.Empty;
            public string? symbols;
            public string generatedTypeName = string.Empty;
            public string displayName = string.Empty;

            internal static SnapshotRecord FromSnapshot(DetourSnapshot snapshot)
                => new()
                {
                    moduleVersionId = snapshot.ModuleVersionId,
                    metadataToken = snapshot.MetadataToken,
                    signatureHash = snapshot.SignatureHash,
                    canonicalName = snapshot.CanonicalName,
                    declaration = snapshot.Declaration,
                    assembly = Convert.ToBase64String(snapshot.AssemblyBytes),
                    symbols = snapshot.PdbBytes == null ? null : Convert.ToBase64String(snapshot.PdbBytes),
                    generatedTypeName = snapshot.GeneratedTypeName,
                    displayName = snapshot.DisplayName,
                };

            internal DetourSnapshot ToSnapshot()
                => new()
                {
                    ModuleVersionId = moduleVersionId,
                    MetadataToken = metadataToken,
                    SignatureHash = signatureHash,
                    CanonicalName = canonicalName,
                    Declaration = declaration,
                    AssemblyBytes = Convert.FromBase64String(assembly),
                    PdbBytes = symbols == null ? null : Convert.FromBase64String(symbols),
                    GeneratedTypeName = generatedTypeName,
                    DisplayName = displayName,
                };
        }
    }
}
