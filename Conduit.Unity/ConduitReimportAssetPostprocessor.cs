#nullable enable

using UnityEditor;

namespace Conduit
{
    sealed class ConduitReimportAssetPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths
        ) => ConduitToolRunner.NotifyReimportedAssets(importedAssets);
    }
}
