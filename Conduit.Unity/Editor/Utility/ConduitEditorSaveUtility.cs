#nullable enable

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Conduit
{
    static class ConduitEditorSaveUtility
    {
        internal static bool TrySave()
        {
            // custom windows own their save contracts; never discard their unsaved documents
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
                if (window.hasUnsavedChanges)
                    return false;

            ConduitSceneCommandUtility.SaveScenes(null);
            if (PrefabStageUtility.GetCurrentPrefabStage() is { } stage && stage.scene.isDirty)
                ConduitSceneCommandUtility.SavePrefabStage(stage);
            AssetDatabase.SaveAssets();

            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
                if (window.hasUnsavedChanges)
                    return false;

            // save callbacks may skip assets without throwing; imported objects do not use SaveAssets
            foreach (var asset in Resources.FindObjectsOfTypeAll<Object>())
                if (EditorUtility.IsDirty(asset) && AssetDatabase.IsNativeAsset(asset))
                    return false;

            return ConduitSceneCommandUtility.GetDirtySceneDescriptions().Length == 0;
        }
    }
}
