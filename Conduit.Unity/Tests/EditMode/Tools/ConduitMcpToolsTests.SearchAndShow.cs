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
    public void ObjectId_RoundTripsAcrossUnityVersions()
    {
        var camera = Camera.main;
        Assert.That(camera, Is.Not.Null);

        Assert.That(
            ConduitObjectId.ResolveObjectId(ConduitObjectId.GetObjectId(camera)),
            Is.SameAs(camera)
        );
    }

    [Test]
    public void Resolve_AcceptsWhitespaceAfterExactObjectIdPrefix()
    {
        var camera = Camera.main;
        Assert.That(camera, Is.Not.Null);

        var objectId = ConduitObjectId.FormatObjectId(camera);
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

        var objectId = ConduitObjectId.FormatObjectId(camera);
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
        var prefix = ConduitObjectId.FormatObjectId(Camera.main);
        prefix = prefix[..(prefix.IndexOf(':') + 1)];
        var matches = ConduitSearchUtility.Resolve($"{prefix} {MaterialAsset}");

        Assert.That(matches, Is.Empty);
    }

    [Test]
    public void FormatMatches_UsesUpdatedAmbiguityHintText()
    {
        var cameraMatch = ConduitSearchUtility.Resolve("/Main Camera")[0];
        var materialMatch = ConduitSearchUtility.Resolve(MaterialAsset)[0];
        var objectId = ConduitObjectId.FormatObjectId(cameraMatch.RequireTarget());
        var prefixLength = objectId.IndexOf(':') + 1;
        var output = ConduitSearchUtility.FormatMatches(new[] { cameraMatch, materialMatch }, includeHint: true);

        Assert.That(output, Does.Contain("Multiple objects match your query."));
        Assert.That(output, Does.Contain($"Rerun with {objectId[..prefixLength]}<number> to select a specific match."));
    }

    [Test]
    public void SearchReportsWhenResultsAreTruncated()
    {
        var name = "Conduit Search Truncation " + Guid.NewGuid().ToString("N");
        var objects = new List<GameObject>();
        try
        {
            for (var index = 0; index < 30; index++)
                objects.Add(new GameObject(name));

            var output = ConduitSearchUtility.Search("/" + name);

            Assert.That(output, Does.Contain("Showing the first 25 results; additional matches were omitted."));
        }
        finally
        {
            foreach (var gameObject in objects)
                UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void PrefabShowOmitsTemporaryObjectIds()
    {
        var output = ShowTool.Show(TestAssetsRoot + "/MissingScriptFixture.prefab");

        Assert.That(output, Does.Contain("Object IDs are omitted because prefab contents are temporary."));
        Assert.That(output, Does.Not.Contain("eid:"));
        Assert.That(output, Does.Not.Contain("id:"));
    }

    [Test]
    public void ConduitSearch_SearchReturnsSingleObjectAndThrowsForNoMatches()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialAsset);
        Assert.That(material, Is.Not.Null);

        var resolved = ConduitSearch.Search(MaterialAsset);
        var exception = Assert.Throws<InvalidOperationException>(() => ConduitSearch.Search("ConduitMissingSearchTarget"));

        Assert.That(resolved, Is.EqualTo(material));
        Assert.That(exception!.Message, Does.Contain("No matches for 'ConduitMissingSearchTarget'."));
    }

    [Test]
    public void ConduitSearch_SearchThrowsAmbiguityWithCandidates()
    {
        var objectName = "ConduitSearchAmbiguous_" + Guid.NewGuid().ToString("N");
        var first = new GameObject(objectName);
        var second = new GameObject(objectName);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => ConduitSearch.Search("/" + objectName));

            Assert.That(exception!.Message, Does.Contain("Multiple objects match your query."));
            Assert.That(exception.Message, Does.Contain(objectName));
            Assert.That(exception.Message, Does.Contain(ConduitObjectId.FormatObjectId(first)));
            Assert.That(exception.Message, Does.Contain(ConduitObjectId.FormatObjectId(second)));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(first);
            UnityEngine.Object.DestroyImmediate(second);
        }
    }

    [Test]
    public void ConduitSearch_SearchManyReturnsEmptyArrayForNoMatches()
    {
        var matches = ConduitSearch.SearchMany<Material>("ConduitMissingMaterialSearchTarget");

        Assert.That(matches, Is.Empty);
        Assert.That(matches, Is.SameAs(Array.Empty<Material>()));
    }

    [Test]
    public void ConduitSearch_SearchManyUsesAllResultsQuery()
    {
        var objectName = "ConduitSearchMany_" + Guid.NewGuid().ToString("N");
        var objects = new List<GameObject>();
        try
        {
            for (var index = 0; index < 30; index++)
                objects.Add(new GameObject(objectName));

            var matches = ConduitSearch.SearchMany("/" + objectName);

            Assert.That(matches.Length, Is.GreaterThanOrEqualTo(objects.Count));
            foreach (var gameObject in objects)
                Assert.That(matches, Does.Contain(gameObject));
        }
        finally
        {
            foreach (var gameObject in objects)
                if (gameObject != null)
                    UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void ConduitSearch_GenericSearchAddsTypeFilter()
    {
        var camera = ConduitSearch.Search<Camera>("Main Camera");
        var incompatibleMatches = ConduitSearch.SearchMany<Camera>("Main Camera t:Light");

        Assert.That(camera, Is.EqualTo(Camera.main));
        Assert.That(incompatibleMatches, Is.Empty);
    }

    [Test]
    public void ConduitSearch_GenericExactAssetPathSearchReturnsTypedAsset()
    {
        var material = ConduitSearch.Search<Material>(MaterialAsset);

        Assert.That(material, Is.EqualTo(AssetDatabase.LoadAssetAtPath<Material>(MaterialAsset)));
    }

    [Test]
    public void ConduitSearch_GenericExactHierarchySearchReturnsComponent()
    {
        var camera = ConduitSearch.Search<Camera>("/Main Camera");

        Assert.That(camera, Is.EqualTo(Camera.main));
    }

    [Test]
    public void ConduitSearch_GenericSearchExpandsMultipleComponents()
    {
        var objectName = "ConduitSearchComponents_" + Guid.NewGuid().ToString("N");
        var gameObject = new GameObject(objectName);
        var first = gameObject.AddComponent<BoxCollider>();
        var second = gameObject.AddComponent<BoxCollider>();
        try
        {
            var matches = ConduitSearch.SearchMany<BoxCollider>("/" + objectName);
            var exception = Assert.Throws<InvalidOperationException>(() => ConduitSearch.Search<BoxCollider>("/" + objectName));

            Assert.That(matches, Is.EqualTo(new[] { first, second }));
            Assert.That(exception!.Message, Does.Contain("Multiple objects match your query."));
            Assert.That(exception.Message, Does.Contain(ConduitObjectId.FormatObjectId(first)));
            Assert.That(exception.Message, Does.Contain(ConduitObjectId.FormatObjectId(second)));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void Show_AssetPathMatchExpandsAsset()
    {
        var output = ShowTool.Show(MaterialAsset);

        Assert.That(output, Does.StartWith($"Asset: {MaterialAsset}"));
        Assert.That(output, Does.Contain("Main Object:"));
        Assert.That(output, Does.Not.Contain("Imported Subassets:"));
    }

    [Test]
    public void Show_SearchMatchStaysOnExactObject()
    {
        var output = ShowTool.Show(CameraSearchQuery);
        var camera = Camera.main;
        Assert.That(camera, Is.Not.Null);

        Assert.That(output, Does.Contain($"Scene: {SceneAsset}"));
        Assert.That(
            output,
            Does.Contain(
                $"GameObject: Main Camera [{ConduitObjectId.FormatObjectId(camera!.gameObject)}]"
            )
        );
        Assert.That(output, Does.Not.Contain("Main Object:"));
        Assert.That(output, Does.Not.Contain("Imported Subassets:"));
    }

    [Test]
    public void Show_SceneHierarchyUsesCompactTreeLegendAndDuplicateComponentIdentifiers()
    {
        var assetPath = ConduitTestAssets.GetTemporaryPath("UnitTests", $"RepeatedComponents_{Guid.NewGuid():N}.unity");
        var originalActiveScene = SceneManager.GetActiveScene();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        var gameObject = new GameObject("ConduitRepeatedComponents");
        var firstChild = new GameObject("ConduitFirstChild");
        var secondChild = new GameObject("ConduitSecondChild");
        var grandChild = new GameObject("ConduitGrandChild");

        try
        {
            firstChild.transform.SetParent(gameObject.transform, false);
            secondChild.transform.SetParent(gameObject.transform, false);
            grandChild.transform.SetParent(firstChild.transform, false);
            SceneManager.MoveGameObjectToScene(gameObject, scene);
            gameObject.AddComponent<BoxCollider>();
            gameObject.AddComponent<BoxCollider>();
            gameObject.AddComponent<MeshCollider>();
            gameObject.AddComponent<MeshCollider>();
            gameObject.AddComponent<MeshCollider>();
            Assert.That(EditorSceneManager.SaveScene(scene, assetPath), Is.True);

            var output = ShowTool.Show(assetPath);

            Assert.That(
                output,
                Does.Contain("Components:\nBC=BoxCollider\nMC=MeshCollider\n\nHierarchy:")
            );
            Assert.That(
                output,
                Does.Contain(
                    $"ConduitRepeatedComponents [{ConduitObjectId.FormatObjectId(gameObject)} | BC | BC | MC ×3]\n" +
                    $"├─ConduitFirstChild [{ConduitObjectId.FormatObjectId(firstChild)}]\n" +
                    $"│ └─ConduitGrandChild [{ConduitObjectId.FormatObjectId(grandChild)}]\n" +
                    $"└─ConduitSecondChild [{ConduitObjectId.FormatObjectId(secondChild)}]"
                )
            );
            Assert.That(output, Does.Not.Contain("MC | MC | MC"));
            Assert.That(output, Does.Not.Contain("- BC = BoxCollider"));
            Assert.That(output, Does.Not.Contain("├───"));
        }
        finally
        {
            if (scene.IsValid() && scene.isLoaded)
                EditorSceneManager.CloseScene(scene, true);

            if (originalActiveScene.IsValid() && originalActiveScene.isLoaded)
                SceneManager.SetActiveScene(originalActiveScene);

            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void Show_LargeGameObjectHierarchyShowsParentDetailsAndCompactTree()
    {
        var root = new GameObject("ConduitCompactRoot");
        root.AddComponent<BoxCollider>();
        GameObject? firstChild = null;

        try
        {
            for (var index = 0; index < 8; index++)
            {
                var childName = $"ConduitCompactChild{index.ToString(CultureInfo.InvariantCulture)}";
                var child = new GameObject(childName);
                child.transform.SetParent(root.transform, false);
                if (index == 0)
                {
                    child.AddComponent<MeshCollider>();
                    firstChild = child;
                }
            }

            Assert.That(firstChild, Is.Not.Null);

            var output = ShowTool.Show(ConduitObjectId.FormatObjectId(root));

            Assert.That(
                output,
                Does.Contain(
                    $"GameObject: ConduitCompactRoot [{ConduitObjectId.FormatObjectId(root)}]"
                )
            );
            Assert.That(output, Does.Contain("Components:\nBC=BoxCollider\nMC=MeshCollider\n\nHierarchy:"));
            Assert.That(
                output,
                Does.Contain(
                    $"ConduitCompactRoot [{ConduitObjectId.FormatObjectId(root)} | BC]\n" +
                    $"├─ConduitCompactChild0 [{ConduitObjectId.FormatObjectId(firstChild!)} | MC]"
                )
            );
            Assert.That(
                output,
                Does.Not.Contain(
                    "GameObject: ConduitCompactRoot/ConduitCompactChild0 "
                    + $"[{ConduitObjectId.FormatObjectId(firstChild!)}]"
                )
            );
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    [Test]
    public void Show_CustomImplementation_UsesToStringForMcp()
    {
        var assetPath = ConduitTestAssets.GetTemporaryPath("UnitTests", $"CustomShow_{Guid.NewGuid():N}.asset");
        var target = ScriptableObject.CreateInstance<ConduitCustomShowAsset>();
        try
        {
            AssetDatabase.CreateAsset(target, assetPath);
            var output = ShowTool.Show(assetPath);
            Assert.That(output, Is.EqualTo("Custom MCP show output"));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void Show_NonSerializableEnumerableThatThrowsDuringEnumerationMarksFieldUnavailable()
    {
        var assetPath = ConduitTestAssets.GetTemporaryPath("UnitTests", $"ThrowingEnumerable_{Guid.NewGuid():N}.asset");
        var target = ScriptableObject.CreateInstance<ConduitThrowingEnumerableAsset>();
        try
        {
            AssetDatabase.CreateAsset(target, assetPath);
            var output = ShowTool.Show(assetPath);

            Assert.That(output, Does.Contain("Non-Serializable:"));
            Assert.That(
                output,
                Does.Contain("- throwingEnumerable: <unavailable: NotImplementedException: The method or operation is not implemented.>")
            );
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void Show_NonSerializableUnityIndexableFormatsByIndex()
    {
        if (AppDomain.CurrentDomain
            .GetAssemblies()
            .All(assembly => assembly.GetName().Name != "Unity.Collections"))
            Assert.Ignore("Unity.Collections is not installed.");

        var assetPath = ConduitTestAssets.GetTemporaryPath("UnitTests", $"NativeIndexable_{Guid.NewGuid():N}.asset");
        var target = ScriptableObject.CreateInstance<ConduitNativeIndexableAsset>();
        try
        {
            target.Initialize();
            AssetDatabase.CreateAsset(target, assetPath);
            var output = ShowTool.Show(assetPath);

            Assert.That(output, Does.Contain("- indexableNumbers: [1, 2, 3]"));
        }
        finally
        {
            target.Dispose();
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void Show_PersistentObjectNameMatchingAssetFilenameOmitsRedundantName()
    {
        var assetName = $"RedundantName_{Guid.NewGuid():N}";
        var assetPath = ConduitTestAssets.GetTemporaryPath("UnitTests", $"{assetName}.asset");
        var target = ScriptableObject.CreateInstance<ConduitShowFormatAsset>();
        try
        {
            target.name = assetName;
            AssetDatabase.CreateAsset(target, assetPath);

            var output = ShowTool.Show(assetPath);

            Assert.That(output, Does.Contain($"Main Object: ConduitShowFormatAsset({assetPath})"));
            Assert.That(output, Does.Not.Contain($"ConduitShowFormatAsset(\"{assetName}\", {assetPath})"));
        }
        finally
        {
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void Show_SerializableHierarchyUsesLocalPropertyNames()
    {
        var target = ScriptableObject.CreateInstance<ConduitNestedShowAsset>();
        try
        {
            var output = ShowTool.Show(ConduitObjectId.FormatObjectId(target));

            Assert.That(
                output,
                Does.Contain(
                    "    - loadout:\n" +
                    "      - inventoryLoot:\n" +
                    "        - entries: [1, 2]\n" +
                    "        - chooseSingle: false"
                )
            );
            Assert.That(output, Does.Not.Contain("loadout.inventoryLoot"));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(target);
        }
    }

    [Test]
    public void Search_WindowTarget_UsesSharedEditorWindowSelector()
    {
        RequireInteractiveEditorWindows();

        var alphaWindow = OpenScreenshotTestWindow<ConduitWindowMatchAlphaWindow>();
        var betaWindow = OpenScreenshotTestWindow<ConduitWindowMatchBetaWindow>();

        Assert.That(ConduitEditorWindowDocking.IsDockedInMainWindow(alphaWindow), Is.True);
        Assert.That(ConduitEditorWindowDocking.IsDockedInMainWindow(betaWindow), Is.True);

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

        Assert.That(result, Does.StartWith("- "));
        Assert.That(result, Does.Contain(" | EditMode"));
    }

    [Test]
    public void Search_TestQuery_PlayModeFilterListsRuntimeBridgeTests()
    {
        if (Type.GetType("RuntimeBridgeTests, Conduit.Tests.PlayMode") == null)
            Assert.Ignore("The opt-in runtime test assembly is not enabled in this project.");

        var result = ConduitSearchUtility.Search("t:test playmode");

        Assert.That(result, Does.Contain("RuntimeBridgeTests.ArtifactTransferRejectsModifiedContent | PlayMode"));
        Assert.That(result, Does.Not.Contain(" | EditMode"));
    }

    [Test]
    public void Search_TestDiscoveryAcceptsProjectAssembliesOutsideTestNamedFolders()
    {
        Assert.That(
            UnityTestSearch.IsDiscoverableProjectSourceFile(
                "Assets/Features/PlayerProbe/LunaConduitPlayerProbe.cs"
            ),
            Is.True
        );
        Assert.That(
            UnityTestSearch.IsDiscoverableProjectSourceFile(
                @"Assets\Features\PlayerProbe\LunaConduitPlayerProbe.cs"
            ),
            Is.True
        );
        Assert.That(
            UnityTestSearch.IsDiscoverableProjectSourceFile("Library/Generated/Probe.cs"),
            Is.False
        );
    }

    [Test]
    public void Search_QueryWithUnsupportedOrSyntaxAndNoMatchesExplainsConstraint()
    {
        var result = ConduitSearchUtility.Search("t:prefab ConduitMissingAlpha OR t:prefab ConduitMissingBeta");

        Assert.That(
            result,
            Is.EqualTo(
                "Unity search does not support OR operators. Run separate queries instead."
            )
        );
    }

    [Test]
    public void Search_QueryWithUnsupportedPipeOrSyntaxAndNoMatchesExplainsConstraint()
    {
        var result = ConduitSearchUtility.Search("t:prefab ConduitMissingAlpha || t:prefab ConduitMissingBeta");

        Assert.That(
            result,
            Is.EqualTo(
                "Unity search does not support OR operators. Run separate queries instead."
            )
        );
    }

    [Test]
    public void Show_QueryWithUnsupportedOrSyntaxAndNoMatchesExplainsConstraint()
    {
        var result = ShowTool.Show("t:prefab ConduitMissingAlpha OR t:prefab ConduitMissingBeta");

        Assert.That(
            result,
            Is.EqualTo(
                "Unity search does not support OR operators. Run separate queries instead."
            )
        );
    }

    [Test]
    public void Show_WindowTarget_ShowsBasicEditorWindowInfo()
    {
        RequireInteractiveEditorWindows();

        var result = ShowTool.Show("window:CaptureProbe");

        Assert.That(result, Does.Contain("Editor Window: Conduit Capture Probe"));
        Assert.That(result, Does.Contain("Type: ConduitCaptureProbeWindow"));
        Assert.That(result, Does.Contain("Title: Conduit Capture Probe"));
        Assert.That(result, Does.Contain("Object: "));
        Assert.That(result, Does.Contain("Focused: "));
        Assert.That(result, Does.Contain("Docked: yes"));
        Assert.That(result, Does.Contain("Position: x="));
    }
}
