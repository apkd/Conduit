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
    public void SceneObjectOverwriteIsRejectedBeforeMutationInPlayMode()
    {
        var gameObject = new GameObject("Conduit Play Mode Overwrite Guard");
        try
        {
            Assert.That(
                ConduitObjectJsonUtility.ShouldRejectSceneObjectOverwrite(true, gameObject),
                Is.True
            );
            Assert.That(
                ConduitObjectJsonUtility.ShouldRejectSceneObjectOverwrite(false, gameObject),
                Is.False
            );
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void ToJson_ReturnsPrettyJsonForExactObject()
    {
        var camera = Camera.main;
        Assert.That(camera, Is.Not.Null);

        var json = ConduitObjectJsonUtility.ToJson(ConduitObjectId.FormatObjectId(camera));

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
        var assetPath = CreateTemporaryMaterialAsset(MaterialShaderAsset);
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

        var query = ConduitObjectId.FormatObjectId(camera);
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

        var query = ConduitObjectId.FormatObjectId(gameObject!);
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

        var query = ConduitObjectId.FormatObjectId(camera);
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
            ConduitObjectId.FormatObjectId(gameObject),
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
            Assert.DoesNotThrow(() => BridgeExceptionFormatter.ToInfo(exception));
            var info = BridgeExceptionFormatter.ToInfo(exception);
            Assert.That(info.type, Is.EqualTo("FormatException"));
            Assert.That(info.message, Does.Contain("Input string"));
        }
    }
}
