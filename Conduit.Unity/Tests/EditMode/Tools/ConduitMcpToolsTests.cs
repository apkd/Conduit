#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using Conduit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public sealed partial class ConduitMcpToolsTests
{
    const string TestAssetsRoot = "Packages/dev.tryfinally.conduit/Tests/EditMode/TestAssets";
    const string MaterialAsset = TestAssetsRoot + "/JsonOverwriteMaterial.mat";
    const string MaterialShaderAsset = TestAssetsRoot + "/IntegerPropertyFixture.shader";
    const string SceneAsset = TestAssetsRoot + "/BridgeFixtureScene.unity";
    const string SourceAsset = MaterialAsset;
    const string DependencyAsset = MaterialShaderAsset;
    const string CameraSearchQuery = "Main Camera t:camera";

    [OneTimeSetUp]
    public void OneTimeSetUp() => EnsureSampleSceneOpen();

    [SetUp]
    public void SetUp() => EnsureSampleSceneOpen();

    [TearDown]
    public void TearDown() => CloseScreenshotTestWindows();

    static void CreateTemporaryScreenshotSceneAsset(string assetPath)
    {
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
    }

    static BurstTarget[] CreateBurstAsmTargets() =>
        new[]
        {
            new BurstTarget(
                "Gameplay.Motion.MoveJob - (IJob)",
                "Execute",
                "Unity.Jobs.IJobExtensions.JobStruct`1",
                "Gameplay.Motion.MoveJob"
            ),
            new BurstTarget(
                "Gameplay.Motion.MoveParticlesJob - (IJob)",
                "Execute",
                "Unity.Jobs.IJobExtensions.JobStruct`1",
                "Gameplay.Motion.MoveParticlesJob"
            ),
            new BurstTarget(
                "Gameplay.Rendering.RenderChunkJob - (IJob)",
                "Execute",
                "Unity.Jobs.IJobExtensions.JobStruct`1",
                "Gameplay.Rendering.RenderChunkJob"
            ),
        };

    static string CreateTemporaryMaterialAssetCopy()
    {
        var assetPath = ConduitTestAssets.GetTemporaryPath("UnitTests", $"Material_{Guid.NewGuid():N}.mat");
        Assert.That(AssetDatabase.CopyAsset(MaterialAsset, assetPath), Is.True);
        return assetPath;
    }

    static async Task<string> InvokeScreenshotAsync(string target)
        => await ScreenshotTool.CaptureAsync(target);

    static async Task<string> InvokeScreenshotWithoutDesktopFocusChangeAsync(string target)
    {
        var previouslyFocusedWindow = EditorWindow.focusedWindow;
        var previousActiveRenderTexture = RenderTexture.active;
        bool wasApplicationActive = UnityEditorInternal.InternalEditorUtility.isApplicationActive;
        int applicationFocusChanges = 0;
        EditorApplication.focusChanged += TrackApplicationFocus;
        try
        {
            var result = await InvokeScreenshotAsync(target);
            try
            {
                Assert.That(applicationFocusChanges, Is.Zero, "Unity application focus changed during capture.");
                Assert.That(EditorWindow.focusedWindow, Is.SameAs(previouslyFocusedWindow));
                Assert.That(
                    UnityEditorInternal.InternalEditorUtility.isApplicationActive,
                    Is.EqualTo(wasApplicationActive)
                );
                Assert.That(
                    RenderTexture.active,
                    Is.SameAs(previousActiveRenderTexture),
                    "The active render texture changed during capture."
                );
                return result;
            }
            catch
            {
                DeleteCapturedImages(result);
                throw;
            }
        }
        finally
        {
            EditorApplication.focusChanged -= TrackApplicationFocus;
        }

        void TrackApplicationFocus(bool _) => applicationFocusChanges++;
    }

    static TWindow OpenScreenshotTestWindow<TWindow>()
        where TWindow : EditorWindow
    {
        var window = (TWindow)ConduitEditorWindowDocking.CreateDockedTab(typeof(TWindow));
        window.Repaint();
        return window;
    }

    static void CloseScreenshotTestWindows()
    {
        CloseScreenshotTestWindows<ConduitWindowMatchAlphaWindow>();
        CloseScreenshotTestWindows<ConduitWindowMatchBetaWindow>();
        CloseScreenshotTestWindows<ConduitTypeMatchAlphaWindow>();
        CloseScreenshotTestWindows<ConduitTypeMatchBetaWindow>();
        CloseScreenshotTestWindows<ConduitCaptureProbeWindow>();
    }

    static void CloseScreenshotTestWindows<TWindow>()
        where TWindow : EditorWindow
    {
        foreach (var window in Resources.FindObjectsOfTypeAll<TWindow>())
            window.Close();
    }

    static void DeleteCapturedImages(string resultText)
    {
        foreach (var path in GetCapturedImagePaths(resultText))
            File.Delete(path);
    }

    static void AssertCapturedImagesHaveVisualVariation(string resultText)
    {
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        try
        {
            foreach (var path in GetCapturedImagePaths(resultText))
            {
                Assert.That(
                    ImageConversion.LoadImage(texture, File.ReadAllBytes(path)),
                    Is.True
                );
                var pixels = texture.GetPixels32();
                var first = pixels[0];
                bool hasVariation = false;
                foreach (var pixel in pixels)
                    if (!pixel.Equals(first))
                    {
                        hasVariation = true;
                        break;
                    }

                Assert.That(hasVariation, Is.True, $"The captured image '{path}' is a uniform color.");
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }

    static IReadOnlyList<string> GetCapturedImagePaths(string resultText)
    {
        const string marker = " image captured: ";
        var paths = new List<string>();
        foreach (var line in resultText.Split('\n'))
        {
            int markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
                continue;

            var relativePath = line[(markerIndex + marker.Length)..].Trim();
            var absolutePath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", relativePath)
            );
            Assert.That(File.Exists(absolutePath), Is.True, absolutePath);
            paths.Add(absolutePath);
        }

        Assert.That(paths, Is.Not.Empty, resultText);
        return paths;
    }

    static void RequireInteractiveEditorWindows()
    {
        if (!Application.isBatchMode && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
            return;

        Assert.Ignore("Editor window selector tests require an interactive Unity editor window with a graphics device.");
    }

    static void EnsureSampleSceneOpen()
    {
        var activeScene = SceneManager.GetActiveScene();
        if (activeScene.path == SceneAsset && !activeScene.isDirty && SceneManager.sceneCount == 1)
            return;

        EditorSceneManager.OpenScene(SceneAsset, OpenSceneMode.Single);
    }

    static string CreateTemporaryMaterialAsset(string shaderAssetPath)
    {
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderAssetPath);
        Assert.That(shader, Is.Not.Null, $"Could not load shader '{shaderAssetPath}'.");

        var assetPath = ConduitTestAssets.GetTemporaryPath("UnitTests", $"Material_{Guid.NewGuid():N}.mat");
        AssetDatabase.CreateAsset(new Material(shader), assetPath);
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        return assetPath;
    }

    static void DeleteTemporaryAsset(string assetPath)
    {
        if (AssetDatabase.LoadMainAssetAtPath(assetPath) != null)
            AssetDatabase.DeleteAsset(assetPath);

        ConduitTestAssets.CleanupTemporaryRoot();
    }

    static Material LoadMaterial(string assetPath)
        => AssetDatabase.LoadAssetAtPath<Material>(assetPath)
           ?? throw new AssertionException($"Could not load material '{assetPath}'.");

    static int GetSerializedInt(string assetPath, string propertyPath)
    {
        var property = new SerializedObject(LoadMaterial(assetPath)).FindProperty(propertyPath);
        Assert.That(property, Is.Not.Null);
        return property!.intValue;
    }

    static bool GetSerializedBool(string assetPath, string propertyPath)
    {
        var property = new SerializedObject(LoadMaterial(assetPath)).FindProperty(propertyPath);
        Assert.That(property, Is.Not.Null);
        return property!.boolValue;
    }

    static int GetSavedPropertyInt(string assetPath, string collectionPath, string propertyName)
    {
        var entry = FindSavedPropertyEntry(assetPath, collectionPath, propertyName);
        Assert.That(entry, Is.Not.Null);
        return entry!.FindPropertyRelative("second")!.intValue;
    }

    static float GetSavedPropertyFloat(string assetPath, string collectionPath, string propertyName)
    {
        var entry = FindSavedPropertyEntry(assetPath, collectionPath, propertyName);
        Assert.That(entry, Is.Not.Null);
        return entry!.FindPropertyRelative("second")!.floatValue;
    }

    static SerializedProperty? FindSavedPropertyEntry(string assetPath, string collectionPath, string propertyName)
    {
        var collection = new SerializedObject(LoadMaterial(assetPath)).FindProperty(collectionPath);
        Assert.That(collection, Is.Not.Null);
        for (var index = 0; index < collection!.arraySize; index++)
        {
            var entry = collection.GetArrayElementAtIndex(index);
            if (entry.FindPropertyRelative("first") is not { stringValue: var currentName })
                continue;

            if (currentName == propertyName)
                return entry.Copy();
        }

        return null;
    }
}
