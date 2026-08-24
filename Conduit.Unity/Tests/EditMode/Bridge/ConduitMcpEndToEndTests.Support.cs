#nullable enable

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Conduit;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Search;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed partial class ConduitMcpEndToEndTests
{
    Task<McpToolCallResult> CallDetourAsync(string methodName, string replacementBody) =>
        client.CallToolAsync(
            BridgeCommandTypes.Detour,
            Args(
                ("projectPath", projectPath),
                ("methodName", methodName),
                ("replacementBody", replacementBody)
            )
        );

    static Dictionary<string, object?> Args(params (string key, object? value)[] pairs)
    {
        var dictionary = new Dictionary<string, object?>(pairs.Length, StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
            dictionary[key] = value;

        return dictionary;
    }

    static void AssertSuccessful(McpToolCallResult result, params string[] expectedSubstrings)
    {
        Assert.That(result.IsError, Is.False, result.Text);
        foreach (var expectedSubstring in expectedSubstrings)
            Assert.That(
                result.Text.IndexOf(expectedSubstring, StringComparison.OrdinalIgnoreCase),
                Is.GreaterThanOrEqualTo(0),
                result.Text
            );
    }

    static string GetSnippetFileName(string text)
    {
        const string prefix = "NAME: `";
        Assert.That(text, Does.StartWith(prefix));
        var lineEnd = text.IndexOf('\n');
        var nameEnd = lineEnd < 0 ? text.Length - 1 : lineEnd - 1;
        Assert.That(text[nameEnd], Is.EqualTo('`'));
        var fileName = text[prefix.Length..nameEnd];
        Assert.That(fileName, Does.Match("^[1-9][0-9]*\\.cs$"));
        return fileName;
    }

    static void AssertTextContainsAny(string text, params string[] expectedSubstrings)
    {
        foreach (var expectedSubstring in expectedSubstrings)
        {
            if (text.IndexOf(expectedSubstring, StringComparison.OrdinalIgnoreCase) >= 0)
                return;
        }

        Assert.Fail(text);
    }

    static string? TryFindGuidForAnyPathSuffix(string text, params string[] pathSuffixes)
    {
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < lines.Length; index++)
        {
            var separatorIndex = lines[index].IndexOf('|');
            if (separatorIndex <= 0)
                continue;

            var path = lines[index][(separatorIndex + 1)..].Trim();
            for (var suffixIndex = 0; suffixIndex < pathSuffixes.Length; suffixIndex++)
            {
                if (!path.EndsWith(pathSuffixes[suffixIndex], StringComparison.OrdinalIgnoreCase))
                    continue;

                return lines[index][..separatorIndex].Trim();
            }
        }

        return null;
    }

    void AssertCapturedImageExists(string resultText)
    {
        var relativePath = ExtractCapturedImagePath(resultText);
        var absolutePath = ToAbsoluteProjectPath(relativePath);
        Assert.That(File.Exists(absolutePath), Is.True, $"Expected captured image '{absolutePath}' to exist.");
        File.Delete(absolutePath);
    }

    static string ExtractCapturedImagePath(string resultText)
    {
        const string marker = " image captured: ";
        var markerIndex = resultText.IndexOf(marker, StringComparison.Ordinal);
        Assert.That(markerIndex, Is.GreaterThanOrEqualTo(0), resultText);
        return resultText[(markerIndex + marker.Length)..].Trim();
    }

    void ValidateCanonicalAssets()
    {
        using var pooled = ConduitPool.GetPooledList<string>(out var missingAssetPaths);
        var assetPaths = new[]
        {
            MaterialAsset,
            MissingScriptPrefabAsset,
            SceneAsset,
            DependencyAsset,
        };

        foreach (var assetPath in assetPaths)
        {
            if (AssetDatabase.LoadMainAssetAtPath(assetPath) != null)
                continue;

            missingAssetPaths.Add(assetPath);
        }

        if (missingAssetPaths.Count == 0)
            return;

        throw new AssertionException($"Missing canonical Conduit test assets:\n{string.Join("\n", missingAssetPaths)}");
    }

    void OpenSampleScene()
    {
        var activeScene = SceneManager.GetActiveScene();
        if (activeScene.path != SceneAsset || activeScene.isDirty || SceneManager.sceneCount != 1)
            EditorSceneManager.OpenScene(SceneAsset, OpenSceneMode.Single);

        WarmSearchProviders();
    }

    void WarmSearchProviders()
    {
        if (searchProvidersWarmed)
            return;

        using (var ctx1 = SearchService.CreateContext(new[] { "asset" }, "__conduit_search_prewarm__", SearchFlags.Synchronous))
            _ = SearchService.GetItems(ctx1, SearchFlags.Synchronous).ToArray();

        using (var ctx2 = SearchService.CreateContext(new[] { "scene" }, "t:GameObject", SearchFlags.Synchronous))
            _ = SearchService.GetItems(ctx2, SearchFlags.Synchronous).ToArray();

        searchProvidersWarmed = true;
    }

    string CreateTemporaryMaterialAssetCopy()
    {
        var assetPath = RegisterTemporaryAsset(ConduitTestAssets.GetTemporaryPath("Materials", $"Material_{Guid.NewGuid():N}.mat"));
        Assert.That(AssetDatabase.CopyAsset(MaterialAsset, assetPath), Is.True);
        return assetPath;
    }

    string CreateTemporarySceneAssetCopy()
    {
        var assetPath = RegisterTemporaryAsset(ConduitTestAssets.GetTemporaryPath("Scenes", $"Scene_{Guid.NewGuid():N}.unity"));
        var originalActiveScene = SceneManager.GetActiveScene();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        try
        {
            Assert.That(EditorSceneManager.SaveScene(scene, assetPath), Is.True);
        }
        finally
        {
            EditorSceneManager.CloseScene(scene, true);
            if (originalActiveScene.IsValid() && originalActiveScene.isLoaded)
                SceneManager.SetActiveScene(originalActiveScene);
        }

        return assetPath;
    }

    string CreateTemporaryScreenshotSceneAsset()
    {
        var assetPath = RegisterTemporaryAsset(ConduitTestAssets.GetTemporaryPath("Scenes", "ScreenshotScene.unity"));
        var originalActiveScene = SceneManager.GetActiveScene();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        try
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "ScreenshotCube";
            cube.transform.position = new(0f, 0.5f, 0f);
            SceneManager.MoveGameObjectToScene(cube, scene);
            Assert.That(EditorSceneManager.SaveScene(scene, assetPath), Is.True);
        }
        finally
        {
            EditorSceneManager.CloseScene(scene, true);
            if (originalActiveScene.IsValid() && originalActiveScene.isLoaded)
                SceneManager.SetActiveScene(originalActiveScene);
        }

        return assetPath;
    }

    string CreateInvalidProjectPath()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"ConduitMcpInvalidProject_{Guid.NewGuid():N}");
        temporaryDirectories.Add(directoryPath);
        return directoryPath;
    }

    string RegisterTemporaryAsset(string assetPath)
    {
        temporaryAssetPaths.Add(assetPath);
        return assetPath;
    }

    void CleanupTemporaryAssets()
    {
        for (var index = temporaryAssetPaths.Count - 1; index >= 0; index--)
            DeleteTemporaryAsset(temporaryAssetPaths[index]);

        temporaryAssetPaths.Clear();
    }

    void CleanupTemporaryDirectories()
    {
        for (var index = temporaryDirectories.Count - 1; index >= 0; index--)
        {
            try
            {
                if (Directory.Exists(temporaryDirectories[index]))
                    Directory.Delete(temporaryDirectories[index], true);
            }
            catch { }
        }

        temporaryDirectories.Clear();
    }

    void DeleteTemporaryAsset(string assetPath)
    {
        try
        {
            AssetDatabase.DeleteAsset(assetPath);
        }
        catch { }

        try
        {
            var absolutePath = ToAbsoluteProjectPath(assetPath);
            if (File.Exists(absolutePath))
                File.Delete(absolutePath);

            if (File.Exists(absolutePath + ".meta"))
                File.Delete(absolutePath + ".meta");
        }
        catch { }
    }

    string ToAbsoluteProjectPath(string assetPath)
        => Path.GetFullPath(Path.Combine(editorProjectPath, assetPath));

    static int GetSerializedInt(string assetPath, string propertyPath)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
        Assert.That(material, Is.Not.Null, $"Could not load material '{assetPath}'.");

        var property = new SerializedObject(material).FindProperty(propertyPath);
        Assert.That(property, Is.Not.Null, $"Could not find serialized property '{propertyPath}' in '{assetPath}'.");
        return property!.intValue;
    }
}
#endif
