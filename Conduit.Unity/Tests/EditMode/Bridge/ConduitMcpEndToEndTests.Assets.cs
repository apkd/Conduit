#nullable enable

#if UNITY_EDITOR
using System;
using System.Threading.Tasks;
using Conduit;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed partial class ConduitMcpEndToEndTests
{
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
            TryFindGuidForAnyPathSuffix(success.Text, "IntegerPropertyFixture.shader"),
            Is.Not.Null,
            success.Text
        );

        var failure = await client.CallToolAsync(
            BridgeCommandTypes.GetDependencies,
            Args(
                ("projectPath", projectPath),
                ("asset", TestAssetsRoot + "/*.*")
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

        AssertTextContainsAny(success.Text, "JsonOverwriteMaterial.mat");

        var failure = await client.CallToolAsync(
            BridgeCommandTypes.FindReferencesTo,
            Args(
                ("projectPath", projectPath),
                ("asset", TestAssetsRoot + "/Nope*.asset")
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
                ("query", "/Main Camera")
            )
        );

        AssertSuccessful(success, "Main Camera", "Scene:");

        var failure = await client.CallToolAsync(
            BridgeCommandTypes.Show,
            Args(
                ("projectPath", projectPath),
                ("query", MissingQuery)
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
    public async Task Reflect_ReturnsTypeAndMemberSummaries()
    {
        var typeResult = await client.CallToolAsync(
            BridgeCommandTypes.Reflect,
            Args(
                ("projectPath", projectPath),
                ("mode", "classes"),
                ("type", "ConduitMcpEndToEndTests")
            )
        );

        AssertSuccessful(typeResult, "class ConduitMcpEndToEndTests", "Members:");

        var memberResult = await client.CallToolAsync(
            BridgeCommandTypes.Reflect,
            Args(
                ("projectPath", projectPath),
                ("mode", "methods"),
                ("member", "Search_ReturnsSceneObjectMatchAndNoMatchFailure")
            )
        );

        AssertSuccessful(memberResult, "Containing Type: ConduitMcpEndToEndTests", "Search_ReturnsSceneObjectMatchAndNoMatchFailure");
    }

    [Test]
    [Order(10)]
    public async Task ProjectSettings_ReadsAndConfirmsAnExactWrite()
    {
        const string key = "graphics_settings.log_shader_compilation";
        var read = await client.CallToolAsync(
            BridgeCommandTypes.ProjectSettings,
            Args(("projectPath", projectPath), ("key", key), ("operation", "get"))
        );

        AssertSuccessful(read, key + " = ");
        string value = read.Text[(read.Text.LastIndexOf(" = ", StringComparison.Ordinal) + 3)..].Trim();
        var write = await client.CallToolAsync(
            BridgeCommandTypes.ProjectSettings,
            Args(
                ("projectPath", projectPath),
                ("key", key),
                ("operation", "set"),
                ("value", value)
            )
        );

        AssertSuccessful(write, "Set " + key + ":", " -> " + value);
    }

    [Test]
    [Order(11)]
    public async Task ToJson_ReturnsCameraJsonAndSceneGuidance()
    {
        OpenSampleScene();

        var success = await client.CallToolAsync(
            BridgeCommandTypes.ToJson,
            Args(
                ("projectPath", projectPath),
                ("query", ConduitObjectId.FormatObjectId(Camera.main))
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

}
#endif
