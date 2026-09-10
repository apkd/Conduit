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
    [TestCase(false)]
    [TestCase(true)]
    public void SceneDiskChanges_ReloadCleanScenesAndPreserveDirtyScenes(bool dirty)
    {
        var assetPath = ConduitTestAssets.GetTemporaryPath("UnitTests", $"DiskChange_{Guid.NewGuid():N}.unity");
        CreateTemporaryScreenshotSceneAsset(assetPath);
        ConduitOpenSceneDiskChangeGuard.Initialize();
        var scene = EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
        AssetDatabase.DisallowAutoRefresh();
        try
        {
            var originalName = scene.GetRootGameObjects().Single().name;
            var diskName = originalName + "ChangedOnDisk";
            if (dirty)
            {
                scene.GetRootGameObjects().Single().name = "UnsavedSceneChange";
                EditorSceneManager.MarkSceneDirty(scene);
            }

            File.WriteAllText(assetPath, File.ReadAllText(assetPath).Replace($"m_Name: {originalName}", $"m_Name: {diskName}"));
            var now = EditorApplication.timeSinceStartup + 10;
            ConduitOpenSceneDiskChangeGuard.CheckForSceneChanges(now);
            Assert.That(scene.GetRootGameObjects().Single().name, Is.EqualTo(dirty ? "UnsavedSceneChange" : originalName));

            ConduitOpenSceneDiskChangeGuard.CheckForSceneChanges(now + 10);
            scene = SceneManager.GetSceneByPath(assetPath);
            Assert.That(scene.GetRootGameObjects().Single().name, Is.EqualTo(dirty ? "UnsavedSceneChange" : diskName));
            Assert.That(scene.isDirty, Is.EqualTo(dirty));

            ConduitOpenSceneDiskChangeGuard.CheckForSceneChanges(now + 20);
            scene = SceneManager.GetSceneByPath(assetPath);
            Assert.That(scene.GetRootGameObjects().Single().name, Is.EqualTo(dirty ? "UnsavedSceneChange" : diskName));
            Assert.That(scene.isDirty, Is.EqualTo(dirty));
        }
        finally
        {
            try
            {
                scene = SceneManager.GetSceneByPath(assetPath);
                if (scene.isLoaded)
                    EditorSceneManager.CloseScene(scene, true);

                DeleteTemporaryAsset(assetPath);
            }
            finally
            {
                AssetDatabase.AllowAutoRefresh();
            }
        }
    }

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
