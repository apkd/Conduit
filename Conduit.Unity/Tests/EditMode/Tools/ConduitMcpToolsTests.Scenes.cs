#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NUnit.Framework;
using Conduit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public sealed partial class ConduitMcpToolsTests
{
    [Test]
    public void SaveScenes_SavesDirtyOpenScene()
    {
        var assetPath = ConduitTestAssets.GetTemporaryPath(
            "UnitTests",
            $"SaveScene_{Guid.NewGuid():N}.unity"
        );
        CreateTemporaryScreenshotSceneAsset(assetPath);
        var scene = EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
        try
        {
            EditorSceneManager.MarkSceneDirty(scene);

            var result = ConduitSceneCommandUtility.SaveScenes(assetPath);

            Assert.That(scene.isDirty, Is.False);
            Assert.That(result, Does.Contain("Saved scene"));
            Assert.That(result, Does.Contain(assetPath));
        }
        finally
        {
            if (scene.isLoaded)
                EditorSceneManager.CloseScene(scene, true);

            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void DiscardScenes_ReloadsDirtySceneWithoutSaving()
    {
        var temporaryObject = new GameObject("ConduitDiscardScenesTemp");
        SceneManager.MoveGameObjectToScene(temporaryObject, SceneManager.GetActiveScene());
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

        var result = ConduitSceneCommandUtility.DiscardScenes(null);

        Assert.That(GameObject.Find("ConduitDiscardScenesTemp"), Is.Null);
        Assert.That(SceneManager.GetActiveScene().isDirty, Is.False);
        Assert.That(result, Does.Contain("Discarded scene changes:"));
        Assert.That(result, Does.Contain(SceneAsset));
    }

    [Test]
    public void DiscardScenes_SpecifiedSoleActiveSceneRetainsItsPathInTheResult()
    {
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

        var result = ConduitSceneCommandUtility.DiscardScenes(SceneAsset);

        Assert.That(result, Is.EqualTo($"Discarded scene changes: {SceneAsset}"));
    }
}
