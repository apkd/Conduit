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
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public sealed class ConduitMcpEndToEndTests
{
    const string TestAssetsRoot = "Packages/dev.tryfinally.conduit/Tests/EditMode/TestAssets";
    const string MaterialAsset = TestAssetsRoot + "/JsonOverwriteMaterial.mat";
    const string MissingScriptPrefabAsset = TestAssetsRoot + "/MissingScriptFixture.prefab";
    const string SceneAsset = TestAssetsRoot + "/Scenes/BridgeFixtureScene.unity";
    const string SettingsRoot = TestAssetsRoot + "/Settings";
    const string SourceAsset = SettingsRoot + "/DependencyPipeline.asset";
    const string DependencyAsset = SettingsRoot + "/DependencyRenderer.asset";
    const string VolumeProfileAsset = SettingsRoot + "/SceneEffectsProfile.asset";
    const string TempRoot = "Assets/ConduitMcpE2ETemp";
    const string MissingScenePath = "Assets/ConduitMcpDefinitelyMissingScene.unity";
    const string MissingQuery = "ConduitMcpDefinitelyMissingObject";
    static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);

    readonly List<string> temporaryAssetPaths = new();
    readonly List<string> temporaryDirectories = new();
    McpStdioTestClient client = null!;
    bool canonicalAssetsValidated;
    bool searchProvidersWarmed;
    string editorProjectPath = string.Empty;
    string projectPath = string.Empty;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        if (ConduitConnection.GetConnectionStatus() == ConduitConnectionStatus.Connected)
            Assert.Ignore("This end-to-end suite must run without another active Conduit bridge client attached to the editor.");

        editorProjectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        projectPath = ConduitProjectIdentity.NormalizeProjectPath(editorProjectPath);

        ValidateCanonicalAssets();
        canonicalAssetsValidated = true;

        ConduitToolRunner.Initialize();
        ConduitConnection.EnsureStarted();

        try
        {
            client = McpStdioTestClient.StartAsync(StartupTimeout)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            client?.Dispose();
            throw;
        }
    }

    [SetUp]
    public void SetUp()
    {
        temporaryAssetPaths.Clear();
        temporaryDirectories.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        if (canonicalAssetsValidated)
            OpenSampleScene();

        CleanupTemporaryAssets();
        CleanupTemporaryDirectories();
        CleanupTempRoot();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => client?.Dispose();

    [Test]
    [Order(1)]
    public void Initialize_Succeeds()
    {
        Assert.That(client.NegotiatedProtocolVersion, Is.Not.Empty);
        Assert.That(client.ServerName, Is.Not.Empty);
        Assert.That(client.ServerName.IndexOf("conduit", StringComparison.OrdinalIgnoreCase), Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    [Order(2)]
    public async Task ToolsList_ContainsBridgeCommandSurface()
    {
        var tools = await client.ListToolsAsync();

        foreach (var tool in new[]
                 {
                     BridgeCommandTypes.Status,
                     BridgeCommandTypes.Play,
                     BridgeCommandTypes.Screenshot,
                     BridgeCommandTypes.GetDependencies,
                     BridgeCommandTypes.FindReferencesTo,
                     BridgeCommandTypes.FindMissingScripts,
                     BridgeCommandTypes.Show,
                     BridgeCommandTypes.Search,
                     BridgeCommandTypes.ToJson,
                     BridgeCommandTypes.FromJsonOverwrite,
                     BridgeCommandTypes.SaveScenes,
                     BridgeCommandTypes.DiscardScenes,
                     BridgeCommandTypes.RefreshAssetDatabase,
                     BridgeCommandTypes.ExecuteCode,
                 })
            Assert.That(tools, Has.Member(tool));
    }

    [Test]
    [Order(3)]
    public async Task Status_ReportsReachableAndInvalidProjectFailure()
    {
        var reachable = await client.CallToolAsync(
            BridgeCommandTypes.Status,
            Args(("projectPath", projectPath))
        );

        Assert.That(reachable.IsError, Is.False, reachable.Text);
        AssertTextContainsAny(reachable.Text, "Bridge: reachable", "Status:");
        AssertTextContainsAny(reachable.Text, projectPath, "Unity ");

        var invalidProjectPath = CreateInvalidProjectPath();
        var invalidProject = await client.CallToolAsync(
            BridgeCommandTypes.Status,
            Args(("projectPath", invalidProjectPath))
        );

        AssertTextContainsAny(invalidProject.Text, "not a valid Unity project", "Project:");
    }

    [Test]
    [Order(4)]
    public async Task GetDependencies_SupportsSuccessAndAmbiguousSelector()
    {
        var success = await client.CallToolAsync(
            BridgeCommandTypes.GetDependencies,
            Args(
                ("projectPath", projectPath),
                ("asset", SourceAsset)
            )
        );

        Assert.That(
            TryFindGuidForAnyPathSuffix(success.Text, "DependencyRenderer.asset", "PC_Renderer.asset"),
            Is.Not.Null,
            success.Text
        );

        var failure = await client.CallToolAsync(
            BridgeCommandTypes.GetDependencies,
            Args(
                ("projectPath", projectPath),
                ("asset", SettingsRoot + "/*.asset")
            )
        );

        AssertTextContainsAny(failure.Text, "Asset selector", "requires a single asset");
    }

    [Test]
    [Order(5)]
    public async Task FindReferencesTo_SupportsSuccessAndNoMatch()
    {
        var success = await client.CallToolAsync(
            BridgeCommandTypes.FindReferencesTo,
            Args(
                ("projectPath", projectPath),
                ("asset", DependencyAsset),
                ("rebuildCache", false)
            )
        );

        AssertTextContainsAny(success.Text, "DependencyPipeline.asset", "PC_RPAsset.asset");

        var failure = await client.CallToolAsync(
            BridgeCommandTypes.FindReferencesTo,
            Args(
                ("projectPath", projectPath),
                ("asset", SettingsRoot + "/Nope*.asset")
            )
        );

        AssertTextContainsAny(failure.Text, "No assets matched");
    }

    [Test]
    [Order(6)]
    public async Task FindMissingScripts_ReportsBrokenPrefabAndCleanScene()
    {
        var missingScripts = await client.CallToolAsync(
            BridgeCommandTypes.FindMissingScripts,
            Args(
                ("projectPath", projectPath),
                ("assetPattern", MissingScriptPrefabAsset)
            )
        );

        AssertSuccessful(missingScripts, MissingScriptPrefabAsset, "Missing script hits:", "missing_scripts=");

        var cleanScene = await client.CallToolAsync(
            BridgeCommandTypes.FindMissingScripts,
            Args(
                ("projectPath", projectPath),
                ("assetPattern", SceneAsset)
            )
        );

        AssertSuccessful(cleanScene, "No missing scripts found");
    }

    [Test]
    [Order(7)]
    public async Task Show_ReturnsSceneObjectSummaryAndNoMatchFailure()
    {
        OpenSampleScene();

        var success = await client.CallToolAsync(
            BridgeCommandTypes.Show,
            Args(
                ("projectPath", projectPath),
                ("asset", "/Main Camera")
            )
        );

        AssertSuccessful(success, "Main Camera", "Scene:");

        var failure = await client.CallToolAsync(
            BridgeCommandTypes.Show,
            Args(
                ("projectPath", projectPath),
                ("asset", MissingQuery)
            )
        );

        AssertTextContainsAny(failure.Text, "No matches for");
    }

    [Test]
    [Order(8)]
    public async Task Search_ReturnsSceneObjectMatchAndNoMatchFailure()
    {
        OpenSampleScene();

        var success = await client.CallToolAsync(
            BridgeCommandTypes.Search,
            Args(
                ("projectPath", projectPath),
                ("query", "Main Camera t:camera")
            )
        );

        AssertSuccessful(success, "Main Camera");

        var failure = await client.CallToolAsync(
            BridgeCommandTypes.Search,
            Args(
                ("projectPath", projectPath),
                ("query", MissingQuery)
            )
        );

        AssertTextContainsAny(failure.Text, "No matches for");
    }

    [Test]
    [Order(9)]
    public async Task Search_TestQuery_ListsDiscoveredTests()
    {
        var result = await client.CallToolAsync(
            BridgeCommandTypes.Search,
            Args(
                ("projectPath", projectPath),
                ("query", "t:test editmode Search_ReturnsSceneObjectMatchAndNoMatchFailure")
            )
        );

        AssertSuccessful(result, "ConduitMcpEndToEndTests.Search_ReturnsSceneObjectMatchAndNoMatchFailure", "EditMode");
    }

    [Test]
    [Order(10)]
    public async Task ToJson_ReturnsCameraJsonAndSceneGuidance()
    {
        OpenSampleScene();

        var success = await client.CallToolAsync(
            BridgeCommandTypes.ToJson,
            Args(
                ("projectPath", projectPath),
                ("query", ConduitUtility.FormatObjectId(Camera.main))
            )
        );

        AssertSuccessful(success, "\"Camera\": {", "\"field of view\": 60.0");

        var failure = await client.CallToolAsync(
            BridgeCommandTypes.ToJson,
            Args(
                ("projectPath", projectPath),
                ("query", SceneAsset)
            )
        );

        AssertTextContainsAny(failure.Text, "cannot be safely and sensibly converted to JSON", "Use the `show` tool");
    }

    [Test]
    [Order(11)]
    public async Task FromJsonOverwrite_UpdatesMaterialAndRejectsUnsupportedPatchAtomically()
    {
        var assetPath = CreateTemporaryMaterialAssetCopy();
        try
        {
            var success = await client.CallToolAsync(
                BridgeCommandTypes.FromJsonOverwrite,
                Args(
                    ("projectPath", projectPath),
                    ("query", assetPath),
                    ("json", "{\"Material\":{\"m_CustomRenderQueue\":2450}}")
                )
            );

            AssertSuccessful(success, "m_CustomRenderQueue");
            Assert.That(GetSerializedInt(assetPath, "m_CustomRenderQueue"), Is.EqualTo(2450));

            var beforeValue = GetSerializedInt(assetPath, "m_CustomRenderQueue");
            var failure = await client.CallToolAsync(
                BridgeCommandTypes.FromJsonOverwrite,
                Args(
                    ("projectPath", projectPath),
                    ("query", assetPath),
                    ("json", "{\"Material\":{\"m_CustomRenderQueue\":2600,\"m_Shader\":{\"fileID\":4800000}}}")
                )
            );

            AssertTextContainsAny(failure.Text, "does not support path", "Material overwrite");
            Assert.That(GetSerializedInt(assetPath, "m_CustomRenderQueue"), Is.EqualTo(beforeValue));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    [Order(12)]
    public async Task SaveScenes_SavesDirtyTempSceneAndRejectsMissingOpenScene()
    {
        OpenSampleScene();

        var tempScenePath = CreateTemporarySceneAssetCopy();
        var scene = EditorSceneManager.OpenScene(tempScenePath, OpenSceneMode.Additive);
        EditorSceneManager.MarkSceneDirty(scene);

        var success = await client.CallToolAsync(
            BridgeCommandTypes.SaveScenes,
            Args(
                ("projectPath", projectPath),
                ("scenePath", tempScenePath)
            )
        );

        AssertSuccessful(success, "Saved scene", tempScenePath);
        Assert.That(scene.isDirty, Is.False);

        var failure = await client.CallToolAsync(
            BridgeCommandTypes.SaveScenes,
            Args(
                ("projectPath", projectPath),
                ("scenePath", MissingScenePath)
            )
        );

        AssertTextContainsAny(failure.Text, "Open scene", "was not found");
    }

    [Test]
    [Order(13)]
    public async Task DiscardScenes_DiscardsDirtyTempSceneAndRejectsMissingOpenScene()
    {
        OpenSampleScene();

        var tempScenePath = CreateTemporarySceneAssetCopy();
        var scene = EditorSceneManager.OpenScene(tempScenePath, OpenSceneMode.Additive);
        var temporaryObjectName = "ConduitMcpDiscardTemp";
        var temporaryObject = new GameObject(temporaryObjectName);
        SceneManager.MoveGameObjectToScene(temporaryObject, scene);
        EditorSceneManager.MarkSceneDirty(scene);

        var success = await client.CallToolAsync(
            BridgeCommandTypes.DiscardScenes,
            Args(
                ("projectPath", projectPath),
                ("scenePath", tempScenePath)
            )
        );

        AssertSuccessful(success, "Discarded scene changes");
        Assert.That(SceneManager.GetActiveScene().isDirty, Is.False);
        Assert.That(GameObject.Find(temporaryObjectName), Is.Null);

        var failure = await client.CallToolAsync(
            BridgeCommandTypes.DiscardScenes,
            Args(
                ("projectPath", projectPath),
                ("scenePath", MissingScenePath)
            )
        );

        AssertTextContainsAny(failure.Text, "Open scene", "was not found");
    }

    [Test]
    [Order(14)]
    public async Task ExecuteCode_CoversSuccessCacheRuntimeFailureAndCompileFailure()
    {
        var runtimeTogglePath = Path.Combine(Path.GetTempPath(), $"ConduitExecuteCode_{Guid.NewGuid():N}.flag");
        var snippet
            = "return File.Exists(@\""
              + runtimeTogglePath.Replace("\"", "\"\"")
              + "\")"
              + " ? System.Int32.Parse(\"abc\")"
              + " : System.Math.Abs(-5);";
        try
        {
            var success = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(
                    ("projectPath", projectPath),
                    ("snippet", snippet)
                )
            );

            var cachedSuccess = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(
                    ("projectPath", projectPath),
                    ("snippet", snippet)
                )
            );

            File.WriteAllText(runtimeTogglePath, string.Empty);
            var runtimeFailure = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(
                    ("projectPath", projectPath),
                    ("snippet", snippet)
                )
            );

            var compileFailure = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(
                    ("projectPath", projectPath),
                    ("snippet", "namespace Rejected { }")
                )
            );

            AssertSuccessful(success, "5");
            AssertSuccessful(cachedSuccess, "5");
            AssertTextContainsAny(runtimeFailure.Text, "FormatException", "Input string");
            AssertTextContainsAny(compileFailure.Text, "Namespace declarations are not supported", "execute_code(");
        }
        finally
        {
            if (File.Exists(runtimeTogglePath))
                File.Delete(runtimeTogglePath);
        }
    }

    [Test]
    [Order(15)]
    public async Task Play_TogglesBetweenEditModeAndPlayMode()
    {
        var originalOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
        var originalOptions = EditorSettings.enterPlayModeOptions;

        try
        {
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;

            var enteredPlay = await client.CallToolAsync(
                BridgeCommandTypes.Play,
                Args(("projectPath", projectPath))
            );

            AssertSuccessful(enteredPlay, "Entered play mode", "Paused:");

            var enteredEdit = await client.CallToolAsync(
                BridgeCommandTypes.Play,
                Args(("projectPath", projectPath))
            );

            AssertSuccessful(enteredEdit, "Entered edit mode.");
        }
        finally
        {
            if (EditorApplication.isPlaying)
                EditorApplication.isPlaying = false;

            EditorSettings.enterPlayModeOptions = originalOptions;
            EditorSettings.enterPlayModeOptionsEnabled = originalOptionsEnabled;
        }
    }

    [Test]
    [Order(16)]
    public async Task Screenshot_CapturesCameraSceneAssetAndAmbiguousSelector()
    {
        OpenSampleScene();

        if (SupportsRenderedScreenshots())
        {
            var cameraCapture = await client.CallToolAsync(
                BridgeCommandTypes.Screenshot,
                Args(
                    ("projectPath", projectPath),
                    ("target", ConduitUtility.FormatObjectId(Camera.main))
                )
            );

            AssertSuccessful(cameraCapture, "Main_Camera image captured:", "Temp/screenshot/");
            AssertCapturedImageExists(cameraCapture.Text);

            var sceneAssetPath = CreateTemporaryScreenshotSceneAsset();
            var sceneCapture = await client.CallToolAsync(
                BridgeCommandTypes.Screenshot,
                Args(
                    ("projectPath", projectPath),
                    ("target", sceneAssetPath)
                )
            );

            AssertSuccessful(sceneCapture, "ScreenshotScene image captured:", "Temp/screenshot/");
            AssertCapturedImageExists(sceneCapture.Text);
        }

        OpenSampleScene();

        var ambiguous = await client.CallToolAsync(
            BridgeCommandTypes.Screenshot,
            Args(
                ("projectPath", projectPath),
                ("target", "ConduitDirtySave")
            )
        );

        AssertTextContainsAny(ambiguous.Text, "Multiple objects match your query.", "ConduitDirtySave");
    }

    [Test]
    [Order(100)]
    public async Task RefreshAssetDatabase_ImportsNewTextAsset()
    {
        var assetPath = RegisterTemporaryAsset(GetTempAssetPath("Refresh", $"RefreshAsset_{Guid.NewGuid():N}.txt"));
        File.WriteAllText(ToAbsoluteProjectPath(assetPath), "hello from refresh");

        Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath), Is.Null);

        var result = await client.CallToolAsync(
            BridgeCommandTypes.RefreshAssetDatabase,
            Args(("projectPath", projectPath))
        );

        Assert.That(result.IsError, Is.False, result.Text);
        AssertTextContainsAny(result.Text, "Success", "completed after", "recovered after");

        var importedAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);
        Assert.That(importedAsset, Is.Not.Null, $"Asset '{assetPath}' was not imported after refresh.");
        Assert.That(importedAsset!.text, Is.EqualTo("hello from refresh"));
    }

    [Test]
    [Order(101)]
    public async Task RefreshAssetDatabase_NoChangesReturnsPromptly()
    {
        var startedAt = DateTime.UtcNow;
        var result = await client.CallToolAsync(
            BridgeCommandTypes.RefreshAssetDatabase,
            Args(("projectPath", projectPath)),
            timeout: TimeSpan.FromSeconds(10)
        );

        var elapsed = DateTime.UtcNow - startedAt;
        Assert.That(result.IsError, Is.False, result.Text);
        AssertTextContainsAny(result.Text, "Success", "completed after", "recovered after");
        Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(10)), $"No-op refresh took {elapsed.TotalSeconds:0.000}s.");
    }

    [Test]
    [Order(102)]
    public async Task RefreshAssetDatabase_PlayModeNoChangesReturnsPromptly()
    {
        var originalOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
        var originalOptions = EditorSettings.enterPlayModeOptions;

        try
        {
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;

            var enteredPlay = await client.CallToolAsync(
                BridgeCommandTypes.Play,
                Args(("projectPath", projectPath))
            );

            AssertSuccessful(enteredPlay, "Entered play mode", "Paused:");

            var startedAt = DateTime.UtcNow;
            var result = await client.CallToolAsync(
                BridgeCommandTypes.RefreshAssetDatabase,
                Args(("projectPath", projectPath)),
                timeout: TimeSpan.FromSeconds(10)
            );

            var elapsed = DateTime.UtcNow - startedAt;
            Assert.That(result.IsError, Is.False, result.Text);
            AssertTextContainsAny(result.Text, "Success", "completed after", "recovered after");
            Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(10)), $"Play-mode no-op refresh took {elapsed.TotalSeconds:0.000}s.");
        }
        finally
        {
            if (EditorApplication.isPlaying)
                EditorApplication.isPlaying = false;

            EditorSettings.enterPlayModeOptions = originalOptions;
            EditorSettings.enterPlayModeOptionsEnabled = originalOptionsEnabled;
        }
    }

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

    static void AssertTextContainsAny(string text, params string[] expectedSubstrings)
    {
        foreach (var expectedSubstring in expectedSubstrings)
        {
            if (text.IndexOf(expectedSubstring, StringComparison.OrdinalIgnoreCase) >= 0)
                return;
        }

        Assert.Fail(text);
    }

    static bool SupportsRenderedScreenshots()
        => !Application.isBatchMode
           && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

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
        using var pooled = ConduitUtility.GetPooledList<string>(out var missingAssetPaths);
        var assetPaths = new[]
        {
            MaterialAsset,
            MissingScriptPrefabAsset,
            SceneAsset,
            SourceAsset,
            DependencyAsset,
            VolumeProfileAsset,
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
        var assetPath = RegisterTemporaryAsset(GetTempAssetPath("Materials", $"Material_{Guid.NewGuid():N}.mat"));
        Assert.That(AssetDatabase.CopyAsset(MaterialAsset, assetPath), Is.True);
        return assetPath;
    }

    string CreateTemporarySceneAssetCopy()
    {
        var assetPath = RegisterTemporaryAsset(GetTempAssetPath("Scenes", $"Scene_{Guid.NewGuid():N}.unity"));
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
        var assetPath = RegisterTemporaryAsset(GetTempAssetPath("Scenes", "ScreenshotScene.unity"));
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

    static string GetTempAssetPath(string category, string fileName)
    {
        var assetPath = $"{TempRoot}/{category}/{fileName}";
        EnsureParentFolders(assetPath);
        return assetPath;
    }

    static void CleanupTempRoot()
    {
        if (AssetDatabase.IsValidFolder(TempRoot))
            AssetDatabase.DeleteAsset(TempRoot);
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
