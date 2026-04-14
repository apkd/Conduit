#nullable enable

using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using Conduit;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public sealed class ConduitMcpToolsTests
{
    const string TestAssetsRoot = "Packages/dev.tryfinally.conduit/Tests/EditMode/TestAssets";
    const string MaterialAsset = TestAssetsRoot + "/JsonOverwriteMaterial.mat";
    const string SceneAsset = TestAssetsRoot + "/Scenes/BridgeFixtureScene.unity";
    const string SettingsRoot = TestAssetsRoot + "/Settings";
    const string SourceAsset = SettingsRoot + "/DependencyPipeline.asset";
    const string DependencyAsset = SettingsRoot + "/DependencyRenderer.asset";
    const string MaterialIntShaderAsset = TestAssetsRoot + "/IntegerPropertyFixture.shader";
    const string TempRoot = "Assets/ConduitMcpE2ETemp";
    const string CameraSearchQuery = "Main Camera t:camera";

    [OneTimeSetUp]
    public void OneTimeSetUp() => EnsureSampleSceneOpen();

    [SetUp]
    public void SetUp() => EnsureSampleSceneOpen();

    [TearDown]
    public void TearDown() => CloseScreenshotTestWindows();

    [Test]
    public void Resolve_TracksMatchSource()
    {
        var assetPathMatches = ConduitSearchUtility.Resolve(MaterialAsset);
        Assert.That(assetPathMatches, Has.Count.EqualTo(1));
        Assert.That(assetPathMatches[0].Source, Is.EqualTo(ResolvedObjectMatchSource.AssetPath));

        var searchMatches = ConduitSearchUtility.Resolve(CameraSearchQuery);
        Assert.That(searchMatches, Has.Count.EqualTo(1));
        Assert.That(searchMatches[0].Source, Is.EqualTo(ResolvedObjectMatchSource.SearchQuery));

        var hierarchyMatches = ConduitSearchUtility.Resolve("/Main Camera");
        Assert.That(hierarchyMatches, Has.Count.EqualTo(1));
        Assert.That(hierarchyMatches[0].Source, Is.EqualTo(ResolvedObjectMatchSource.HierarchyPath));
    }

    [Test]
    public void Resolve_AcceptsWhitespaceAfterExactObjectIdPrefix()
    {
        var camera = Camera.main;
        Assert.That(camera, Is.Not.Null);

        var objectId = ConduitUtility.FormatObjectId(camera);
        var prefixLength = objectId.IndexOf(':') + 1;
        var query = $"{objectId[..prefixLength]} {objectId[prefixLength..]}";
        var matches = ConduitSearchUtility.Resolve(query);

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].Target, Is.EqualTo(camera));
        Assert.That(matches[0].Source, Is.EqualTo(ConduitSearchUtility.Resolve(objectId)[0].Source));
    }

    [Test]
    public void Resolve_AcceptsAlternateExactObjectIdPrefix()
    {
        var camera = Camera.main;
        Assert.That(camera, Is.Not.Null);

        var objectId = ConduitUtility.FormatObjectId(camera);
        var prefixLength = objectId.IndexOf(':') + 1;
        var activePrefix = objectId[..prefixLength];
        var alternatePrefix = activePrefix == "eid:" ? "id:" : "eid:";
        var query = $"{alternatePrefix}{objectId[prefixLength..]}";
        var matches = ConduitSearchUtility.Resolve(query);

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].Target, Is.EqualTo(camera));
        Assert.That(matches[0].Source, Is.EqualTo(ConduitSearchUtility.Resolve(objectId)[0].Source));
    }

    [Test]
    public void Resolve_MalformedExactObjectIdDoesNotFallThroughToSearch()
    {
        var prefix = ConduitUtility.FormatObjectId(Camera.main);
        prefix = prefix[..(prefix.IndexOf(':') + 1)];
        var matches = ConduitSearchUtility.Resolve($"{prefix} {MaterialAsset}");

        Assert.That(matches, Is.Empty);
    }

    [Test]
    public void FormatMatches_UsesUpdatedAmbiguityHintText()
    {
        var cameraMatch = ConduitSearchUtility.Resolve("/Main Camera")[0];
        var materialMatch = ConduitSearchUtility.Resolve(MaterialAsset)[0];
        var objectId = ConduitUtility.FormatObjectId(cameraMatch.Target);
        var prefixLength = objectId.IndexOf(':') + 1;
        var output = ConduitSearchUtility.FormatMatches(new[] { cameraMatch, materialMatch }, includeHint: true);

        Assert.That(output, Does.Contain("Multiple objects match your query."));
        Assert.That(output, Does.Contain($"Rerun with {objectId[..prefixLength]}<number> to select a specific match."));
    }

    [Test]
    public void GetDependencies_PatternWithSingleMatchMatchesExactOutput()
    {
        var exact = find_references_to.GetDependencies(SourceAsset);
        var pattern = find_references_to.GetDependencies($"{SettingsRoot}/DependencyPipeline*.asset");

        Assert.That(pattern, Is.EqualTo(exact));
    }

    [Test]
    public void ExpandAssetPaths_PackageWildcardMatchesSingleAsset()
    {
        var matches = ConduitAssetPathUtility.ExpandAssetPaths($"{SettingsRoot}/DependencyPipeline*.asset");

        Assert.That(matches, Is.EqualTo(new[] { SourceAsset }));
    }

    [Test]
    public void FindReferencesTo_PatternWithSingleMatchMatchesExactOutput()
    {
        var exact = find_references_to.FindReferencesTo(DependencyAsset, true);
        var pattern = find_references_to.FindReferencesTo($"{SettingsRoot}/DependencyRenderer*.asset", false);

        Assert.That(pattern, Is.EqualTo(exact));
    }

    [Test]
    public void GetDependencies_PatternWithMultipleMatchesReportsAmbiguity()
    {
        var output = find_references_to.GetDependencies($"{SettingsRoot}/*.asset");

        Assert.That(output, Does.StartWith($"Asset selector '{SettingsRoot}/*.asset' matched "));
        Assert.That(output, Does.Contain("requires a single asset"));
    }

    [Test]
    public void FindReferencesTo_PatternWithNoMatchesReportsNoResults()
    {
        var output = find_references_to.FindReferencesTo($"{SettingsRoot}/Nope*.asset", true);

        Assert.That(output, Is.EqualTo($"No assets matched '{SettingsRoot}/Nope*.asset'."));
    }

    [Test]
    public void Show_AssetPathMatchExpandsAsset()
    {
        var output = show.Show(MaterialAsset);

        Assert.That(output, Does.StartWith($"Asset: {MaterialAsset}"));
        Assert.That(output, Does.Contain("Main Object:"));
        Assert.That(output, Does.Contain("Imported Subassets:"));
    }

    [Test]
    public void Show_SearchMatchStaysOnExactObject()
    {
        var output = show.Show(CameraSearchQuery);

        Assert.That(output, Does.Contain($"Scene: {SceneAsset}"));
        Assert.That(output, Does.Contain("GameObject: Main Camera [eid:"));
        Assert.That(output, Does.Not.Contain("Main Object:"));
        Assert.That(output, Does.Not.Contain("Imported Subassets:"));
    }

    [Test]
    public void Show_CustomImplementation_UsesToStringForMcp()
    {
        var assetPath = GetTempAssetPath("UnitTests", $"CustomShow_{Guid.NewGuid():N}.asset");
        var target = ScriptableObject.CreateInstance<ConduitCustomShowAsset>();
        try
        {
            AssetDatabase.CreateAsset(target, assetPath);
            var output = show.Show(assetPath);
            Assert.That(output, Is.EqualTo("Custom MCP show output"));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void ToJson_ReturnsPrettyJsonForExactObject()
    {
        var camera = Camera.main;
        Assert.That(camera, Is.Not.Null);

        var json = ConduitObjectJsonUtility.ToJson(ConduitUtility.FormatObjectId(camera));

        Assert.That(json, Does.StartWith("{\n"));
        Assert.That(json, Does.Contain("\"Camera\": {"));
        Assert.That(json, Does.Contain("\"field of view\": 60.0"));
    }

    [Test]
    public void ToJson_SceneAssetThrowsExplicitGuidance()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ConduitObjectJsonUtility.ToJson(SceneAsset));

        Assert.That(exception, Is.Not.Null);
        Assert.That(
            exception!.Message,
            Is.EqualTo(
                $"Target scene '{SceneAsset}' cannot be safely and sensibly converted to JSON. " +
                "Use the `show` tool to display a compact representation of the scene. " +
                "(Note that the scene needs to be opened to show its contents.) " +
                "After that, you can use `to_json` and `from_json_overwrite` targeting specific scene objects."
            )
        );
    }

    [Test]
    public void FromJsonOverwrite_MaterialSupportedNoOpReportsNoChangesAndPreservesOmittedFields()
    {
        var result = ConduitObjectJsonUtility.FromJsonOverwrite(
            MaterialAsset,
            "{\"Material\":{\"m_Name\":\"JsonOverwriteMaterial\"}}");

        Assert.That(result, Is.EqualTo("No serialized properties changed."));

        var json = ConduitObjectJsonUtility.ToJson(MaterialAsset);
        Assert.That(json, Does.Contain("\"RenderType\": \"Opaque\""));
        Assert.That(json, Does.Contain("\"disabledShaderPasses\": ["));
        Assert.That(json, Does.Contain("\"MOTIONVECTORS\""));
    }

    [Test]
    public void FromJsonOverwrite_MaterialWrappedCustomRenderQueueChangePersists()
    {
        var assetPath = CreateTemporaryMaterialAssetCopy();
        try
        {
            var result = ConduitObjectJsonUtility.FromJsonOverwrite(
                assetPath,
                "{\"Material\":{\"m_CustomRenderQueue\":2500}}");

            Assert.That(result, Does.StartWith("Applied changes:"));
            Assert.That(result, Does.Contain("- Material.m_CustomRenderQueue"));
            Assert.That(GetSerializedInt(assetPath, "m_CustomRenderQueue"), Is.EqualTo(2500));
            Assert.That(ConduitObjectJsonUtility.ToJson(assetPath), Does.Contain("\"m_CustomRenderQueue\": 2500"));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void FromJsonOverwrite_MaterialUnwrappedCustomRenderQueueChangePersists()
    {
        var assetPath = CreateTemporaryMaterialAssetCopy();
        try
        {
            var result = ConduitObjectJsonUtility.FromJsonOverwrite(
                assetPath,
                "{\"m_CustomRenderQueue\":2450}");

            Assert.That(result, Does.StartWith("Applied changes:"));
            Assert.That(result, Does.Contain("- Material.m_CustomRenderQueue"));
            Assert.That(GetSerializedInt(assetPath, "m_CustomRenderQueue"), Is.EqualTo(2450));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void FromJsonOverwrite_MaterialAllowLockingChangePersists()
    {
        var assetPath = CreateTemporaryMaterialAssetCopy();
        try
        {
            var initialValue = GetSerializedBool(assetPath, "m_AllowLocking");
            var desiredValue = !initialValue;
            var result = ConduitObjectJsonUtility.FromJsonOverwrite(
                assetPath,
                $"{{\"Material\":{{\"m_AllowLocking\":{(desiredValue ? "true" : "false")}}}}}");

            Assert.That(result, Does.StartWith("Applied changes:"));
            Assert.That(result, Does.Contain("- Material.m_AllowLocking"));
            Assert.That(GetSerializedBool(assetPath, "m_AllowLocking"), Is.EqualTo(desiredValue));
            Assert.That(ConduitObjectJsonUtility.ToJson(assetPath), Does.Contain($"\"m_AllowLocking\": {(desiredValue ? "true" : "false")}"));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void FromJsonOverwrite_MaterialStringTagMapPatchUsesKeyedUpsertSemantics()
    {
        var assetPath = CreateTemporaryMaterialAssetCopy();
        try
        {
            var result = ConduitObjectJsonUtility.FromJsonOverwrite(
                assetPath,
                "{\"Material\":{\"stringTagMap\":{\"RenderType\":\"Transparent\"}}}");

            Assert.That(result, Does.StartWith("Applied changes:"));
            Assert.That(LoadMaterial(assetPath).GetTag("RenderType", false, string.Empty), Is.EqualTo("Transparent"));
            Assert.That(ConduitObjectJsonUtility.ToJson(assetPath), Does.Contain("\"RenderType\": \"Transparent\""));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void FromJsonOverwrite_MaterialDisabledShaderPassesPatchReplacesArray()
    {
        var assetPath = CreateTemporaryMaterialAssetCopy();
        try
        {
            var result = ConduitObjectJsonUtility.FromJsonOverwrite(
                assetPath,
                "{\"Material\":{\"disabledShaderPasses\":[]}}");

            Assert.That(result, Does.StartWith("Applied changes:"));
            Assert.That(LoadMaterial(assetPath).GetShaderPassEnabled("MOTIONVECTORS"), Is.True);
            Assert.That(ConduitObjectJsonUtility.ToJson(assetPath), Does.Contain("\"disabledShaderPasses\": []"));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void FromJsonOverwrite_MaterialDisabledShaderPassesRoundTripsSerializedPassNameCasing()
    {
        var assetPath = CreateTemporaryMaterialAssetCopy();
        try
        {
            Assert.That(ConduitObjectJsonUtility.ToJson(assetPath), Does.Contain("\"MOTIONVECTORS\""));

            var result = ConduitObjectJsonUtility.FromJsonOverwrite(
                assetPath,
                "{\"Material\":{\"disabledShaderPasses\":[\"MOTIONVECTORS\"]}}");

            Assert.That(result, Is.EqualTo("No serialized properties changed."));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void FromJsonOverwrite_MaterialDisabledShaderPassesAcceptsRuntimePassNameCasing()
    {
        var assetPath = CreateTemporaryMaterialAssetCopy();
        try
        {
            var result = ConduitObjectJsonUtility.FromJsonOverwrite(
                assetPath,
                "{\"Material\":{\"disabledShaderPasses\":[\"MotionVectors\"]}}");

            Assert.That(result, Is.EqualTo("No serialized properties changed."));
            Assert.That(ConduitObjectJsonUtility.ToJson(assetPath), Does.Contain("\"MOTIONVECTORS\""));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void FromJsonOverwrite_MaterialDisabledShaderPassesRejectsDuplicateCanonicalNames()
    {
        var assetPath = CreateTemporaryMaterialAssetCopy();
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => ConduitObjectJsonUtility.FromJsonOverwrite(
                assetPath,
                "{\"Material\":{\"disabledShaderPasses\":[\"MOTIONVECTORS\",\"MotionVectors\"]}}"));

            Assert.That(exception, Is.Not.Null);
            Assert.That(exception!.Message, Does.Contain("duplicate disabled shader pass"));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void FromJsonOverwrite_MaterialFloatPatchUsesKeyedUpsertSemantics()
    {
        var assetPath = CreateTemporaryMaterialAssetCopy();
        try
        {
            var material = LoadMaterial(assetPath);
            var untouchedValue = material.GetFloat("_Metallic");

            var result = ConduitObjectJsonUtility.FromJsonOverwrite(
                assetPath,
                "{\"Material\":{\"m_SavedProperties\":{\"m_Floats\":[{\"first\":\"_Glossiness\",\"second\":0.75}]}}}");

            Assert.That(result, Does.StartWith("Applied changes:"));
            material = LoadMaterial(assetPath);
            Assert.That(material.GetFloat("_Glossiness"), Is.EqualTo(0.75f).Within(0.0001f));
            Assert.That(material.GetFloat("_Metallic"), Is.EqualTo(untouchedValue).Within(0.0001f));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void FromJsonOverwrite_MaterialFloatPatchAcceptsPseudoIntSurfaceProperty()
    {
        var assetPath = CreateTemporaryMaterialAssetCopy();
        try
        {
            var result = ConduitObjectJsonUtility.FromJsonOverwrite(
                assetPath,
                "{\"Material\":{\"m_SavedProperties\":{\"m_Floats\":[{\"first\":\"_Surface\",\"second\":1.0}]}}}");

            Assert.That(result, Does.StartWith("Applied changes:"));
            Assert.That(GetSavedPropertyFloat(assetPath, "m_SavedProperties.m_Floats", "_Surface"), Is.EqualTo(1f).Within(0.0001f));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void FromJsonOverwrite_MaterialIntPatchPersistsForTrueIntProperty()
    {
        var assetPath = CreateTemporaryMaterialAsset(MaterialIntShaderAsset);
        try
        {
            var result = ConduitObjectJsonUtility.FromJsonOverwrite(
                assetPath,
                "{\"Material\":{\"m_SavedProperties\":{\"m_Ints\":[{\"first\":\"_TestInt\",\"second\":3}]}}}");

            Assert.That(result, Does.StartWith("Applied changes:"));
            Assert.That(GetSavedPropertyInt(assetPath, "m_SavedProperties.m_Ints", "_TestInt"), Is.EqualTo(3));
            Assert.That(ConduitObjectJsonUtility.ToJson(assetPath), Does.Contain("\"first\": \"_TestInt\""));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void FromJsonOverwrite_MaterialIntPatchRejectsPseudoIntSurfaceProperty()
    {
        var assetPath = CreateTemporaryMaterialAssetCopy();
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => ConduitObjectJsonUtility.FromJsonOverwrite(
                assetPath,
                "{\"Material\":{\"m_SavedProperties\":{\"m_Ints\":[{\"first\":\"_Surface\",\"second\":1}]}}}"));

            Assert.That(exception, Is.Not.Null);
            Assert.That(exception!.Message, Does.Contain("does not support integer property '_Surface'"));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void FromJsonOverwrite_MaterialIntPatchRejectsPseudoIntCullProperty()
    {
        var assetPath = CreateTemporaryMaterialAssetCopy();
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => ConduitObjectJsonUtility.FromJsonOverwrite(
                assetPath,
                "{\"Material\":{\"m_SavedProperties\":{\"m_Ints\":[{\"first\":\"_Cull\",\"second\":1}]}}}"));

            Assert.That(exception, Is.Not.Null);
            Assert.That(exception!.Message, Does.Contain("does not support integer property '_Cull'"));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void FromJsonOverwrite_MaterialColorPatchUpdatesRequestedChannelsOnly()
    {
        var assetPath = CreateTemporaryMaterialAssetCopy();
        try
        {
            var result = ConduitObjectJsonUtility.FromJsonOverwrite(
                assetPath,
                "{\"Material\":{\"m_SavedProperties\":{\"m_Colors\":[{\"first\":\"_BaseColor\",\"second\":{\"r\":0.25}}]}}}");

            Assert.That(result, Does.StartWith("Applied changes:"));
            var color = LoadMaterial(assetPath).GetColor("_BaseColor");
            Assert.That(color.r, Is.EqualTo(0.25f).Within(0.0001f));
            Assert.That(color.g, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(color.b, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(color.a, Is.EqualTo(1f).Within(0.0001f));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void FromJsonOverwrite_MaterialColorRepeatedSameValueIsNoOp()
    {
        var assetPath = CreateTemporaryMaterialAssetCopy();
        try
        {
            var firstResult = ConduitObjectJsonUtility.FromJsonOverwrite(
                assetPath,
                "{\"Material\":{\"m_SavedProperties\":{\"m_Colors\":[{\"first\":\"_BaseColor\",\"second\":{\"r\":0.25}}]}}}");
            var secondResult = ConduitObjectJsonUtility.FromJsonOverwrite(
                assetPath,
                "{\"Material\":{\"m_SavedProperties\":{\"m_Colors\":[{\"first\":\"_BaseColor\",\"second\":{\"r\":0.25}}]}}}");

            Assert.That(firstResult, Does.StartWith("Applied changes:"));
            Assert.That(firstResult, Does.Contain("- Material.m_SavedProperties.m_Colors[0].second.r"));
            Assert.That(secondResult, Is.EqualTo("No serialized properties changed."));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void FromJsonOverwrite_MaterialUnsupportedShaderFieldThrowsExplicitError()
    {
        var assetPath = CreateTemporaryMaterialAssetCopy();
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => ConduitObjectJsonUtility.FromJsonOverwrite(
                assetPath,
                "{\"Material\":{\"m_Shader\":{\"fileID\":4800000}}}"));

            Assert.That(exception, Is.Not.Null);
            Assert.That(exception!.Message, Does.Contain("Material overwrite does not support path 'm_Shader.fileID'"));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void FromJsonOverwrite_MaterialUnsupportedTexEnvFieldThrowsExplicitError()
    {
        var assetPath = CreateTemporaryMaterialAssetCopy();
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => ConduitObjectJsonUtility.FromJsonOverwrite(
                assetPath,
                "{\"Material\":{\"m_SavedProperties\":{\"m_TexEnvs\":[{\"first\":\"_BaseMap\",\"second\":{\"m_Offset\":{\"x\":0.5}}}]}}}"));

            Assert.That(exception, Is.Not.Null);
            Assert.That(exception!.Message, Does.Contain("Material overwrite does not support path 'm_SavedProperties.m_TexEnvs"));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void FromJsonOverwrite_MaterialMixedSupportedAndUnsupportedPatchIsAtomic()
    {
        var assetPath = CreateTemporaryMaterialAssetCopy();
        try
        {
            var beforeJson = ConduitObjectJsonUtility.ToJson(assetPath);

            var exception = Assert.Throws<InvalidOperationException>(() => ConduitObjectJsonUtility.FromJsonOverwrite(
                assetPath,
                "{\"Material\":{\"m_CustomRenderQueue\":2500,\"m_Shader\":{\"fileID\":4800000}}}"));

            Assert.That(exception, Is.Not.Null);
            Assert.That(exception!.Message, Does.Contain("Material overwrite does not support path 'm_Shader.fileID'"));
            Assert.That(ConduitObjectJsonUtility.ToJson(assetPath), Is.EqualTo(beforeJson));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void FromJsonOverwrite_RealChangeReturnsChangedLeafPath()
    {
        var camera = Camera.main;
        Assert.That(camera, Is.Not.Null);

        var query = ConduitUtility.FormatObjectId(camera);
        var originalJson = ConduitObjectJsonUtility.ToJson(query);
        try
        {
            var result = ConduitObjectJsonUtility.FromJsonOverwrite(
                query,
                "{\"Camera\":{\"field of view\":61.0}}");

            Assert.That(result, Does.StartWith("Applied changes:"));
            Assert.That(result, Does.Contain("- Camera.field of view"));
        }
        finally
        {
            ConduitObjectJsonUtility.FromJsonOverwrite(query, originalJson);
        }
    }

    [Test]
    public void FromJsonOverwrite_SceneGameObjectNameChangePersists()
    {
        var gameObject = Camera.main?.gameObject;
        Assert.That(gameObject, Is.Not.Null);

        var query = ConduitUtility.FormatObjectId(gameObject!);
        var originalName = gameObject!.name;
        var desiredName = $"{originalName}_Renamed";
        try
        {
            var result = ConduitObjectJsonUtility.FromJsonOverwrite(
                query,
                $"{{\"GameObject\":{{\"m_Name\":\"{desiredName}\"}}}}");

            Assert.That(result, Does.StartWith("Applied changes:"));
            Assert.That(result, Does.Contain("- GameObject.m_Name"));
            Assert.That(gameObject.name, Is.EqualTo(desiredName));
        }
        finally
        {
            gameObject.name = originalName;
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            EditorSceneManager.SaveScene(gameObject.scene);
        }
    }

    [Test]
    public void FromJsonOverwrite_ComponentNamePatchRenamesOwningGameObjectAndReportsChange()
    {
        var camera = Camera.main;
        Assert.That(camera, Is.Not.Null);

        var query = ConduitUtility.FormatObjectId(camera);
        var originalName = camera!.gameObject.name;
        var desiredName = $"{originalName}_FromComponent";
        try
        {
            var result = ConduitObjectJsonUtility.FromJsonOverwrite(
                query,
                $"{{\"Camera\":{{\"m_Name\":\"{desiredName}\"}}}}");

            Assert.That(result, Does.StartWith("Applied changes:"));
            Assert.That(result, Does.Contain("- GameObject.m_Name"));
            Assert.That(camera.gameObject.name, Is.EqualTo(desiredName));
        }
        finally
        {
            camera.gameObject.name = originalName;
            EditorSceneManager.MarkSceneDirty(camera.gameObject.scene);
            EditorSceneManager.SaveScene(camera.gameObject.scene);
        }
    }

    [Test]
    public void FromJsonOverwrite_MismatchedTypedWrapperThrowsExplicitError()
    {
        var gameObject = Camera.main?.gameObject;
        Assert.That(gameObject, Is.Not.Null);
        var originalName = gameObject!.name;

        var exception = Assert.Throws<InvalidOperationException>(() => ConduitObjectJsonUtility.FromJsonOverwrite(
            ConduitUtility.FormatObjectId(gameObject),
            "{\"Transform\":{\"m_Name\":\"WrongWrapper\"}}"));

        Assert.That(exception, Is.Not.Null);
        Assert.That(exception!.Message, Is.EqualTo("JSON wrapper 'Transform' does not match target type 'GameObject'."));
        Assert.That(gameObject.name, Is.EqualTo(originalName));
    }

    [Test]
    public void ToExceptionInfo_InvalidParseDoesNotThrow()
    {
        try
        {
            int.Parse("abc");
            Assert.Fail("Expected a FormatException.");
        }
        catch (FormatException exception)
        {
            Assert.DoesNotThrow(() => ConduitUtility.ToExceptionInfo(exception));
            var info = ConduitUtility.ToExceptionInfo(exception);
            Assert.That(info.type, Is.EqualTo("FormatException"));
            Assert.That(info.message, Does.Contain("Input string"));
        }
    }

    [Test]
    public void ExecuteCode_GetAdditionalReferences_ReusesCachedProjectLocalSnapshot()
    {
        execute_code.Initialize();
        var projectPath = execute_code.GetCurrentProjectPath();
        var snippetRootPath = execute_code.GetSnippetRootPath(projectPath);
        var first = execute_code.GetAdditionalReferences(projectPath, snippetRootPath);
        var second = execute_code.GetAdditionalReferences(projectPath, snippetRootPath);

        Assert.That(first, Is.Not.Empty);
        Assert.That(second, Is.SameAs(first));
        foreach (var reference in first)
        {
            Assert.That(Path.IsPathRooted(reference), Is.False, reference);
            Assert.That(reference, Does.EndWith(".dll"), reference);
            Assert.That(reference, Does.Not.StartWith("Library/Conduit/ExecuteCodeReferences/"), reference);
            Assert.That(reference, Does.Not.StartWith("Temp/execute_code/"), reference);
        }
    }

    [Test]
    public void ExecuteCode_SnippetArtifactIdsAreShortSequentialNumbers()
    {
        execute_code.Initialize();
        var first = execute_code.AllocateSnippetArtifactId();
        var second = execute_code.AllocateSnippetArtifactId();

        Assert.That(int.TryParse(first, out var firstId), Is.True, first);
        Assert.That(int.TryParse(second, out var secondId), Is.True, second);
        Assert.That(secondId, Is.EqualTo(firstId + 1));
        Assert.That(first, Does.Not.Contain("-"));
        Assert.That(first.Length, Is.LessThanOrEqualTo(10));
    }

    [Test]
    public void ExecuteCode_CompilerMessageFormattingAvoidsDuplicateLocationPrefix()
    {
        var compilerMessage = new CompilerMessage
        {
            type = CompilerMessageType.Warning,
            file = "Temp/execute_code/1.cs",
            line = 2,
            column = 1,
            message = "Temp/execute_code/1.cs(2,1): warning CS0618: 'Application.RegisterLogCallback(Application.LogCallback)' is obsolete: 'Application.RegisterLogCallback is deprecated. Use Application.logMessageReceived instead.'",
        };

        var formatted = execute_code.FormatCompilerMessages(new[] { compilerMessage });

        Assert.That(formatted, Is.EqualTo(
            "[Warning] Temp/execute_code/1.cs(2,1): warning CS0618: " +
            "'Application.RegisterLogCallback(Application.LogCallback)' is obsolete: " +
            "'Application.RegisterLogCallback is deprecated. Use Application.logMessageReceived instead.'"));
    }

    [Test]
    public void Screenshot_OutputPathsUseShortSequentialFileNames()
    {
        var projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var screenshotDirectoryPath = Path.Combine(projectPath, "Temp", "screenshot");
        Directory.CreateDirectory(screenshotDirectoryPath);

        foreach (var existingPath in Directory.EnumerateFiles(screenshotDirectoryPath, "Test_Path_*.jpg"))
            File.Delete(existingPath);

        var first = screenshot.AllocateOutputPath(projectPath, "Test Path");
        var firstPrefix = first.prefix;
        var firstRelativePath = first.relative_path;
        var firstAbsolutePath = first.absolute_path;

        Assert.That(firstPrefix, Is.EqualTo("Test_Path"));
        Assert.That(firstRelativePath, Is.EqualTo("Temp/screenshot/Test_Path_1.jpg"));
        File.WriteAllBytes(firstAbsolutePath, new byte[] { 1 });

        try
        {
            var second = screenshot.AllocateOutputPath(projectPath, "Test Path");
            var secondRelativePath = second.relative_path;
            Assert.That(secondRelativePath, Is.EqualTo("Temp/screenshot/Test_Path_2.jpg"));
        }
        finally
        {
            if (File.Exists(firstAbsolutePath))
                File.Delete(firstAbsolutePath);
        }
    }

    [Test]
    public async Task Screenshot_CameraCaptureCreatesImage()
    {
        var camera = Camera.main;
        Assert.That(camera, Is.Not.Null);

        if (SupportsRenderedScreenshots())
        {
            var result = await InvokeScreenshotAsync(ConduitUtility.FormatObjectId(camera));
            Assert.That(result, Does.Contain("Main_Camera image captured: Temp/screenshot/"));
            DeleteCapturedImage(result);
            return;
        }

        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () => await InvokeScreenshotAsync(ConduitUtility.FormatObjectId(camera)));
        Assert.That(exception, Is.Not.Null);
        Assert.That(exception!.Message, Does.Contain("graphics device").Or.Contain("interactive Unity editor window"));
    }

    [Test]
    public async Task Screenshot_SceneAssetCaptureCreatesImage()
    {
        var assetPath = GetTempAssetPath("UnitTests", $"ScreenshotScene_{Guid.NewGuid():N}.unity");
        CreateTemporaryScreenshotSceneAsset(assetPath);

        try
        {
            if (SupportsRenderedScreenshots())
            {
                var result = await InvokeScreenshotAsync(assetPath);
                Assert.That(result, Does.Contain("ScreenshotScene_").And.Contain(" image captured: Temp/screenshot/"));
                DeleteCapturedImage(result);
            }
            else
            {
                var exception = Assert.ThrowsAsync<InvalidOperationException>(async () => await InvokeScreenshotAsync(assetPath));
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.Message, Does.Contain("graphics device").Or.Contain("interactive Unity editor window"));
            }
        }
        finally
        {
            AssetDatabase.DeleteAsset(assetPath);
        }
    }

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

    [Test]
    public async Task Screenshot_AmbiguousQueryRequestsDisambiguation()
    {
        var result = await InvokeScreenshotAsync("ConduitDirtySave");

        Assert.That(result, Does.Contain("Multiple objects match your query."));
        Assert.That(result, Does.Contain("ConduitDirtySave"));
    }

    [Test]
    public async Task Screenshot_WindowTarget_NoMatchesReportsNoResults()
    {
        RequireInteractiveEditorWindows();

        var result = await InvokeScreenshotAsync("window:NoSuchConduitScreenshotWindow");

        Assert.That(result, Is.EqualTo("No matches for 'window:NoSuchConduitScreenshotWindow'."));
    }

    [Test]
    public async Task Screenshot_WindowTarget_AmbiguousOpenWindowQueryRequestsDisambiguation()
    {
        RequireInteractiveEditorWindows();

        OpenScreenshotTestWindow<ConduitWindowMatchAlphaWindow>();
        OpenScreenshotTestWindow<ConduitWindowMatchBetaWindow>();

        var result = await InvokeScreenshotAsync("window:window match");

        Assert.That(result, Does.Contain("Multiple editor windows match your query."));
        Assert.That(result, Does.Contain("Conduit Window Match Alpha | EditorWindow:Conduit Window Match Alpha (ConduitWindowMatchAlphaWindow)"));
        Assert.That(result, Does.Contain("Conduit Window Match Beta | EditorWindow:Conduit Window Match Beta (ConduitWindowMatchBetaWindow)"));
    }

    [Test]
    public async Task Screenshot_WindowTarget_AmbiguousWindowTypeQueryRequestsDisambiguation()
    {
        RequireInteractiveEditorWindows();

        var result = await InvokeScreenshotAsync("window:TypeMatch");

        Assert.That(result, Does.Contain("Multiple editor windows match your query."));
        Assert.That(result, Does.Contain("ConduitTypeMatchAlphaWindow | EditorWindow type"));
        Assert.That(result, Does.Contain("ConduitTypeMatchBetaWindow | EditorWindow type"));
    }

    [Test]
    public void Search_WindowTarget_UsesSharedEditorWindowSelector()
    {
        RequireInteractiveEditorWindows();

        OpenScreenshotTestWindow<ConduitWindowMatchAlphaWindow>();
        OpenScreenshotTestWindow<ConduitWindowMatchBetaWindow>();

        var result = ConduitSearchUtility.Search("window:window match");

        Assert.That(result, Does.Contain("Conduit Window Match Alpha | EditorWindow:Conduit Window Match Alpha (ConduitWindowMatchAlphaWindow)"));
        Assert.That(result, Does.Contain("Conduit Window Match Beta | EditorWindow:Conduit Window Match Beta (ConduitWindowMatchBetaWindow)"));
        Assert.That(result, Does.Not.Contain("Multiple editor windows match your query."));
    }

    [Test]
    public void Search_TestQuery_ListsKnownEditModeTests()
    {
        var result = ConduitSearchUtility.Search("t:test editmode Search_WindowTarget");

        Assert.That(result, Does.Contain("ConduitMcpToolsTests.Search_WindowTarget_UsesSharedEditorWindowSelector | EditMode"));
        Assert.That(result, Does.Not.Contain("ConduitMcpEndToEndTests.Search_ReturnsSceneObjectMatchAndNoMatchFailure"));
    }

    [Test]
    public void Search_TestQuery_ExactTestsAliasListsTests()
    {
        var result = ConduitSearchUtility.Search("tests");

        Assert.That(result, Does.Contain("ConduitMcpToolsTests.Resolve_TracksMatchSource | EditMode"));
    }

    [Test]
    public void Search_TestQuery_PlayModeFilterWithoutProjectMatchesUsesNoMatchText()
    {
        var result = ConduitSearchUtility.Search("t:test playmode");

        Assert.That(result, Is.EqualTo("No matches for 't:test playmode'."));
    }

    [Test]
    public void Show_WindowTarget_ShowsBasicEditorWindowInfo()
    {
        RequireInteractiveEditorWindows();

        var result = show.Show("window:CaptureProbe");

        Assert.That(result, Does.Contain("Editor Window: Conduit Capture Probe"));
        Assert.That(result, Does.Contain("Type: ConduitCaptureProbeWindow"));
        Assert.That(result, Does.Contain("Title: Conduit Capture Probe"));
        Assert.That(result, Does.Contain("Object: "));
        Assert.That(result, Does.Contain("Focused: "));
        Assert.That(result, Does.Contain("Docked: "));
        Assert.That(result, Does.Contain("Position: x="));
    }

    [Test]
    public async Task Screenshot_WindowTarget_OpensMatchingWindowTypeAndCapturesImage()
    {
        if (SupportsRenderedScreenshots())
        {
            try
            {
                var result = await InvokeScreenshotAsync("window:CaptureProbe");
                Assert.That(result, Does.Contain("Conduit_Capture_Probe image captured: Temp/screenshot/"));
                DeleteCapturedImage(result);
            }
            catch (InvalidOperationException captureException)
            {
                Assert.That(captureException.Message, Is.EqualTo("Editor window 'Conduit Capture Probe' could not be focused for capture."));
            }

            Assert.That(Resources.FindObjectsOfTypeAll<ConduitCaptureProbeWindow>(), Is.Not.Empty);
            return;
        }

        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () => await InvokeScreenshotAsync("window:CaptureProbe"));
        Assert.That(exception, Is.Not.Null);
        Assert.That(exception!.Message, Does.Contain("graphics device").Or.Contain("interactive Unity editor window"));
    }

    [Test]
    public void SaveScenes_SavesDirtyOpenScene()
    {
        var scene = SceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);

        var result = ConduitSceneCommandUtility.SaveScenes(null);

        Assert.That(scene.isDirty, Is.False);
        Assert.That(result, Does.Contain("Saved scenes:"));
        Assert.That(result, Does.Contain(scene.path));
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
    public void HandleClientDisconnected_PreservesActiveOperationForReconnectReplay()
    {
        var pendingOperation = new PendingOperationState
        {
            request_id = "disconnect-test",
            command_type = BridgeCommandTypes.ExecuteCode,
            client_id = 17,
        };
        var queuedOperation = new PendingOperationState
        {
            request_id = "queued-disconnect-test",
            command_type = BridgeCommandTypes.Show,
            client_id = 17,
        };

        var originalOperation = ConduitToolRunner.activeOperation;
        var originalCommand = ConduitToolRunner.activeCommand;
        var originalQueuedOperations = ConduitToolRunner.queuedOperations.ToArray();
        try
        {
            ConduitToolRunner.activeOperation = pendingOperation;
            ConduitToolRunner.activeCommand = ConduitToolRunner.ParseIncomingCommand(BridgeCommandTypes.ExecuteCode);
            ConduitToolRunner.queuedOperations.Clear();
            ConduitToolRunner.queuedOperations.Add(queuedOperation);

            ConduitToolRunner.HandleClientDisconnected(17);

            Assert.That(ConduitToolRunner.activeOperation, Is.SameAs(pendingOperation));
            Assert.That(pendingOperation.client_id, Is.EqualTo(0));
            Assert.That(queuedOperation.client_id, Is.EqualTo(0));
        }
        finally
        {
            ConduitToolRunner.activeOperation = originalOperation;
            ConduitToolRunner.activeCommand = originalCommand;
            ConduitToolRunner.queuedOperations.Clear();
            foreach (var queued in originalQueuedOperations)
                ConduitToolRunner.queuedOperations.Add(queued);
        }
    }

    [Test]
    public void HasOutstandingClientWork_TracksQueuedOperationsBeforeAcknowledgement()
    {
        var queuedOperation = new PendingOperationState
        {
            request_id = "queued-client-work",
            command_type = BridgeCommandTypes.Show,
            client_id = 23,
            is_acknowledged = false,
        };

        var originalQueuedOperations = ConduitToolRunner.queuedOperations.ToArray();
        try
        {
            ConduitToolRunner.queuedOperations.Clear();
            ConduitToolRunner.queuedOperations.Add(queuedOperation);

            Assert.That(ConduitToolRunner.HasOutstandingClientWork(23), Is.True);

            queuedOperation.is_acknowledged = true;
            Assert.That(ConduitToolRunner.HasOutstandingClientWork(23), Is.True);
        }
        finally
        {
            ConduitToolRunner.queuedOperations.Clear();
            foreach (var queued in originalQueuedOperations)
                ConduitToolRunner.queuedOperations.Add(queued);
        }
    }

    [Test]
    public void HasReconnectableWorkForAnyClient_TracksDisconnectedOperationsAndPendingResults()
    {
        var disconnectedActiveOperation = new PendingOperationState
        {
            request_id = "restored-active-work",
            command_type = BridgeCommandTypes.RefreshAssetDatabase,
            client_id = 0,
        };
        var disconnectedQueuedOperation = new PendingOperationState
        {
            request_id = "restored-queued-work",
            command_type = BridgeCommandTypes.Show,
            client_id = 0,
        };

        var originalOperation = ConduitToolRunner.activeOperation;
        var originalQueuedOperations = ConduitToolRunner.queuedOperations.ToArray();
        var pendingResultField = typeof(ConduitToolRunner).GetField("pendingResult", BindingFlags.Static | BindingFlags.NonPublic);
        var pendingResultType = typeof(ConduitToolRunner).GetNestedType("PersistedPendingResultState", BindingFlags.NonPublic);
        var originalPendingResult = pendingResultField?.GetValue(null);
        try
        {
            ConduitToolRunner.activeOperation = disconnectedActiveOperation;
            ConduitToolRunner.queuedOperations.Clear();
            Assert.That(ConduitToolRunner.HasReconnectableWorkForAnyClient(), Is.True);

            ConduitToolRunner.activeOperation = null;
            ConduitToolRunner.queuedOperations.Add(disconnectedQueuedOperation);
            Assert.That(ConduitToolRunner.HasReconnectableWorkForAnyClient(), Is.True);

            ConduitToolRunner.queuedOperations.Clear();
            Assert.That(ConduitToolRunner.HasReconnectableWorkForAnyClient(), Is.False);

            Assert.That(pendingResultField, Is.Not.Null);
            Assert.That(pendingResultType, Is.Not.Null);
            var pendingResult = Activator.CreateInstance(pendingResultType!);
            pendingResultType!.GetField("RequestID")!.SetValue(pendingResult, "restored-result");
            pendingResultType.GetField("CommandType")!.SetValue(pendingResult, BridgeCommandTypes.RefreshAssetDatabase);
            pendingResultType.GetField("Result")!.SetValue(pendingResult, new BridgeCommandResult { outcome = ToolOutcome.Success });
            pendingResultField!.SetValue(null, pendingResult);
            Assert.That(ConduitToolRunner.HasReconnectableWorkForAnyClient(), Is.True);
        }
        finally
        {
            ConduitToolRunner.activeOperation = originalOperation;
            ConduitToolRunner.queuedOperations.Clear();
            foreach (var queued in originalQueuedOperations)
                ConduitToolRunner.queuedOperations.Add(queued);

            pendingResultField?.SetValue(null, originalPendingResult);
        }
    }

    [Test]
    public void TestRunBusyGuard_BlocksCompilingUpdatingAndPlayModeTransition()
    {
        Assert.That(run_tests.ShouldBlockTestRun(true, false, false), Is.True);
        Assert.That(run_tests.ShouldBlockTestRun(false, true, false), Is.True);
        Assert.That(run_tests.ShouldBlockTestRun(false, false, true), Is.True);
        Assert.That(run_tests.ShouldBlockTestRun(false, false, false), Is.False);

        var diagnostic = run_tests.BuildBlockedTestRunDiagnostic(BridgeCommandTypes.RunTestsPlayMode, true, true, true);
        Assert.That(diagnostic, Is.EqualTo(
            "Cannot start 'run_tests_playmode' while Unity is busy: compiling scripts, updating assets, changing play mode."));
    }

    [Test]
    public void TestRunCompileErrorGuard_BlocksWhenCompilationHasFailed()
    {
        Assert.That(run_tests.ShouldFailTestRunForCompileErrors(true), Is.True);
        Assert.That(run_tests.ShouldFailTestRunForCompileErrors(false), Is.False);

        var diagnostic = run_tests.BuildCompileErrorTestRunDiagnostic(BridgeCommandTypes.RunTestsPlayMode);
        Assert.That(diagnostic, Is.EqualTo("The project has compilation errors."));
    }

    [Test]
    public void PlayBusyGuard_BlocksCompilingUpdatingAndPlayModeTransition()
    {
        Assert.That(ConduitToolRunner.ShouldWaitToEnterPlayMode(true, false, false), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitToEnterPlayMode(false, true, false), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitToEnterPlayMode(false, false, true), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitToEnterPlayMode(false, false, false), Is.False);

        var diagnostic = ConduitToolRunner.BuildEnterPlayBusyDiagnostic(true, true, true);
        Assert.That(diagnostic, Is.EqualTo(
            "Cannot enter play mode while Unity is busy: compiling scripts, updating assets, changing play mode."));
    }

    [Test]
    public void PlayCompileErrorGuard_BlocksWhenCompilationHasFailed()
    {
        Assert.That(ConduitToolRunner.ShouldFailEnterPlayForCompileErrors(true), Is.True);
        Assert.That(ConduitToolRunner.ShouldFailEnterPlayForCompileErrors(false), Is.False);
        Assert.That(ConduitToolRunner.BuildEnterPlayCompileErrorDiagnostic(), Is.EqualTo("Cannot enter play mode because the project has compilation errors."));
    }

    [Test]
    public void PlayPersistedOperation_RestoresTargetMode()
    {
        var pendingOperation = new PendingOperationState
        {
            request_id = "play-restore-test",
            command_type = BridgeCommandTypes.Play,
            target = "play",
        };

        var originalOperation = ConduitToolRunner.activeOperation;
        var originalCommand = ConduitToolRunner.activeCommand;
        try
        {
            ConduitToolRunner.ClearPersistedActiveOperation();
            ConduitToolRunner.activeOperation = null;
            ConduitToolRunner.activeCommand = default;

            ConduitToolRunner.PersistActiveOperation(pendingOperation, ConduitToolRunner.ParsedBridgeCommandKind.Play);
            ConduitToolRunner.RestorePersistedOperation();

            var restoredOperation = ConduitToolRunner.activeOperation;
            Assert.That(restoredOperation, Is.Not.Null);
            Assert.That(restoredOperation!.command_type, Is.EqualTo(BridgeCommandTypes.Play));
            Assert.That(restoredOperation.target, Is.EqualTo("play"));
            Assert.That(restoredOperation.is_restored, Is.EqualTo(true));
        }
        finally
        {
            ConduitToolRunner.ClearPersistedActiveOperation();
            ConduitToolRunner.activeOperation = originalOperation;
            ConduitToolRunner.activeCommand = originalCommand;
        }
    }

    [Test]
    public void RefreshPersistedOperation_ResumeCompletesIntoReconnectablePendingResult()
    {
        var pendingOperation = new PendingOperationState
        {
            request_id = "refresh-restore-test",
            command_type = BridgeCommandTypes.RefreshAssetDatabase,
        };

        var originalOperation = ConduitToolRunner.activeOperation;
        var originalCommand = ConduitToolRunner.activeCommand;
        var refreshReturnedField = typeof(ConduitToolRunner).GetField("reimportRefreshReturned", BindingFlags.Static | BindingFlags.NonPublic);
        var sawImportedScriptsField = typeof(ConduitToolRunner).GetField("reimportSawImportedScripts", BindingFlags.Static | BindingFlags.NonPublic);
        var observedCompilationField = typeof(ConduitToolRunner).GetField("reimportObservedCompilation", BindingFlags.Static | BindingFlags.NonPublic);
        var pendingResultField = typeof(ConduitToolRunner).GetField("pendingResult", BindingFlags.Static | BindingFlags.NonPublic);
        var pendingResultType = typeof(ConduitToolRunner).GetNestedType("PersistedPendingResultState", BindingFlags.NonPublic);
        var originalRefreshReturned = refreshReturnedField?.GetValue(null);
        var originalSawImportedScripts = sawImportedScriptsField?.GetValue(null);
        var originalObservedCompilation = observedCompilationField?.GetValue(null);
        var originalPendingResult = pendingResultField?.GetValue(null);
        var resumeMethod = typeof(ConduitToolRunner).GetMethod("ResumeRestoredOperation", BindingFlags.Static | BindingFlags.NonPublic);
        var removeReimportHooksMethod = typeof(ConduitToolRunner).GetMethod("RemoveReimportHooks", BindingFlags.Static | BindingFlags.NonPublic);

        try
        {
            Assert.That(refreshReturnedField, Is.Not.Null);
            Assert.That(sawImportedScriptsField, Is.Not.Null);
            Assert.That(observedCompilationField, Is.Not.Null);
            Assert.That(pendingResultField, Is.Not.Null);
            Assert.That(pendingResultType, Is.Not.Null);
            Assert.That(resumeMethod, Is.Not.Null);
            Assert.That(removeReimportHooksMethod, Is.Not.Null);

            ConduitToolRunner.ClearPersistedActiveOperation();
            ConduitToolRunner.activeOperation = null;
            ConduitToolRunner.activeCommand = default;
            refreshReturnedField!.SetValue(null, false);
            sawImportedScriptsField!.SetValue(null, false);
            observedCompilationField!.SetValue(null, false);
            pendingResultField!.SetValue(null, null);

            ConduitToolRunner.PersistActiveOperation(pendingOperation, ConduitToolRunner.ParsedBridgeCommandKind.RefreshAssetDatabase);
            ConduitToolRunner.RestorePersistedOperation();
            resumeMethod!.Invoke(null, null);

            Assert.That(ConduitToolRunner.activeOperation, Is.Null);
            var pendingResult = pendingResultField.GetValue(null);
            Assert.That(pendingResult, Is.Not.Null);
            Assert.That(pendingResultType!.GetField("RequestID")!.GetValue(pendingResult), Is.EqualTo("refresh-restore-test"));
            Assert.That(pendingResultType.GetField("CommandType")!.GetValue(pendingResult), Is.EqualTo(BridgeCommandTypes.RefreshAssetDatabase));
        }
        finally
        {
            removeReimportHooksMethod?.Invoke(null, null);
            ConduitToolRunner.ClearPersistedActiveOperation();
            ConduitToolRunner.activeOperation = originalOperation;
            ConduitToolRunner.activeCommand = originalCommand;
            refreshReturnedField?.SetValue(null, originalRefreshReturned);
            sawImportedScriptsField?.SetValue(null, originalSawImportedScripts);
            observedCompilationField?.SetValue(null, originalObservedCompilation);
            pendingResultField?.SetValue(null, originalPendingResult);
        }
    }

    [Test]
    public void RestoredRefreshCompileErrorDiagnostic_IsStable()
    {
        Assert.That(
            ConduitToolRunner.BuildRestoredReimportCompileErrorDiagnostic(),
            Is.EqualTo("Asset refresh completed, but the project has compilation errors."));
    }

    [Test]
    public void UserStoppedPlayModeTestRun_MapsToCancelledDiagnosticWithoutException()
    {
        var matched = run_tests.TryCreateUserStoppedPlayModeTestRunResult(
            "Exception: Playmode tests were aborted because the player was stopped.\nUnityEditor.TestTools.TestRunner.TestRun.Tasks.PlayModeRunTask",
            true,
            out var result);

        Assert.That(matched, Is.True);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.outcome, Is.EqualTo(ToolOutcome.Cancelled));
        Assert.That(result.diagnostic, Is.EqualTo("The user has manually stopped the play mode test run."));
        Assert.That(result.exception, Is.Null);

        matched = run_tests.TryCreateUserStoppedPlayModeTestRunResult(
            "Playmode tests were aborted because the player was stopped.",
            false,
            out result);

        Assert.That(matched, Is.False);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void ReimportSettlement_WaitsOnlyForScriptCompilation()
    {
        Assert.That(ConduitToolRunner.ContainsCompileAffectingAssetImports(new[] { "Assets/Test.prefab" }), Is.False);
        Assert.That(ConduitToolRunner.ContainsCompileAffectingAssetImports(new[] { "Assets/Test.cs" }), Is.True);
        Assert.That(ConduitToolRunner.ContainsCompileAffectingAssetImports(new[] { "Assets/Test.asmdef" }), Is.True);
        Assert.That(ConduitToolRunner.ContainsCompileAffectingAssetImports(new[] { "Assets/Test.dll" }), Is.True);

        Assert.That(ConduitToolRunner.ShouldWaitForReimportScriptCompilation(false, false, false, false, false), Is.False);
        Assert.That(ConduitToolRunner.ShouldWaitForReimportScriptCompilation(true, false, false, false, false), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForReimportScriptCompilation(true, true, true, false, false), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForReimportScriptCompilation(true, true, false, true, false), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForReimportScriptCompilation(true, true, false, false, false), Is.False);
        Assert.That(ConduitToolRunner.ShouldWaitForReimportScriptCompilation(true, false, false, false, true), Is.False);
        Assert.That(ConduitToolRunner.ShouldWaitForReimportScriptCompilation(true, false, false, true, true), Is.True);
    }

    [Test]
    public void ExecuteCode_LogStackTail_TrimsCompilerCallbackFramesAboveMethodInvoke()
    {
        var fullCompilerCallbackTail = string.Join("\n",
            "UnityEngine.Debug:Log",
            "System.Reflection.MethodBase:Invoke",
            "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<UnityEditor.Compilation.CompilerMessage[]>:SetResult",
            "System.Threading.Tasks.TaskCompletionSource`1<UnityEditor.Compilation.CompilerMessage[]>:TrySetResult",
            "UnityEditor.Scripting.ScriptCompilation.EditorCompilationInterface:IsCompiling");

        var truncatedCompilerCallbackTail = string.Join("\n",
            "UnityEngine.Debug:Log",
            "System.Reflection.MethodBase:Invoke",
            "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<UnityEditor.Compilation.CompilerMessage[]>:SetResult");

        Assert.That(ConduitToolRunner.TrimCommonLogTail(fullCompilerCallbackTail), Is.EqualTo("UnityEngine.Debug:Log"));
        Assert.That(ConduitToolRunner.TrimCommonLogTail(truncatedCompilerCallbackTail), Is.EqualTo("UnityEngine.Debug:Log"));
    }

    [Test]
    public void TestFilterRegex_UsesSubstringMatchByDefaultAndSupportsGlobTokens()
    {
        Assert.That(run_tests.BuildTestNameRegexPattern("Resolve"), Is.EqualTo("^.*Resolve.*$"));
        Assert.That(run_tests.BuildTestNameRegexPattern("Resolve_*"), Is.EqualTo("^Resolve_.*$"));
        Assert.That(run_tests.BuildTestNameRegexPattern("Foo?Bar"), Is.EqualTo("^Foo.Bar$"));
    }

    [Test]
    public void FilteredTestDiagnostic_IncludesStartedTests()
    {
        try
        {
            run_tests.ResetState();
            run_tests.SetActiveFilterPattern("*Resolve*");
            run_tests.RecordStartedFilteredTestLabel("ConduitMcpToolsTests.Resolve_TracksMatchSource");
            run_tests.RecordStartedFilteredTestLabel("ConduitMcpToolsTests.Resolve_AcceptsWhitespaceAfterExactObjectIdPrefix");

            var diagnostic = run_tests.BuildFilteredTestRunDiagnostic("Passed 2 tests.");
            Assert.That(diagnostic, Does.StartWith("Passed 2 tests."));
            Assert.That(diagnostic, Does.Contain("RAN TESTS:"));
            Assert.That(diagnostic, Does.Contain("ConduitMcpToolsTests.Resolve_TracksMatchSource"));
            Assert.That(diagnostic, Does.Contain("ConduitMcpToolsTests.Resolve_AcceptsWhitespaceAfterExactObjectIdPrefix"));
        }
        finally
        {
            run_tests.ResetState();
        }
    }

    static string CreateTemporaryMaterialAssetCopy()
    {
        var assetPath = GetTempAssetPath("UnitTests", $"Material_{Guid.NewGuid():N}.mat");
        Assert.That(AssetDatabase.CopyAsset(MaterialAsset, assetPath), Is.True);
        return assetPath;
    }

    static async Task<string> InvokeScreenshotAsync(string target)
        => await screenshot.CaptureAsync(target);

    static TWindow OpenScreenshotTestWindow<TWindow>()
        where TWindow : EditorWindow
    {
        var window = EditorWindow.GetWindow<TWindow>();
        window.position = new Rect(120f, 120f, 320f, 240f);
        window.Show();
        window.Focus();
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

    static void DeleteCapturedImage(string resultText)
    {
        const string marker = " image captured: ";
        var markerIndex = resultText.IndexOf(marker, StringComparison.Ordinal);
        Assert.That(markerIndex, Is.GreaterThanOrEqualTo(0), resultText);

        var relativePath = resultText[(markerIndex + marker.Length)..].Trim();
        var absolutePath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", relativePath));
        Assert.That(File.Exists(absolutePath), Is.True, absolutePath);
        File.Delete(absolutePath);
    }

    static bool SupportsRenderedScreenshots()
        => !Application.isBatchMode
           && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

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

        var assetPath = GetTempAssetPath("UnitTests", $"Material_{Guid.NewGuid():N}.mat");
        AssetDatabase.CreateAsset(new Material(shader), assetPath);
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        return assetPath;
    }

    static void DeleteTemporaryAsset(string assetPath)
    {
        if (AssetDatabase.LoadMainAssetAtPath(assetPath) != null)
            AssetDatabase.DeleteAsset(assetPath);

        CleanupTempRoot();
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

sealed class ConduitWindowMatchAlphaWindow : EditorWindow
{
    void OnEnable() => titleContent = new("Conduit Window Match Alpha");

    void OnGUI() => GUILayout.Label("Conduit Window Match Alpha");
}

sealed class ConduitWindowMatchBetaWindow : EditorWindow
{
    void OnEnable() => titleContent = new("Conduit Window Match Beta");

    void OnGUI() => GUILayout.Label("Conduit Window Match Beta");
}

sealed class ConduitTypeMatchAlphaWindow : EditorWindow
{
    void OnEnable() => titleContent = new("Conduit Type Match Alpha");

    void OnGUI() => GUILayout.Label("Conduit Type Match Alpha");
}

sealed class ConduitTypeMatchBetaWindow : EditorWindow
{
    void OnEnable() => titleContent = new("Conduit Type Match Beta");

    void OnGUI() => GUILayout.Label("Conduit Type Match Beta");
}

sealed class ConduitCaptureProbeWindow : EditorWindow
{
    void OnEnable() => titleContent = new("Conduit Capture Probe");

    void OnGUI() => GUILayout.Label("Conduit Capture Probe");
}

sealed class ConduitCustomShowAsset : ScriptableObject
{
    string ToStringForMCP() => "Custom MCP show output";
}
