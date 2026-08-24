#nullable enable

using UnityEditor;

static class ConduitTestAssets
{
    internal const string TemporaryRoot = "Assets/ConduitMcpE2ETemp";

    internal static string GetTemporaryPath(string category, string fileName)
    {
        var assetPath = $"{TemporaryRoot}/{category}/{fileName}";
        EnsureParentFolders(assetPath);
        return assetPath;
    }

    internal static void CleanupTemporaryRoot()
    {
        if (AssetDatabase.IsValidFolder(TemporaryRoot))
            AssetDatabase.DeleteAsset(TemporaryRoot);
    }

    static void EnsureParentFolders(string assetPath)
    {
        var lastSlashIndex = assetPath.LastIndexOf('/');
        if (lastSlashIndex <= 0)
            return;

        var folderPath = assetPath[..lastSlashIndex];
        if (AssetDatabase.IsValidFolder(folderPath))
            return;

        var segments = folderPath.Split('/');
        var current = segments[0];
        for (var index = 1; index < segments.Length; index++)
        {
            var next = $"{current}/{segments[index]}";
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, segments[index]);

            current = next;
        }
    }
}
