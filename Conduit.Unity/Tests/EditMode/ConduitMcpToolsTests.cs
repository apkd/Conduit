#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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
    public void ObjectId_RoundTripsAcrossUnityVersions()
    {
        var camera = Camera.main;
        Assert.That(camera, Is.Not.Null);

        Assert.That(
            ConduitUtility.ResolveObjectId(ConduitUtility.GetObjectId(camera)),
            Is.SameAs(camera)
        );
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
    public void ViewBurstAsmCommand_Parses()
    {
        var command = ConduitToolRunner.ParseIncomingCommand(BridgeCommandTypes.ViewBurstAsm);

        Assert.That(command, Is.EqualTo(BridgeCommandKind.ViewBurstAsm));
    }

    [Test]
    public void ReflectCommand_Parses()
    {
        var command = ConduitToolRunner.ParseIncomingCommand(BridgeCommandTypes.Reflect);

        Assert.That(command, Is.EqualTo(BridgeCommandKind.Reflect));
    }

    [Test]
    public void ReimportAssetsCommand_Parses()
    {
        var command = ConduitToolRunner.ParseIncomingCommand(BridgeCommandTypes.ReimportAssets);

        Assert.That(command, Is.EqualTo(BridgeCommandKind.ReimportAssets));
    }

    [Test]
    public void ResolveAssetPaths_UsesObjectQueryAndReturnsAssetsOnly()
    {
        var materialMatches = ConduitSearchUtility.ResolveAssetPaths(MaterialAsset);
        Assert.That(materialMatches, Is.EqualTo(new[] { MaterialAsset }));

        var cameraMatches = ConduitSearchUtility.ResolveAssetPaths(CameraSearchQuery);
        Assert.That(cameraMatches, Is.Empty);
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
            Assert.That(exception.Message, Does.Contain(ConduitUtility.FormatObjectId(first)));
            Assert.That(exception.Message, Does.Contain(ConduitUtility.FormatObjectId(second)));
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
            Assert.That(exception.Message, Does.Contain(ConduitUtility.FormatObjectId(first)));
            Assert.That(exception.Message, Does.Contain(ConduitUtility.FormatObjectId(second)));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void ReimportAssetFilenames_FormatOmitsAssetPaths()
    {
        var output = ConduitToolRunner.FormatReimportedAssetFilenames(
            "Assets/Temp/Foo.asset\nPackages/dev.tryfinally.conduit/Tests/EditMode/TestAssets/JsonOverwriteMaterial.mat"
        );

        Assert.That(output, Does.Contain("- Foo.asset"));
        Assert.That(output, Does.Contain("- JsonOverwriteMaterial.mat"));
        Assert.That(output, Does.Not.Contain("Assets/Temp"));
        Assert.That(output, Does.Not.Contain("Packages/dev.tryfinally.conduit"));
    }

    [Test]
    public void ProfilerCommands_Parse()
    {
        Assert.That(
            ConduitToolRunner.ParseIncomingCommand(BridgeCommandTypes.ProfilerRecord),
            Is.EqualTo(BridgeCommandKind.ProfilerRecord)
        );
        Assert.That(
            ConduitToolRunner.ParseIncomingCommand(BridgeCommandTypes.ProfilerOverview),
            Is.EqualTo(BridgeCommandKind.ProfilerOverview)
        );
        Assert.That(
            ConduitToolRunner.ParseIncomingCommand(BridgeCommandTypes.ProfilerBrowse),
            Is.EqualTo(BridgeCommandKind.ProfilerBrowse)
        );
    }

    [Test]
    public void ProfilerCapturePath_BareFileNameUsesTempProfiler()
    {
        var path = profiler.ResolveCapturePathForTest("sample", allocateDefault: false);

        Assert.That(path.DisplayPath, Is.EqualTo("Temp/profiler/sample.data"));
    }

    [Test]
    public void ProfilerFrameRange_UsesAvailableFrameOrdinalsAndClampsLargeRanges()
    {
        var frames = profiler.ResolveFrameRangeForTest(10, "0..^1", out var warnings);

        Assert.That(frames, Is.EqualTo(new[] { 1000, 1001, 1002, 1003, 1004, 1005, 1006, 1007, 1008, 1009 }));
        Assert.That(warnings, Is.Empty);

        var clamped = profiler.ResolveFrameRangeForTest(2500, "0..^1", out warnings);

        Assert.That(clamped, Has.Count.EqualTo(2000));
        Assert.That(clamped[0], Is.EqualTo(1500));
        Assert.That(clamped[^1], Is.EqualTo(3499));
        Assert.That(warnings, Does.Contain("frame_range_clamped_to_latest_2000"));
    }

    [Test]
    public void ProfilerBrowse_NonTrivialFilterDependsOnSort()
    {
        Assert.That(profiler.IsNonTrivialForTest(totalMs: 1.0, selfMs: 0.1, gcBytes: 0, calls: 1, frameTimeMs: 100, sort: "total_ms"), Is.True);
        Assert.That(profiler.IsNonTrivialForTest(totalMs: 0.9, selfMs: 0.9, gcBytes: 0, calls: 1, frameTimeMs: 100, sort: "total_ms"), Is.False);
        Assert.That(profiler.IsNonTrivialForTest(totalMs: 10, selfMs: 0.9, gcBytes: 0, calls: 1, frameTimeMs: 100, sort: "self_ms"), Is.False);
        Assert.That(profiler.IsNonTrivialForTest(totalMs: 0, selfMs: 0, gcBytes: 1, calls: 1, frameTimeMs: 100, sort: "gc_bytes"), Is.True);
    }

    [Test]
    public void ProfilerBrowse_WorkerAggregateSumsMatchingHierarchyPaths()
    {
        var worker0 = Row(
            "Worker 0",
            totalMs: 4,
            selfMs: 1,
            gcBytes: 32,
            calls: 1,
            Row("Execute", 3, 1, 16, 2, Row("JobA", 2, 2, 8, 2))
        );
        var worker1 = Row(
            "Worker 1",
            totalMs: 6,
            selfMs: 2,
            gcBytes: 64,
            calls: 1,
            Row("Execute", 5, 2, 32, 3, Row("JobA", 3, 3, 16, 3), Row("JobB", 1, 1, 0, 1))
        );

        var aggregate = profiler.AggregateWorkerHierarchiesForTest(worker0, worker1);

        Assert.That(aggregate.Name, Is.EqualTo("Job Workers"));
        Assert.That(aggregate.TotalMs, Is.EqualTo(10));
        Assert.That(aggregate.SelfMs, Is.EqualTo(3));
        Assert.That(aggregate.GcBytes, Is.EqualTo(96));
        Assert.That(aggregate.Calls, Is.EqualTo(2));
        Assert.That(aggregate.ContributingWorkerCount, Is.EqualTo(2));
        Assert.That(aggregate.MinTotalMs, Is.EqualTo(4));
        Assert.That(aggregate.MaxTotalMs, Is.EqualTo(6));
        Assert.That(profiler.GetWorkerMeanMsForTest(aggregate), Is.EqualTo(5));
        Assert.That(aggregate.Children, Has.Count.EqualTo(1));

        var execute = aggregate.Children[0];
        Assert.That(execute.TotalMs, Is.EqualTo(8));
        Assert.That(execute.SelfMs, Is.EqualTo(3));
        Assert.That(execute.GcBytes, Is.EqualTo(48));
        Assert.That(execute.Calls, Is.EqualTo(5));
        Assert.That(execute.ContributingWorkerCount, Is.EqualTo(2));
        Assert.That(execute.MinTotalMs, Is.EqualTo(3));
        Assert.That(execute.MaxTotalMs, Is.EqualTo(5));
        Assert.That(execute.Children, Has.Count.EqualTo(2));
        var jobA = execute.Children.Find(row => row.Name == "JobA")!;
        Assert.That(jobA.TotalMs, Is.EqualTo(5));
        Assert.That(jobA.ContributingWorkerCount, Is.EqualTo(2));
        Assert.That(jobA.MinTotalMs, Is.EqualTo(2));
        Assert.That(jobA.MaxTotalMs, Is.EqualTo(3));

        var jobB = execute.Children.Find(row => row.Name == "JobB")!;
        Assert.That(jobB.TotalMs, Is.EqualTo(1));
        Assert.That(jobB.ContributingWorkerCount, Is.EqualTo(1));
        Assert.That(profiler.GetWorkerMeanMsForTest(jobB), Is.EqualTo(1));
        Assert.That(profiler.GetNormalizedWorkerMsForTest(jobB, workerCount: 2), Is.EqualTo(0.5));
        Assert.That(jobB.MinTotalMs, Is.EqualTo(1));
        Assert.That(jobB.MaxTotalMs, Is.EqualTo(1));

        static profiler.HierarchyRow Row(
            string name,
            double totalMs,
            double selfMs,
            double gcBytes,
            double calls,
            params profiler.HierarchyRow[] children
        )
        {
            var row = new profiler.HierarchyRow
            {
                Name = name,
                TotalMs = totalMs,
                SelfMs = selfMs,
                GcBytes = gcBytes,
                Calls = calls,
            };
            row.Children.AddRange(children);
            return row;
        }
    }

    [Test]
    public void ProfilerOverview_ThreadLabelsOnlyShowMainRenderAndJobWorkers()
    {
        Assert.That(profiler.TryGetOverviewThreadLabelForTest("Main Thread", "", out var label), Is.True);
        Assert.That(label, Is.EqualTo("main"));

        Assert.That(profiler.TryGetOverviewThreadLabelForTest("Render Thread", "", out label), Is.True);
        Assert.That(label, Is.EqualTo("render"));

        Assert.That(profiler.TryGetOverviewThreadLabelForTest("Worker 7", "Job", out label), Is.True);
        Assert.That(label, Is.EqualTo("worker7"));

        Assert.That(profiler.TryGetOverviewThreadLabelForTest("Job Worker 12", "Job", out label), Is.True);
        Assert.That(label, Is.EqualTo("worker12"));

        Assert.That(profiler.TryGetOverviewThreadLabelForTest("Background Worker", "Loading", out _), Is.False);
        Assert.That(profiler.TryGetOverviewThreadLabelForTest("GfxDeviceWorker", "", out _), Is.False);
    }

    [Test]
    public void ProfilerOverview_ThreadLabelsUseStableDisplayOrder()
    {
        var labels = profiler.FormatThreadLabelsForTest(new[] { "worker10", "render", "worker2", "main", "worker0" });

        Assert.That(labels, Is.EqualTo("main, render, worker0, worker2, worker10"));
    }

    [Test]
    public void ProfilerOverview_InterestingSampleFilterSkipsContainersAndNoise()
    {
        Assert.That(
            profiler.ShouldIncludeOverviewSampleForTest("EditorLoop", totalMs: 33, selfMs: 33, gcBytes: 0, frameTimeMs: 40, childCount: 0, mode: "cpu_ms"),
            Is.False
        );
        Assert.That(
            profiler.ShouldIncludeOverviewSampleForTest("EnemySystem.Update", totalMs: 2, selfMs: 2, gcBytes: 0, frameTimeMs: 100, childCount: 0, mode: "cpu_ms"),
            Is.True
        );
        Assert.That(
            profiler.ShouldIncludeOverviewSampleForTest("Tiny.Marker", totalMs: 0.5, selfMs: 0.5, gcBytes: 0, frameTimeMs: 100, childCount: 0, mode: "cpu_ms"),
            Is.False
        );
        Assert.That(
            profiler.ShouldIncludeOverviewSampleForTest("Allocator", totalMs: 0.01, selfMs: 0.01, gcBytes: 64, frameTimeMs: 100, childCount: 0, mode: "gc_kb"),
            Is.True
        );
        Assert.That(
            profiler.ShouldIncludeOverviewSampleForTest("Allocator.Parent", totalMs: 0.01, selfMs: 0.01, gcBytes: 64, frameTimeMs: 100, childCount: 1, mode: "gc_kb"),
            Is.False
        );
    }

    [Test]
    public void ProfilerOverview_ActionableCpuUsesLeafTotalAndParentSelf()
    {
        Assert.That(profiler.GetActionableCpuMsForTest(totalMs: 20, selfMs: 1, childCount: 0), Is.EqualTo(20));
        Assert.That(profiler.GetActionableCpuMsForTest(totalMs: 20, selfMs: 1, childCount: 3), Is.EqualTo(1));
    }

    [Test]
    public void ProfilerOverview_SamplePathsUseCompactMarkerNames()
    {
        var path = string.Join(
            "/",
            "EditorLoop",
            "Application.Tick",
            "UnityEngine.IMGUIModule.dll!UnityEngine::GUIUtility.ProcessEvent()",
            "UnityEngine.UIElementsModule.dll!::<>c.<.cctor>b__1_2()",
            "UnityEngine.UIElementsModule.dll!UnityEngine.UIElements::Panel.Render()",
            "UnityEditor.CoreModule.dll!Unity.Profiling.Editor::ProfilerModule.DrawChartView()",
            "UnityEngine.CoreModule.dll!Unity.Profiling::ProfilerMarker.Auto()",
            "UnityEditor.CoreModule.dll!UnityEditorInternal::Chart.DrawChartItemLine()"
        );

        var formatted = profiler.FormatSamplePathForTest(path);

        Assert.That(
            formatted,
            Is.EqualTo(
                "EditorLoop/Application.Tick/GUIUtility.ProcessEvent/<>c.<.cctor>b__1_2/Panel.Render/Unity.Profiling.Editor::ProfilerModule.DrawChartView/Unity.Profiling::ProfilerMarker.Auto/UnityEditorInternal::Chart.DrawChartItemLine"
            )
        );
        Assert.That(formatted, Does.Not.Contain(".dll!"));
        Assert.That(formatted, Does.Not.Contain("()"));
    }

    [Test]
    public void Status_IncludesProfilerStatusLineInSnapshot()
    {
        var snapshot = status.Status();

        Assert.That(snapshot, Does.Contain("\"profiler_status_line\""));
        Assert.That(snapshot, Does.Contain("Profiler:"));
    }

    [Test]
    public void Status_IncludesTestRunnerStateInSnapshot()
    {
        var snapshot = status.Status();

        Assert.That(snapshot, Does.Contain("\"is_test_runner_active\""));
        Assert.That(snapshot, Does.Contain("\"active_test_mode\""));
    }

    [Test]
    public void ReflectTypes_SearchesByTypeNameAndKind()
    {
        var result = reflect.Reflect(new[] { "classes", "ConduitReflectDerivedFixture", string.Empty });

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(result.return_value, Does.Contain("class ConduitReflectDerivedFixture"));
        Assert.That(result.return_value, Does.Contain("Base: ConduitReflectBaseFixture"));
        Assert.That(result.return_value, Does.Contain("Interfaces: ConduitReflectInterfaceFixture"));
        Assert.That(result.return_value, Does.Not.Contain("Types:"));
    }

    [Test]
    public void ReflectModes_AreCaseInsensitive()
    {
        var result = reflect.Reflect(new[] { "CLASSES", "ConduitReflectDerivedFixture", string.Empty });

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(result.return_value, Does.Contain("class ConduitReflectDerivedFixture"));
    }

    [Test]
    public void ReflectTypes_FiltersStructsAndEnums()
    {
        var structResult = reflect.Reflect(new[] { "structs", "ConduitReflectStructFixture", string.Empty });
        var enumResult = reflect.Reflect(new[] { "enums", "ConduitReflectEnumFixture", string.Empty });

        Assert.That(structResult.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(structResult.return_value, Does.Contain("struct ConduitReflectStructFixture"));
        Assert.That(enumResult.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(enumResult.return_value, Does.Contain("enum ConduitReflectEnumFixture"));
    }

    [Test]
    public void ReflectTypes_FiltersInterfacesAndDelegates()
    {
        var interfaceResult = reflect.Reflect(new[] { "interfaces", "ConduitReflectInterfaceFixture", string.Empty });
        var delegateResult = reflect.Reflect(new[] { "delegates", "ConduitReflectDelegateFixture", string.Empty });

        Assert.That(interfaceResult.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(interfaceResult.return_value, Does.Contain("interface ConduitReflectInterfaceFixture"));
        Assert.That(delegateResult.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(delegateResult.return_value, Does.Contain("delegate ConduitReflectDelegateFixture"));
    }

    [Test]
    public void ReflectTypes_SearchesByDirectMemberName()
    {
        var result = reflect.Reflect(new[] { "types", string.Empty, "ReflectBaseOnlyMethod" });

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(result.return_value, Does.Contain("ConduitReflectBaseFixture"));
        Assert.That(result.return_value, Does.Not.Contain("ConduitReflectDerivedFixture"));
    }

    [Test]
    public void ReflectMembers_TargetTypeIncludesDeclaredAndInheritedMembers()
    {
        var result = reflect.Reflect(new[] { "members", "ConduitReflectDerivedFixture", string.Empty });

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(result.return_value, Does.Contain("Declared on ConduitReflectDerivedFixture"));
        Assert.That(result.return_value, Does.Contain("private int derivedPrivateField"));
        Assert.That(result.return_value, Does.Contain("public string DerivedProperty { get; private set; }"));
        Assert.That(result.return_value, Does.Contain("public T GenericMethod<T>(ref int value, out string text, params T[] items)"));
        Assert.That(result.return_value, Does.Contain("Inherited from ConduitReflectBaseFixture"));
        Assert.That(result.return_value, Does.Contain("protected string ReflectBaseOnlyMethod()"));
        Assert.That(result.return_value, Does.Contain("Interface ConduitReflectInterfaceFixture"));
        Assert.That(result.return_value, Does.Not.Contain("System.Object"));
    }

    [Test]
    public void ReflectMembers_WideSearchUsesDirectContainingTypeOnly()
    {
        var result = reflect.Reflect(new[] { "methods", string.Empty, "ReflectBaseOnlyMethod" });

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(result.return_value, Does.Contain("Containing Type: ConduitReflectBaseFixture"));
        Assert.That(result.return_value, Does.Not.Contain("Containing Type: ConduitReflectDerivedFixture"));
    }

    [Test]
    public void ReflectMembers_AmbiguousTypeReturnsCandidates()
    {
        var result = reflect.Reflect(new[] { "members", "ConduitReflectAmbiguous", string.Empty });

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.AmbiguousTarget));
        Assert.That(result.diagnostic, Does.Contain("Multiple types match"));
        Assert.That(result.diagnostic, Does.Contain("ConduitReflectAmbiguousAlpha"));
        Assert.That(result.diagnostic, Does.Contain("ConduitReflectAmbiguousBeta"));
    }

    [Test]
    public void ReflectMembers_WideSearchTruncatesAtTwoHundredRows()
    {
        var result = reflect.Reflect(new[] { "members", string.Empty, "ToString" });

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(result.return_value, Does.Contain("showing 200"));
        Assert.That(result.return_value, Does.Contain("Truncated:"));
    }

    [Test]
    public void ReflectMembers_WideSearchRanksExactMatchesBeforeSubstringMatches()
    {
        var result = reflect.Reflect(new[] { "methods", string.Empty, "ReflectRank" });

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
        var exactIndex = result.return_value.IndexOf("public void ReflectRank()", StringComparison.Ordinal);
        var looseIndex = result.return_value.IndexOf("public void PrefixReflectRankSuffix()", StringComparison.Ordinal);

        Assert.That(exactIndex, Is.GreaterThanOrEqualTo(0), result.return_value);
        Assert.That(looseIndex, Is.GreaterThanOrEqualTo(0), result.return_value);
        Assert.That(exactIndex, Is.LessThan(looseIndex), result.return_value);
    }

    [Test]
    public void ReflectMembers_NonTruncatedSearchOmitsHeaderAndNoMatchesAreExplicit()
    {
        var matched = reflect.Reflect(new[] { "methods", string.Empty, "ReflectBaseOnlyMethod" });
        var noMatch = reflect.Reflect(new[] { "methods", string.Empty, "DefinitelyNotAConduitReflectMember" });

        Assert.That(matched.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(matched.return_value, Does.Not.Contain("Members:"));
        Assert.That(noMatch.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(noMatch.return_value, Is.EqualTo("No members matched."));
    }

    [Test]
    public void ReflectTypes_NoMatchesAreExplicit()
    {
        var result = reflect.Reflect(new[] { "types", "DefinitelyNotAConduitReflectType", string.Empty });

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(result.return_value, Is.EqualTo("No types matched."));
    }

    [Test]
    public void ConduitReflect_TypeHelpersReturnTypedResults()
    {
        var type = ConduitReflect.Type("ConduitReflectDerivedFixture");
        var interfaces = ConduitReflect.Interfaces("ConduitReflectInterfaceFixture");

        Assert.That(type, Is.EqualTo(typeof(ConduitReflectDerivedFixture)));
        Assert.That(interfaces, Is.EqualTo(new[] { typeof(ConduitReflectInterfaceFixture) }));
    }

    [Test]
    public void ConduitReflect_TypeSearchCanUseMemberQuery()
    {
        var types = ConduitReflect.Types(member: "ReflectBaseOnlyMethod");

        Assert.That(types, Has.Member(typeof(ConduitReflectBaseFixture)));
        Assert.That(types, Has.No.Member(typeof(ConduitReflectDerivedFixture)));
    }

    [Test]
    public void ConduitReflect_MemberHelpersReturnTypedResults()
    {
        var baseMethod = ConduitReflect.Method(type: "ConduitReflectDerivedFixture", member: "ReflectBaseOnlyMethod");
        var constructors = ConduitReflect.Constructors("ConduitReflectDerivedFixture");

        Assert.That(baseMethod.DeclaringType, Is.EqualTo(typeof(ConduitReflectBaseFixture)));
        Assert.That(constructors, Has.Length.GreaterThanOrEqualTo(1));
        Assert.That(constructors, Has.Some.Property(nameof(ConstructorInfo.DeclaringType)).EqualTo(typeof(ConduitReflectDerivedFixture)));
    }

    [Test]
    public void ConduitReflect_FindHandlesCardinalityAndCompatibility()
    {
        var manyMethods = ConduitReflect.FindMany<MethodInfo>("members", type: "ConduitReflectDerivedFixture", member: "Reflect");
        var ambiguous = Assert.Throws<InvalidOperationException>(() => ConduitReflect.Type("ConduitReflectAmbiguous"));
        var missing = Assert.Throws<InvalidOperationException>(() => ConduitReflect.Type("DefinitelyNotAConduitReflectType"));
        var invalidType = Assert.Throws<InvalidOperationException>(() => ConduitReflect.Find<MethodInfo>("fields", type: "ConduitReflectDerivedFixture"));
        var empty = Assert.Throws<InvalidOperationException>(() => ConduitReflect.Types());

        Assert.That(manyMethods, Is.Not.Empty);
        Assert.That(ambiguous!.Message, Does.Contain("Multiple reflected results match"));
        Assert.That(ambiguous.Message, Does.Contain("ConduitReflectAmbiguousAlpha"));
        Assert.That(missing!.Message, Does.Contain("No reflected result matched"));
        Assert.That(invalidType!.Message, Does.Contain("cannot return MethodInfo"));
        Assert.That(empty!.Message, Does.Contain("reflect type modes require"));
    }

    [Test]
    public void ViewBurstAsmMatch_SelectsExactAndUniqueSubstringTargets()
    {
        var targets = CreateBurstAsmTargets();

        var exact = view_burst_asm.MatchTarget("Gameplay.Motion.MoveJob - (IJob)", targets);
        var substring = view_burst_asm.MatchTarget("RenderChunk", targets);

        Assert.That(exact.Kind, Is.EqualTo(BurstAsmTargetMatchKind.Matched));
        Assert.That(exact.SelectedIndex, Is.EqualTo(0));
        Assert.That(substring.Kind, Is.EqualTo(BurstAsmTargetMatchKind.Matched));
        Assert.That(substring.SelectedIndex, Is.EqualTo(2));
    }

    [Test]
    public void ViewBurstAsmMatch_UsesTokenScoringForFuzzyNames()
    {
        var targets = CreateBurstAsmTargets();

        var match = view_burst_asm.MatchTarget("render execute", targets);

        Assert.That(match.Kind, Is.EqualTo(BurstAsmTargetMatchKind.Matched));
        Assert.That(match.SelectedIndex, Is.EqualTo(2));
    }

    [Test]
    public void ViewBurstAsmMatch_RejectsAmbiguousTargets()
    {
        var targets = CreateBurstAsmTargets();

        var match = view_burst_asm.MatchTarget("motion job", targets);

        Assert.That(match.Kind, Is.EqualTo(BurstAsmTargetMatchKind.Ambiguous));
        Assert.That(match.CandidateIndexes, Is.EquivalentTo(new[] { 0, 1 }));
    }

    [Test]
    public void ViewBurstAsmMatch_ReturnsNoMatchWithCandidates()
    {
        var targets = CreateBurstAsmTargets();

        var match = view_burst_asm.MatchTarget("missing target", targets);

        Assert.That(match.Kind, Is.EqualTo(BurstAsmTargetMatchKind.None));
        Assert.That(match.CandidateIndexes, Has.Length.EqualTo(3));
    }

    [Test]
    public void ViewBurstAsmNoMatch_EmptyQueryShowsOnlyCandidates()
    {
        var targets = CreateBurstAsmTargets();

        var diagnostic = view_burst_asm.NoMatchDiagnostic(string.Empty, targets, new[] { 0, 1 });

        Assert.That(diagnostic, Does.StartWith("Candidates:"));
        Assert.That(diagnostic, Does.Not.Contain("No Burst compile target matched"));
        Assert.That(diagnostic, Does.Contain("- Gameplay.Motion.MoveJob - (IJob)"));
        Assert.That(diagnostic, Does.Contain("- Gameplay.Motion.MoveParticlesJob - (IJob)"));
    }

    [Test]
    public void ViewBurstAsmOptions_UseInspectorDefaults()
    {
        var fixture = new BurstInspectorOptionsFixture();

        view_burst_asm.ApplyInspectorOptionOverrides(fixture);
        var options = view_burst_asm.BuildInspectorOptions("--float-mode=Default");

        Assert.That(fixture.EnableBurstSafetyChecks, Is.False);
        Assert.That(fixture.ForceEnableBurstSafetyChecks, Is.False);
        Assert.That(fixture.EnableBurstDebug, Is.False);
        Assert.That(options, Does.Contain("--float-mode=Default"));
        Assert.That(options, Does.Contain("--disable-warnings=BC1370;BC1322"));
        Assert.That(options, Does.Contain("--target=Auto"));
        Assert.That(options, Does.Contain("--debug=2"));
        Assert.That(options, Does.Not.Contain("--disable-function-caching"));
        Assert.That(options, Does.Not.Contain("--disable-assembly-caching"));
        Assert.That(options, Does.EndWith("--dump=Asm"));
    }

    [Test]
    public void ViewBurstAsmOutput_EmptyDisassemblyReportsCompileFailure()
    {
        var target = new BurstTarget("Example.BrokenJob - (IJob)", "Execute", "Example", "Example.BrokenJob");

        var diagnostic = view_burst_asm.BuildEmptyDisassemblyDiagnostic(target);

        Assert.That(diagnostic, Is.EqualTo("Failed to compile 'BrokenJob - (IJob)': Burst returned no assembly or diagnostic text."));
        Assert.That(diagnostic, Does.Not.Contain("Burst Inspector"));
    }

    [Test]
    public void ViewBurstAsmCleanup_StripsOnlyTrailingTemporaryDirectiveBlocks()
    {
        var input = string.Join("\n",
            "mov eax, ecx",
            "ret",
            "    .size BurstMethod, .-BurstMethod",
            ".Ltmp99:",
            "    .quad 1",
            ".Ltmp100:",
            "    .asciz \"debug\"");

        var result = view_burst_asm.StripTrailingTemporaryLabelBlocks(input);

        Assert.That(result, Is.EqualTo(string.Join("\n",
            "mov eax, ecx",
            "ret",
            "    .size BurstMethod, .-BurstMethod")));
    }

    [Test]
    public void ViewBurstAsmCleanup_PreservesMiddleAndInstructionTemporaryLabels()
    {
        var middleLabel = string.Join("\n",
            "mov eax, ecx",
            ".Ltmp99:",
            "add eax, 1",
            "ret");
        var instructionSuffix = string.Join("\n",
            "mov eax, ecx",
            ".Ltmp100:",
            "ret");

        Assert.That(view_burst_asm.StripTrailingTemporaryLabelBlocks(middleLabel), Is.EqualTo(middleLabel));
        Assert.That(view_burst_asm.StripTrailingTemporaryLabelBlocks(instructionSuffix), Is.EqualTo(instructionSuffix));
    }

    [Test]
    public void ViewBurstAsmCleanup_CompactsManagedSymbolsAndSourceLocations()
    {
        const string hash = "7435d70d723590c51e89202ae2f9be71";
        const string testAssembly = "BurstCanvasProject.Tests.RuntimeCommon, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null";
        const string runtimeAssembly = "BurstCanvas.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null";
        var jumpSymbol =
            "BurstCanvas.BurstBrainRuntimeTraversal`2" +
            "[[BurstCanvas.TrackedGraphTestActionBlock+Wrapper, " + testAssembly + "]," +
            "[BurstCanvas.TrackedGraphTestConditionBlock+Wrapper, " + testAssembly + "]], " +
            runtimeAssembly +
            ".AbortFsmState(" +
            "BurstCanvas.NativeBurstBrainProgram&, " + runtimeAssembly + " program, " +
            "BurstCanvas.NativeBurstBrainInstance&, " + runtimeAssembly + " instance, " +
            "BurstCanvas.FsmStateRuntimeNode&, " + runtimeAssembly + " node, " +
            "BurstCanvas.RawExecutionContext&, " + runtimeAssembly + " context) -> " +
            "System.Boolean, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089_" +
            hash +
            " from " +
            runtimeAssembly +
            "@@32";
        var stringReferenceSymbol =
            "BurstCanvas.BurstBrainRuntimeTraversal`2<" +
            "BurstCanvas.TrackedGraphTestActionBlock.Wrapper," +
            "BurstCanvas.TrackedGraphTestConditionBlock.Wrapper>" +
            ".ExecuteFsmEnterHook(" +
            "ref BurstCanvas.NativeBurstBrainProgram program, " +
            "ref BurstCanvas.NativeBurstBrainInstance instance, " +
            "ref BurstCanvas.FsmEnterHookRuntimeNode node, " +
            "ref BurstCanvas.RawExecutionContext context) -> " +
            "BurstCanvas.NodeResultValue_" +
            hash +
            " from " +
            runtimeAssembly +
            ".string.IL_0036";
        var input = string.Join("\n",
            "        # BurstString.cs(797, 1)                while (digPos < 0)",
            "        .globl        burst.initialize.statics.660453e77e7446c547511a17e62a4458",
            "        burst.initialize.statics.660453e77e7446c547511a17e62a4458:",
            "        .ascii        \" /EXPORT:660453e77e7446c547511a17e62a4458\"",
            "        jmp               \"" + jumpSymbol + "\"",
            "        lea               rcx, [rip + \"" + stringReferenceSymbol + "\"+2]");

        var result = view_burst_asm.CleanDisassembly(input);

        Assert.That(result, Is.EqualTo(string.Join("\n",
            "# BurstString.cs:797                while (digPos < 0)",
            ".globl        burst.initialize.statics.660453e7",
            "burst.initialize.statics.660453e7:",
            ".ascii        \" /EXPORT:660453e7\"",
            "jmp               \"BurstBrainRuntimeTraversal<TrackedGraphTestActionBlock+Wrapper,TrackedGraphTestConditionBlock+Wrapper>.AbortFsmState(NativeBurstBrainProgram& program, NativeBurstBrainInstance& instance, FsmStateRuntimeNode& node, RawExecutionContext& context) -> bool\"",
            "lea               rcx, [rip + \"BurstBrainRuntimeTraversal<TrackedGraphTestActionBlock.Wrapper,TrackedGraphTestConditionBlock.Wrapper>.ExecuteFsmEnterHook(ref NativeBurstBrainProgram program, ref NativeBurstBrainInstance instance, ref FsmEnterHookRuntimeNode node, ref RawExecutionContext context) -> NodeResultValue.string.IL_0036\"+2]")));
    }

    [Test]
    public void ViewBurstAsmOutput_SimplifiesHeaderLine()
    {
        var target = new BurstTarget(
            "UnityEngine.Rendering.Universal.ShadowUtility.GenerateInteriorMesh(Unity.Collections.NativeArray`1[UnityEngine.Rendering.Universal.ShadowUtility.ShadowMeshVertex]&, Unity.Collections.NativeArray`1[System.Int32]&, UnityEngine.Rendering.Universal.ShadowEdge&, System.Int32&)",
            "GenerateInteriorMesh",
            "",
            ""
        );

        var output = view_burst_asm.BuildOutput(target, "ret");

        Assert.That(output, Is.EqualTo(
            "**Assembly:** `GenerateInteriorMesh(NativeArray<ShadowMeshVertex>&, NativeArray<int>&, ShadowEdge&, int&)`\n\n" +
            "- Instructions: 1\n" +
            "- Vector instructions: 0 (0%)\n" +
            "- Control flow: branches=0, conditional=0, unconditional=0, calls=0, returns=1\n" +
            "- Memory operands: 0 (0%); stack/frame operands: 0\n" +
            "- Vector width hints: xmm=0, ymm=0, zmm=0, neon/simd=0, sve=0\n" +
            "- Top instructions: ret=1\n\n" +
            "```asm\nret\n```"));
    }

    [Test]
    public void ViewBurstAsmOutput_ReportsMainCodeOptimizationStats()
    {
        var target = new BurstTarget(
            "Gameplay.Motion.MoveJob - (IJob)",
            "Execute",
            "Unity.Jobs.IJobExtensions.JobStruct`1",
            "Gameplay.Motion.MoveJob"
        );
        var disassembly = string.Join("\n",
            ".text",
            "660453e7:",
            "push              rbp",
            "call              \"JobStruct<MoveJob>.Execute(ref MoveJob data) -> void\"",
            "ret",
            "",
            "burst.initialize:",
            "push              rbp",
            "ret",
            "",
            "\"JobStruct<MoveJob>.Execute(ref MoveJob data) -> void\":",
            "push              rbp",
            "mov               rbp, rsp",
            "vmovups           ymm0, ymmword ptr [rcx]",
            "vaddps            ymm0, ymm0, ymmword ptr [rdx]",
            "vaddss            xmm1, xmm1, dword ptr [rsp + 4]",
            "jne               .LBB0_1",
            "call              helper",
            "ret",
            ".section        .debug$S,\"dr\"",
            ".long        4");

        var output = view_burst_asm.BuildOutput(target, disassembly);

        Assert.That(output, Does.Contain("**Assembly:** `MoveJob - (IJob)`\n\n- Instructions: 8\n"));
        Assert.That(output, Does.Contain("\n\n```asm\n.text"));
        Assert.That(output, Does.EndWith("```"));
        Assert.That(output, Does.Contain("- Vector instructions: 2 (25%)"));
        Assert.That(output, Does.Contain("- Control flow: branches=1, conditional=1, unconditional=0, calls=1, returns=1"));
        Assert.That(output, Does.Contain("- Memory operands: 3 (37.5%); stack/frame operands: 1"));
        Assert.That(output, Does.Contain("- Vector width hints: xmm=1, ymm=2, zmm=0, neon/simd=0, sve=0"));
        Assert.That(output, Does.Contain("call=1"));
        Assert.That(output, Does.Contain("vaddps=1"));
        Assert.That(output, Does.Not.Contain("Analyzed:"));
        Assert.That(output, Does.Not.Contain("Top source lines:"));
    }

    [Test]
    public void ViewBurstAsmOutput_ReportsArmStyleOptimizationStats()
    {
        var target = new BurstTarget("Example.Execute()", "Execute", "Example", "Example");
        var disassembly = string.Join("\n",
            "Execute:",
            "ldr               q0, [x0]",
            "fmla              v0.4s, v1.4s, v2.4s",
            "cbz               x0, .Ldone",
            "bl                helper",
            "ret");

        var output = view_burst_asm.BuildOutput(target, disassembly);

        Assert.That(output, Does.Contain("- Instructions: 5"));
        Assert.That(output, Does.Contain("- Vector instructions: 2 (40%)"));
        Assert.That(output, Does.Contain("- Control flow: branches=1, conditional=1, unconditional=0, calls=1, returns=1"));
        Assert.That(output, Does.Contain("- Memory operands: 1 (20%); stack/frame operands: 0"));
        Assert.That(output, Does.Contain("- Vector width hints: xmm=0, ymm=0, zmm=0, neon/simd=2, sve=0"));
    }

    [Test]
    public void ViewBurstAsmOutput_SavesLargeOutputToTempFile()
    {
        var target = new BurstTarget("Example.GenerateInteriorMesh()", "GenerateInteriorMesh", "", "");
        var path = Path.Combine("Temp", "GenerateInteriorMesh.txt");
        try
        {
            var builder = new System.Text.StringBuilder();
            for (var i = 0; i < 1000; i++)
            {
                if (i > 0)
                    builder.Append('\n');

                builder.Append("nop");
            }

            var result = view_burst_asm.CompleteOutput(target, builder.ToString());

            Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
            Assert.That(result.return_value, Does.StartWith("**Assembly:** `GenerateInteriorMesh()`\n\n- Instructions: 1000"));
            Assert.That(result.return_value, Does.Contain("Assembly output very large ("));
            Assert.That(result.return_value, Does.EndWith(" KB); saved to `Temp/GenerateInteriorMesh.txt`.*"));
            Assert.That(File.Exists(path), Is.True);
            Assert.That(File.ReadAllText(path), Does.StartWith("**Assembly:** `GenerateInteriorMesh()`\n\n- Instructions: 1000"));
            Assert.That(File.ReadAllText(path), Does.Contain("\n\n```asm\nnop\nnop"));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
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
        var camera = Camera.main;
        Assert.That(camera, Is.Not.Null);

        Assert.That(output, Does.Contain($"Scene: {SceneAsset}"));
        Assert.That(output, Does.Contain($"GameObject: Main Camera [{ConduitUtility.FormatObjectId(camera!.gameObject)}]"));
        Assert.That(output, Does.Not.Contain("Main Object:"));
        Assert.That(output, Does.Not.Contain("Imported Subassets:"));
    }

    [Test]
    public void Show_SceneHierarchyUsesCompactTreeLegendAndDuplicateComponentIdentifiers()
    {
        var assetPath = GetTempAssetPath("UnitTests", $"RepeatedComponents_{Guid.NewGuid():N}.unity");
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

            var output = show.Show(assetPath);

            Assert.That(
                output,
                Does.Contain("Components:\nBC=BoxCollider\nMC=MeshCollider\n\nHierarchy:")
            );
            Assert.That(
                output,
                Does.Contain(
                    $"ConduitRepeatedComponents [{ConduitUtility.FormatObjectId(gameObject)} | BC | BC | MC ×3]\n" +
                    $"├─ConduitFirstChild [{ConduitUtility.FormatObjectId(firstChild)}]\n" +
                    $"│ └─ConduitGrandChild [{ConduitUtility.FormatObjectId(grandChild)}]\n" +
                    $"└─ConduitSecondChild [{ConduitUtility.FormatObjectId(secondChild)}]"
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

            var output = show.Show(ConduitUtility.FormatObjectId(root));

            Assert.That(output, Does.Contain($"GameObject: ConduitCompactRoot [{ConduitUtility.FormatObjectId(root)}]"));
            Assert.That(output, Does.Contain("Components:\nBC=BoxCollider\nMC=MeshCollider\n\nHierarchy:"));
            Assert.That(
                output,
                Does.Contain(
                    $"ConduitCompactRoot [{ConduitUtility.FormatObjectId(root)} | BC]\n" +
                    $"├─ConduitCompactChild0 [{ConduitUtility.FormatObjectId(firstChild!)} | MC]"
                )
            );
            Assert.That(
                output,
                Does.Not.Contain($"GameObject: ConduitCompactRoot/ConduitCompactChild0 [{ConduitUtility.FormatObjectId(firstChild!)}]")
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
    public void Show_NonSerializableEnumerableThatThrowsDuringEnumerationMarksFieldUnavailable()
    {
        var assetPath = GetTempAssetPath("UnitTests", $"ThrowingEnumerable_{Guid.NewGuid():N}.asset");
        var target = ScriptableObject.CreateInstance<ConduitThrowingEnumerableAsset>();
        try
        {
            AssetDatabase.CreateAsset(target, assetPath);
            var output = show.Show(assetPath);

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
        var assetPath = GetTempAssetPath("UnitTests", $"NativeIndexable_{Guid.NewGuid():N}.asset");
        var target = ScriptableObject.CreateInstance<ConduitNativeIndexableAsset>();
        try
        {
            target.Initialize();
            AssetDatabase.CreateAsset(target, assetPath);
            var output = show.Show(assetPath);

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
        var assetPath = GetTempAssetPath("UnitTests", $"{assetName}.asset");
        var target = ScriptableObject.CreateInstance<ConduitShowFormatAsset>();
        try
        {
            target.name = assetName;
            AssetDatabase.CreateAsset(target, assetPath);

            var output = show.Show(assetPath);

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
            var output = show.Show(ConduitUtility.FormatObjectId(target));

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
    public void ExecuteCode_SnippetFileNamesRequireCanonicalPositiveIds()
    {
        Assert.That(execute_code.TryParseSnippetFileName("17.cs", out var artifactId), Is.True);
        Assert.That(artifactId, Is.EqualTo("17"));

        foreach (var value in new[]
                 {
                     "0.cs",
                     "01.cs",
                     "-1.cs",
                     "17.CS",
                     " 17.cs",
                     "17.cs ",
                     "../17.cs",
                     "snippet.cs",
                 })
            Assert.That(execute_code.TryParseSnippetFileName(value, out _), Is.False, value);
    }

    [Test]
    public void ExecuteCode_ExistingSnippetFilesAdvanceArtifactIds()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), $"ConduitSnippetIds_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        try
        {
            foreach (var fileName in new[] { "2.cs", "11.cs", "03.cs", "snippet.cs", "12.cs.bak" })
                File.WriteAllText(Path.Combine(directoryPath, fileName), string.Empty);

            Assert.That(execute_code.GetHighestSnippetArtifactId(directoryPath), Is.EqualTo(11));
        }
        finally
        {
            Directory.Delete(directoryPath, true);
        }
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
    public void ExecuteCode_NormalizeBareReturns_RewritesOnlyEntryPointDiagnostics()
    {
        var parsedSnippet = ConduitCodeParser.Parse(
            "object Broken() { return; }\n"
            + "if (true)\n"
            + "    return /* first */ ;\n"
            + "if (false) return;\n"
            + "return \"ok\";\n"
        );
        var objectResultMessages = new[]
        {
            Error(1, 19, "CS0126", "An object of a type convertible to 'object' is required"),
            Error(3, 5, "CS0126", "An object of a type convertible to 'object' is required"),
            Error(4, 12, "CS0126", "An object of a type convertible to 'object' is required"),
        };
        var noResultMessages = new[]
        {
            Error(1, 19, "CS0126", "An object of a type convertible to 'object' is required"),
            Error(5, 1, "CS1997", "A return keyword must not be followed by an object expression"),
        };

        var normalized = execute_code.TryNormalizeBareReturns(
            parsedSnippet.Body,
            objectResultMessages,
            noResultMessages,
            out var normalizedBody,
            out var recoveredLocations
        );

        Assert.That(normalized, Is.True);
        Assert.That(recoveredLocations, Has.Count.EqualTo(2));
        Assert.That(recoveredLocations[(3, 5)], Is.EqualTo(1));
        Assert.That(recoveredLocations[(4, 12)], Is.EqualTo(1));
        Assert.That(normalizedBody.Text, Does.Contain("object Broken() { return; }"));
        Assert.That(normalizedBody.Text, Does.Contain("return null /* first */ ;"));
        Assert.That(normalizedBody.Text, Does.Contain("if (false) return null;"));

        static CompilerMessage Error(int line, int column, string code, string text) => new()
        {
            type = CompilerMessageType.Error,
            file = "Temp/execute_code/1.cs",
            line = line,
            column = column,
            message = $"Temp/execute_code/1.cs({line},{column}): error {code}: {text}",
        };
    }

    [Test]
    public void ExecuteCode_NormalizeBareReturns_FailsClosedForInvalidCompilerLocation()
    {
        var parsedSnippet = ConduitCodeParser.Parse("Debug.Log(\"return;\");");
        var objectResultMessages = new[]
        {
            new CompilerMessage
            {
                type = CompilerMessageType.Error,
                line = 1,
                column = 1,
                message = "Temp/execute_code/1.cs(1,1): error CS0126: An object of a type convertible to 'object' is required",
            },
        };

        var normalized = execute_code.TryNormalizeBareReturns(
            parsedSnippet.Body,
            objectResultMessages,
            Array.Empty<CompilerMessage>(),
            out var normalizedBody,
            out var recoveredLocations
        );

        Assert.That(normalized, Is.False);
        Assert.That(normalizedBody.Text, Is.EqualTo(parsedSnippet.Body.Text));
        Assert.That(recoveredLocations, Is.Empty);
    }

    [Test]
    public void ExecuteCode_ParseRetryableMissingSymbol_RecognizesSupportedDiagnostics()
    {
        var missingName = new CompilerMessage
        {
            type = CompilerMessageType.Error,
            message = "Temp/execute_code/1.cs(9,36): error CS0103: The name 'BindingFlags' does not exist in the current context",
        };
        var missingType = new CompilerMessage
        {
            type = CompilerMessageType.Error,
            message = "Temp/execute_code/1.cs(2,1): error CS0246: The type or namespace name 'MethodInfo' could not be found (are you missing a using directive or an assembly reference?)",
        };

        Assert.That(execute_code.TryParseRetryableMissingSymbol(missingName, out var missingNameSymbol), Is.True);
        Assert.That(missingNameSymbol, Is.EqualTo("BindingFlags"));
        Assert.That(execute_code.TryParseRetryableMissingSymbol(missingType, out var missingTypeSymbol), Is.True);
        Assert.That(missingTypeSymbol, Is.EqualTo("MethodInfo"));
    }

    [Test]
    public void ExecuteCode_ParseRetryableMissingSymbol_RejectsUnsupportedOrNonTypeLikeDiagnostics()
    {
        var missingVariable = new CompilerMessage
        {
            type = CompilerMessageType.Error,
            message = "Temp/execute_code/1.cs(9,36): error CS0103: The name 'bindingFlags' does not exist in the current context",
        };
        var lowercaseType = new CompilerMessage
        {
            type = CompilerMessageType.Error,
            message = "Temp/execute_code/1.cs(1,8): error CS0103: The name 'math' does not exist in the current context",
        };
        var unsupported = new CompilerMessage
        {
            type = CompilerMessageType.Error,
            message = "Temp/execute_code/1.cs(9,36): error CS1061: 'string' does not contain a definition for 'Foo'",
        };

        Assert.That(execute_code.TryParseRetryableMissingSymbol(missingVariable, out _), Is.False);
        Assert.That(execute_code.TryParseRetryableMissingSymbol(lowercaseType, out _), Is.False);
        Assert.That(execute_code.TryParseRetryableMissingSymbol(unsupported, out _), Is.False);
    }

    [Test]
    public void ExecuteCode_ParseRetryableMissingSymbol_RejectsQualifiedNames()
    {
        var qualifiedType = new CompilerMessage
        {
            type = CompilerMessageType.Error,
            message = "Temp/execute_code/1.cs(2,1): error CS0246: The type or namespace name 'System.Reflection.MethodInfo' could not be found (are you missing a using directive or an assembly reference?)",
        };
        var aliasedType = new CompilerMessage
        {
            type = CompilerMessageType.Error,
            message = "Temp/execute_code/1.cs(2,1): error CS0246: The type or namespace name 'global::MethodInfo' could not be found (are you missing a using directive or an assembly reference?)",
        };

        Assert.That(execute_code.TryParseRetryableMissingSymbol(qualifiedType, out _), Is.False);
        Assert.That(execute_code.TryParseRetryableMissingSymbol(aliasedType, out _), Is.False);
    }

    [Test]
    public void ExecuteCode_InferMissingNamespaces_ResolvesUnambiguousReflectionImport()
    {
        var parsedSnippet = ConduitCodeParser.Parse("return BindingFlags.Public.ToString();");
        var compilerMessages = new[]
        {
            new CompilerMessage
            {
                type = CompilerMessageType.Error,
                message = "Temp/execute_code/1.cs(1,8): error CS0103: The name 'BindingFlags' does not exist in the current context",
            },
        };

        var inferred = execute_code.TryInferMissingNamespaces(
            execute_code.GetCurrentProjectPath(),
            execute_code.GetSnippetRootPath(execute_code.GetCurrentProjectPath()),
            parsedSnippet,
            compilerMessages,
            out var inferredNamespaces
        );

        Assert.That(inferred, Is.True);
        Assert.That(inferredNamespaces, Is.EqualTo(new[] { "System.Reflection" }));
    }

    [Test]
    public void ExecuteCode_InferMissingNamespaces_ResolvesAllLowercaseMathematicsTypes()
    {
        var projectPath = execute_code.GetCurrentProjectPath();
        var lowercaseTypeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        foreach (var type in GetLoadableTypes(assembly))
        {
            var aritySeparator = type.Name.IndexOf('`');
            var typeName = aritySeparator >= 0 ? type.Name[..aritySeparator] : type.Name;
            if (!type.IsNested
                && type.Namespace == "Unity.Mathematics"
                && IsLowercaseTypeName(typeName))
                lowercaseTypeNames.Add(typeName);
        }

        Assert.That(lowercaseTypeNames, Does.Contain("math"));
        foreach (var typeName in lowercaseTypeNames)
        {
            var parsedSnippet = ConduitCodeParser.Parse($"return typeof({typeName}).Name;");
            var compilerMessages = new[]
            {
                new CompilerMessage
                {
                    type = CompilerMessageType.Error,
                    message = $"Temp/execute_code/1.cs(1,15): error CS0246: The type or namespace name '{typeName}' could not be found (are you missing a using directive or an assembly reference?)",
                },
            };

            var inferred = execute_code.TryInferMissingNamespaces(
                projectPath,
                execute_code.GetSnippetRootPath(projectPath),
                parsedSnippet,
                compilerMessages,
                out var inferredNamespaces
            );

            Assert.That(inferred, Is.True, typeName);
            Assert.That(inferredNamespaces, Is.EqualTo(new[] { "Unity.Mathematics" }), typeName);
        }

        static bool IsLowercaseTypeName(string typeName)
        {
            if (typeName.Length == 0 || !char.IsLower(typeName[0]))
                return false;

            foreach (var ch in typeName)
                if (char.IsUpper(ch))
                    return false;

            return true;
        }

        static IEnumerable<Type> GetLoadableTypes(System.Reflection.Assembly assembly)
        {
            Type?[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types;
            }

            foreach (var type in types)
                if (type is not null)
                    yield return type;
        }
    }

    [Test]
    public void ExecuteCode_InferMissingNamespaces_RejectsMissingVariables()
    {
        var parsedSnippet = ConduitCodeParser.Parse("return bindingFlags.ToString();");
        var compilerMessages = new[]
        {
            new CompilerMessage
            {
                type = CompilerMessageType.Error,
                message = "Temp/execute_code/1.cs(1,8): error CS0103: The name 'bindingFlags' does not exist in the current context",
            },
        };

        var inferred = execute_code.TryInferMissingNamespaces(
            execute_code.GetCurrentProjectPath(),
            execute_code.GetSnippetRootPath(execute_code.GetCurrentProjectPath()),
            parsedSnippet,
            compilerMessages,
            out _
        );

        Assert.That(inferred, Is.False);
    }

    [Test]
    public void ExecuteCode_InferMissingNamespaces_ResolvesMultipleNamespacesInSingleRetry()
    {
        var projectPath = execute_code.GetCurrentProjectPath();
        var parsedSnippet = ConduitCodeParser.Parse("return Regex.IsMatch(typeof(MethodInfo).Name, \"^Method\").ToString();");
        var compilerMessages = new[]
        {
            new CompilerMessage
            {
                type = CompilerMessageType.Error,
                message = "Temp/execute_code/1.cs(1,24): error CS0103: The name 'Regex' does not exist in the current context",
            },
            new CompilerMessage
            {
                type = CompilerMessageType.Error,
                message = "Temp/execute_code/1.cs(1,39): error CS0246: The type or namespace name 'MethodInfo' could not be found (are you missing a using directive or an assembly reference?)",
            },
        };

        var inferred = execute_code.TryInferMissingNamespaces(
            projectPath,
            execute_code.GetSnippetRootPath(projectPath),
            parsedSnippet,
            compilerMessages,
            out var inferredNamespaces
        );

        Assert.That(inferred, Is.True);
        Assert.That(inferredNamespaces, Is.EqualTo(new[] { "System.Reflection", "System.Text.RegularExpressions" }));
    }

    [Test]
    public void ExecuteCode_InferMissingNamespaces_DeduplicatesRepeatedMissingSymbols()
    {
        var projectPath = execute_code.GetCurrentProjectPath();
        var parsedSnippet = ConduitCodeParser.Parse("var a = BindingFlags.Public; return BindingFlags.NonPublic.ToString();");
        var compilerMessages = new[]
        {
            new CompilerMessage
            {
                type = CompilerMessageType.Error,
                message = "Temp/execute_code/1.cs(1,9): error CS0103: The name 'BindingFlags' does not exist in the current context",
            },
            new CompilerMessage
            {
                type = CompilerMessageType.Error,
                message = "Temp/execute_code/1.cs(1,38): error CS0103: The name 'BindingFlags' does not exist in the current context",
            },
        };

        var inferred = execute_code.TryInferMissingNamespaces(
            projectPath,
            execute_code.GetSnippetRootPath(projectPath),
            parsedSnippet,
            compilerMessages,
            out var inferredNamespaces
        );

        Assert.That(inferred, Is.True);
        Assert.That(inferredNamespaces, Is.EqualTo(new[] { "System.Reflection" }));
    }

    [Test]
    public void ExecuteCode_InferMissingNamespaces_DoesNotRetryWhenNamespaceAlreadyImported()
    {
        var parsedSnippet = ConduitCodeParser.Parse(
            "using System.Reflection;\n"
            + "\n"
            + "return BindingFlags.Public.ToString();\n"
        );
        var compilerMessages = new[]
        {
            new CompilerMessage
            {
                type = CompilerMessageType.Error,
                message = "Temp/execute_code/1.cs(3,8): error CS0103: The name 'BindingFlags' does not exist in the current context",
            },
        };

        var inferred = execute_code.TryInferMissingNamespaces(
            execute_code.GetCurrentProjectPath(),
            execute_code.GetSnippetRootPath(execute_code.GetCurrentProjectPath()),
            parsedSnippet,
            compilerMessages,
            out _
        );

        Assert.That(inferred, Is.False);
    }

    [Test]
    public void ExecuteCode_BuildSnippetSource_DeduplicatesInferredAndExplicitUsingDirectives()
    {
        var parsedSnippet = ConduitCodeParser.Parse(
            "using System.Reflection;\n"
            + "using System.Reflection;\n"
            + "\n"
            + "return BindingFlags.Public.ToString();\n"
        );
        var buildSnippetSource = typeof(execute_code).GetMethod(
            "BuildSnippetSource",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        Assert.That(buildSnippetSource, Is.Not.Null);

        var generatedSource = (string)buildSnippetSource!.Invoke(
            null,
            new object?[]
            {
                "SnippetHost_Test",
                "1.cs",
                parsedSnippet,
                new[] { "System.Reflection" },
            }
        )!;

        Assert.That(CountOccurrences(generatedSource, "using System.Reflection;"), Is.EqualTo(1), generatedSource);
    }

    [Test]
    public void ExecuteCode_BuildSnippetSource_ImportsConduitHelpers()
    {
        var parsedSnippet = ConduitCodeParser.Parse("return Reflect.Type(\"UnityEngine.Camera\").Name + Search<Material>(\"JsonOverwriteMaterial\").name;");
        var buildSnippetSource = typeof(execute_code).GetMethod(
            "BuildSnippetSource",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        Assert.That(buildSnippetSource, Is.Not.Null);

        var generatedSource = (string)buildSnippetSource!.Invoke(
            null,
            new object?[]
            {
                "SnippetHost_Test",
                "1.cs",
                parsedSnippet,
                Array.Empty<string>(),
            }
        )!;

        Assert.That(CountOccurrences(generatedSource, "using static Conduit.ConduitSearch;"), Is.EqualTo(1), generatedSource);
        Assert.That(CountOccurrences(generatedSource, "using Reflect = Conduit.ConduitReflect;"), Is.EqualTo(1), generatedSource);
        Assert.That(CountOccurrences(generatedSource, "using Object = UnityEngine.Object;"), Is.EqualTo(1), generatedSource);
        Assert.That(generatedSource, Does.Contain("public static object Execute()"), generatedSource);
        Assert.That(generatedSource, Does.Not.Contain("async Task<object> Execute()"), generatedSource);
    }

    [Test]
    public void ExecuteCode_AsyncContextRetry_RequiresRemovedDiagnostic()
    {
        var wrapperAwait = CompilerError("CS4032", 1, 1);
        var nestedAwait = CompilerError("CS4033", 2, 5);

        Assert.That(
            execute_code.RemovesAsyncContextError(
                new[] { wrapperAwait, nestedAwait },
                new[] { nestedAwait }
            ),
            Is.True
        );
        Assert.That(
            execute_code.RemovesAsyncContextError(
                new[] { nestedAwait },
                new[] { nestedAwait }
            ),
            Is.False
        );

        static CompilerMessage CompilerError(string code, int line, int column)
            => new()
            {
                type = CompilerMessageType.Error,
                line = line,
                column = column,
                message = $"Temp/execute_code/1.cs({line},{column}): error {code}: async context required",
            };
    }

    [Test]
    public void ExecuteCode_InferMissingNamespaces_RejectsMixedErrorSets()
    {
        var parsedSnippet = ConduitCodeParser.Parse("return BindingFlags.Public.ToString();");
        var compilerMessages = new[]
        {
            new CompilerMessage
            {
                type = CompilerMessageType.Error,
                message = "Temp/execute_code/1.cs(1,8): error CS0103: The name 'BindingFlags' does not exist in the current context",
            },
            new CompilerMessage
            {
                type = CompilerMessageType.Error,
                message = "Temp/execute_code/1.cs(1,1): error CS1002: ; expected",
            },
        };

        var inferred = execute_code.TryInferMissingNamespaces(
            execute_code.GetCurrentProjectPath(),
            execute_code.GetSnippetRootPath(execute_code.GetCurrentProjectPath()),
            parsedSnippet,
            compilerMessages,
            out _
        );

        Assert.That(inferred, Is.False);
    }

    [Test]
    public void ExecuteCode_ResolveMissingSymbolNamespace_RejectsAmbiguousCandidates()
    {
        var lookup = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Thing"] = new[] { "A.B", "C.D" },
        };

        var resolved = execute_code.TryResolveMissingSymbolNamespace(
            "Thing",
            lookup,
            new(StringComparer.Ordinal),
            new(StringComparer.Ordinal),
            out _
        );

        Assert.That(resolved, Is.False);
    }

    [Test]
    public void ExecuteCode_ResolveMissingSymbolNamespace_ReusesPreviouslyResolvedNamespace()
    {
        var lookup = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Thing"] = new[] { "A.B", "C.D" },
        };
        var resolvedNamespaces = new HashSet<string>(StringComparer.Ordinal)
        {
            "A.B",
        };

        var resolved = execute_code.TryResolveMissingSymbolNamespace(
            "Thing",
            lookup,
            new(StringComparer.Ordinal),
            resolvedNamespaces,
            out var resolvedNamespace
        );

        Assert.That(resolved, Is.True);
        Assert.That(resolvedNamespace, Is.EqualTo("A.B"));
    }

    static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var startIndex = 0;
        while ((startIndex = text.IndexOf(value, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += value.Length;
        }

        return count;
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

        Assert.That(result, Does.StartWith("- "));
        Assert.That(result, Does.Contain(" | EditMode"));
    }

    [Test]
    public void Search_TestQuery_PlayModeFilterWithoutProjectMatchesUsesNoMatchText()
    {
        var result = ConduitSearchUtility.Search("t:test playmode");

        Assert.That(result, Is.EqualTo("No matches for 't:test playmode'."));
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
        var result = show.Show("t:prefab ConduitMissingAlpha OR t:prefab ConduitMissingBeta");

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
    public void ClientWorkSnapshot_TracksOutstandingActiveAndQueuedOperations()
    {
        var activeOperation = new PendingOperationState
        {
            command_type = BridgeCommandTypes.ExecuteCode,
            client_id = 17,
        };
        var queuedOperation = new PendingOperationState
        {
            command_type = BridgeCommandTypes.Show,
            client_id = 23,
        };

        var snapshot = ClientWorkSnapshot.Create(
            activeOperation,
            new() { queuedOperation },
            hasPendingResult: false
        );

        Assert.That(snapshot.ActiveCommandType, Is.EqualTo(BridgeCommandTypes.ExecuteCode));
        Assert.That(snapshot.HasOutstandingClientWork(17), Is.True);
        Assert.That(snapshot.HasOutstandingClientWork(23), Is.True);
        Assert.That(snapshot.HasOutstandingClientWork(0), Is.False);
        Assert.That(snapshot.HasOutstandingClientWork(99), Is.False);
    }

    [Test]
    public void ClientWorkSnapshot_TracksReconnectableDisconnectedOperationsAndPendingResults()
    {
        var disconnectedActiveOperation = new PendingOperationState
        {
            command_type = BridgeCommandTypes.RefreshAssetDatabase,
            client_id = 0,
        };
        var disconnectedQueuedOperation = new PendingOperationState
        {
            command_type = BridgeCommandTypes.Show,
            client_id = 0,
        };

        Assert.That(
            ClientWorkSnapshot.Create(disconnectedActiveOperation, new(), false).HasReconnectableWorkForAnyClient(),
            Is.True
        );
        Assert.That(
            ClientWorkSnapshot.Create(null, new() { disconnectedQueuedOperation }, false).HasReconnectableWorkForAnyClient(),
            Is.True
        );
        Assert.That(
            ClientWorkSnapshot.Create(null, new(), true).HasReconnectableWorkForAnyClient(),
            Is.True
        );
        Assert.That(
            ClientWorkSnapshot.Create(null, new(), false).HasReconnectableWorkForAnyClient(),
            Is.False
        );
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
    public void BridgeCommandJson_DeserializesAsyncFlag()
    {
        var command = JsonUtility.FromJson<BridgeCommand>(
            "{\"command_type\":\"run_tests_editmode\",\"async\":true}"
        );

        Assert.That(command.@async, Is.True);
    }

    [Test]
    public void TestRunCompletionGuard_WaitsForRunnerAndEditorLifecycle()
    {
        Assert.That(ConduitToolRunner.ShouldWaitForTestRunCompletion(true, false, false, false, false), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForTestRunCompletion(false, true, false, false, false), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForTestRunCompletion(false, false, true, false, false), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForTestRunCompletion(false, false, false, true, false), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForTestRunCompletion(false, false, false, false, true), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForTestRunCompletion(false, false, false, false, false), Is.False);
    }

    [Test]
    public void TestRunCompletionGuard_CancelledResultCanBypassStuckRunnerWhenEditorIsIdle()
    {
        Assert.That(ConduitToolRunner.ShouldWaitForTestRunCompletion(true, false, false, false, false, true), Is.False);
        Assert.That(ConduitToolRunner.ShouldWaitForTestRunCompletion(true, true, false, false, false, true), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForTestRunCompletion(true, false, true, false, false, true), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForTestRunCompletion(true, false, false, true, false, true), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForTestRunCompletion(true, false, false, false, true, true), Is.True);
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
    public void PlayCompletionDiagnostic_ReportsAlreadyInTargetMode()
    {
        Assert.That(ConduitToolRunner.BuildPlayCompletionDiagnostic(true, false, false), Is.EqualTo("Already in play mode. Paused: no."));
        Assert.That(ConduitToolRunner.BuildPlayCompletionDiagnostic(false, false, false), Is.EqualTo("Already in edit mode."));
    }

    [Test]
    public void UnfocusedGameView_CoverTabSelectionPrefersTestRunnerThenExistingTabs()
    {
        Assert.That(ConduitGameViewFocus.GetCoverTabIndex(1, 2, 0, 3), Is.EqualTo(2));
        Assert.That(ConduitGameViewFocus.GetCoverTabIndex(1, -1, 2, 3), Is.EqualTo(2));
        Assert.That(ConduitGameViewFocus.GetCoverTabIndex(1, -1, -1, 3), Is.EqualTo(0));
        Assert.That(ConduitGameViewFocus.GetCoverTabIndex(0, -1, 0, 1), Is.EqualTo(-1));
    }

    [Test]
    public void GameViewPreparation_SkipsFocusButAppliesResolutionForMaximizedNonGameView()
    {
        RequireInteractiveEditorWindows();

        var sceneView = SceneView.lastActiveSceneView ?? EditorWindow.GetWindow<SceneView>();
        EditorWindow? previouslyMaximizedWindow = null;
        foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            if (window.maximized)
            {
                previouslyMaximizedWindow = window;
                break;
            }

        try
        {
            ConduitGameViewFocus.Restore();
            ConduitGameViewResolution.Restore();
            sceneView.maximized = true;

            Assert.That(ConduitGameView.IsOtherWindowMaximized(), Is.True);

            ConduitGameViewFocus.Prepare(true);
            ConduitGameViewResolution.Prepare(true);

            Assert.That(ConduitGameViewFocus.IsPrepared, Is.False);
            Assert.That(ConduitGameViewResolution.IsPrepared, Is.True);
        }
        finally
        {
            ConduitGameViewFocus.Restore();
            ConduitGameViewResolution.Restore();
            sceneView.maximized = false;
            if (previouslyMaximizedWindow is { } window)
                window.maximized = true;
        }
    }

    [Test]
    public void UnfocusedGameView_PrepareAndRestorePreservesPlayBehavior()
    {
        RequireInteractiveEditorWindows();

        var editorAssembly = typeof(EditorWindow).Assembly;
        var gameViewType = editorAssembly.GetType("UnityEditor.GameView")
                           ?? throw new TypeLoadException("UnityEditor.GameView");
        var playModeViewType = editorAssembly.GetType("UnityEditor.PlayModeView")
                               ?? throw new TypeLoadException("UnityEditor.PlayModeView");
        var testRunnerWindowType = typeof(UnityEditor.TestTools.TestRunner.Api.TestRunnerApi).Assembly.GetType(
            "UnityEditor.TestTools.TestRunner.TestRunnerWindow"
        ) ?? throw new TypeLoadException("UnityEditor.TestTools.TestRunner.TestRunnerWindow");
        var behaviorProperty = playModeViewType.GetProperty("enterPlayModeBehavior")
                               ?? throw new MissingMemberException("UnityEditor.PlayModeView.enterPlayModeBehavior");
        var existingGameViews = GetWindowIds(gameViewType);
        var existingTestRunners = GetWindowIds(testRunnerWindowType);
        var previouslyFocusedWindow = EditorWindow.focusedWindow;
        var gameView = FindOrOpenGameView();
        var originalBehavior = behaviorProperty.GetValue(gameView);
        var playFocused = Enum.Parse(behaviorProperty.PropertyType, "PlayFocused");

        try
        {
            ConduitGameViewFocus.Restore();
            behaviorProperty.SetValue(gameView, playFocused);

            ConduitGameViewFocus.Prepare(true);

            Assert.That(ConduitGameViewFocus.IsPrepared, Is.True);
            Assert.That(behaviorProperty.GetValue(gameView)?.ToString(), Is.EqualTo("PlayUnfocused"));

            ConduitGameViewFocus.Restore();

            Assert.That(ConduitGameViewFocus.IsPrepared, Is.False);
            Assert.That(behaviorProperty.GetValue(gameView)?.ToString(), Is.EqualTo("PlayFocused"));
        }
        finally
        {
            ConduitGameViewFocus.Restore();
            behaviorProperty.SetValue(gameView, originalBehavior);
            CloseNewWindows(testRunnerWindowType, existingTestRunners);
            CloseNewWindows(gameViewType, existingGameViews);
            previouslyFocusedWindow?.Focus();
        }

        HashSet<ulong> GetWindowIds(Type windowType)
        {
            var ids = new HashSet<ulong>();
            foreach (var candidate in Resources.FindObjectsOfTypeAll(windowType))
                if (candidate is EditorWindow window)
                    ids.Add(ConduitUtility.GetObjectId(window));

            return ids;
        }

        EditorWindow FindOrOpenGameView()
        {
            foreach (var candidate in Resources.FindObjectsOfTypeAll(gameViewType))
                if (candidate is EditorWindow window)
                    return window;

            return EditorWindow.GetWindow(gameViewType, false, "Game", false);
        }

        void CloseNewWindows(Type windowType, HashSet<ulong> existingWindowIds)
        {
            foreach (var candidate in Resources.FindObjectsOfTypeAll(windowType))
                if (candidate is EditorWindow window
                    && !existingWindowIds.Contains(ConduitUtility.GetObjectId(window)))
                    window.Close();
        }
    }

    [Test]
    public void LowResolutionGameView_PrepareAndRestorePreservesSelectedSize()
    {
        RequireInteractiveEditorWindows();

        var editorAssembly = typeof(EditorWindow).Assembly;
        var gameViewType = editorAssembly.GetType("UnityEditor.GameView")
                           ?? throw new TypeLoadException("UnityEditor.GameView");
        var gameViewSizesType = editorAssembly.GetType("UnityEditor.GameViewSizes")
                                ?? throw new TypeLoadException("UnityEditor.GameViewSizes");
        var selectedSizeIndexProperty = gameViewType.GetProperty("selectedSizeIndex")
                                        ?? throw new MissingMemberException(
                                            "UnityEditor.GameView.selectedSizeIndex"
                                        );
        var sizes = gameViewSizesType.GetProperty(
                        "instance",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy
                    )?.GetValue(null)
                    ?? throw new MissingMemberException("UnityEditor.GameViewSizes.instance");
        var group = gameViewSizesType.GetProperty("currentGroup")?.GetValue(sizes)
                    ?? throw new MissingMemberException("UnityEditor.GameViewSizes.currentGroup");
        var getTotalCount = group.GetType().GetMethod("GetTotalCount")
                            ?? throw new MissingMemberException(
                                "UnityEditor.GameViewSizeGroup.GetTotalCount"
                            );
        var getGameViewSize = group.GetType().GetMethod("GetGameViewSize")
                              ?? throw new MissingMemberException(
                                  "UnityEditor.GameViewSizeGroup.GetGameViewSize"
                              );
        var existingGameViews = new HashSet<ulong>();
        foreach (var candidate in Resources.FindObjectsOfTypeAll(gameViewType))
            if (candidate is EditorWindow window)
                existingGameViews.Add(ConduitUtility.GetObjectId(window));
        var previouslyFocusedWindow = EditorWindow.focusedWindow;
        var gameView = ConduitGameView.FindOrOpen();
        int originalSizeIndex = Convert.ToInt32(selectedSizeIndexProperty.GetValue(gameView));
        int originalTargetSizeIndex = FindTargetSizeIndex();
        int previousSizeIndex = originalTargetSizeIndex == 0 ? 1 : 0;

        try
        {
            ConduitGameViewResolution.Restore();
            selectedSizeIndexProperty.SetValue(gameView, previousSizeIndex);

            ConduitGameViewResolution.Prepare(true);

            int targetSizeIndex = FindTargetSizeIndex();
            Assert.That(ConduitGameViewResolution.IsPrepared, Is.True);
            Assert.That(targetSizeIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(
                Convert.ToInt32(selectedSizeIndexProperty.GetValue(gameView)),
                Is.EqualTo(targetSizeIndex)
            );

            ConduitGameViewResolution.Restore();

            Assert.That(ConduitGameViewResolution.IsPrepared, Is.False);
            Assert.That(
                Convert.ToInt32(selectedSizeIndexProperty.GetValue(gameView)),
                Is.EqualTo(previousSizeIndex)
            );
        }
        finally
        {
            ConduitGameViewResolution.Restore();
            selectedSizeIndexProperty.SetValue(gameView, originalSizeIndex);
            if (originalTargetSizeIndex < 0)
                RemoveTargetSize();

            foreach (var candidate in Resources.FindObjectsOfTypeAll(gameViewType))
                if (candidate is EditorWindow window
                    && !existingGameViews.Contains(ConduitUtility.GetObjectId(window)))
                    window.Close();

            previouslyFocusedWindow?.Focus();
        }

        int FindTargetSizeIndex()
        {
            int sizeCount = Convert.ToInt32(getTotalCount.Invoke(group, null));
            for (int index = 0; index < sizeCount; ++index)
            {
                var size = getGameViewSize.Invoke(group, new object[] { index })
                           ?? throw new InvalidOperationException("A Game View size was unavailable.");
                var sizeType = size.GetType();
                string? typeName = sizeType.GetProperty("sizeType")?.GetValue(size)?.ToString();
                int width = Convert.ToInt32(sizeType.GetProperty("width")?.GetValue(size));
                int height = Convert.ToInt32(sizeType.GetProperty("height")?.GetValue(size));
                if (typeName == "FixedResolution"
                    && width == ConduitGameViewResolution.TargetWidth
                    && height == ConduitGameViewResolution.TargetHeight)
                    return index;
            }

            return -1;
        }

        void RemoveTargetSize()
        {
            int targetSizeIndex = FindTargetSizeIndex();
            if (targetSizeIndex < 0)
                return;

            var totalIndexToCustomIndex = group.GetType().GetMethod("TotalIndexToCustomIndex")
                                          ?? throw new MissingMemberException(
                                              "UnityEditor.GameViewSizeGroup.TotalIndexToCustomIndex"
                                          );
            var removeCustomSize = group.GetType().GetMethod("RemoveCustomSize")
                                   ?? throw new MissingMemberException(
                                       "UnityEditor.GameViewSizeGroup.RemoveCustomSize"
                                   );
            int customSizeIndex = Convert.ToInt32(
                totalIndexToCustomIndex.Invoke(group, new object[] { targetSizeIndex })
            );
            removeCustomSize.Invoke(
                group,
                new object[] { customSizeIndex }
            );
            gameViewSizesType.GetMethod("Changed")?.Invoke(sizes, null);
            gameViewSizesType.GetMethod("SaveToHDD")?.Invoke(sizes, null);
        }
    }

    [Test]
    public void RefreshAssetDatabasePlayModeGuard_BlocksPlayMode()
    {
        Assert.That(ConduitToolRunner.ShouldBlockReimportForPlayMode(true), Is.True);
        Assert.That(ConduitToolRunner.ShouldBlockReimportForPlayMode(false), Is.False);
        Assert.That(ConduitToolRunner.BuildReimportPlayModeDiagnostic(), Is.EqualTo(
            "Cannot run 'refresh_asset_database' while Unity is in play mode. Use 'editmode' to return to edit mode first."));
        Assert.That(ConduitToolRunner.BuildReimportPlayModeDiagnostic(BridgeCommandTypes.ReimportAssets), Is.EqualTo(
            "Cannot run 'reimport_assets' while Unity is in play mode. Use 'editmode' to return to edit mode first."));
    }

    [Test]
    public void PlayModePersistedOperation_RestoresCommand()
    {
        var pendingOperation = new PendingOperationState
        {
            request_id = "play-restore-test",
            command_type = BridgeCommandTypes.PlayMode,
            tool_usage_started_utc_ticks = 123456789L,
        };

        try
        {
            OperationPersistence.ClearActiveOperation();
            OperationPersistence.SaveActiveOperation(pendingOperation, BridgeCommandKind.PlayMode);

            var restoredOperation = OperationPersistence.RestoreActiveOperation();
            Assert.That(restoredOperation, Is.Not.Null);
            Assert.That(restoredOperation!.command_type, Is.EqualTo(BridgeCommandTypes.PlayMode));
            Assert.That(restoredOperation.is_restored, Is.EqualTo(true));
            Assert.That(restoredOperation.tool_usage_started_utc_ticks, Is.EqualTo(123456789L));
        }
        finally
        {
            OperationPersistence.ClearActiveOperation();
        }
    }

    [Test]
    public void PendingResultPersistence_RestoresCompletedResult()
    {
        var pendingResult = new PersistedPendingResultState
        {
            RequestID = "refresh-restore-test",
            CommandType = BridgeCommandTypes.RefreshAssetDatabase,
            Result = new() { outcome = ToolOutcome.Success },
        };

        try
        {
            OperationPersistence.ClearPendingResult();
            OperationPersistence.SavePendingResult(pendingResult);

            var restoredResult = OperationPersistence.RestorePendingResult();
            Assert.That(restoredResult, Is.Not.Null);
            Assert.That(restoredResult!.RequestID, Is.EqualTo("refresh-restore-test"));
            Assert.That(restoredResult.CommandType, Is.EqualTo(BridgeCommandTypes.RefreshAssetDatabase));
            Assert.That(restoredResult.Result.outcome, Is.EqualTo(ToolOutcome.Success));
        }
        finally
        {
            OperationPersistence.ClearPendingResult();
        }
    }

    [Test]
    public void PendingTestCompletionPersistence_RestoresRunFinishedResult()
    {
        var pendingResult = new PersistedPendingResultState
        {
            RequestID = "test-run-restore",
            CommandType = BridgeCommandTypes.RunTestsEditMode,
            Result = new()
            {
                outcome = ToolOutcome.Success,
                diagnostic = "Passed 1 test.",
            },
        };

        try
        {
            OperationPersistence.ClearPendingTestCompletion();
            OperationPersistence.SavePendingTestCompletion(pendingResult);

            var restoredResult = OperationPersistence.RestorePendingTestCompletion();
            Assert.That(restoredResult, Is.Not.Null);
            Assert.That(restoredResult!.RequestID, Is.EqualTo("test-run-restore"));
            Assert.That(restoredResult.CommandType, Is.EqualTo(BridgeCommandTypes.RunTestsEditMode));
            Assert.That(restoredResult.Result.outcome, Is.EqualTo(ToolOutcome.Success));
            Assert.That(restoredResult.Result.diagnostic, Is.EqualTo("Passed 1 test."));
        }
        finally
        {
            OperationPersistence.ClearPendingTestCompletion();
        }
    }

    [TestCase(BridgeCommandTypes.PlayMode, true)]
    [TestCase(BridgeCommandTypes.RefreshAssetDatabase, true)]
    [TestCase(BridgeCommandTypes.RunTestsEditMode, true)]
    [TestCase(BridgeCommandTypes.ExecuteCode, false)]
    [TestCase(BridgeCommandTypes.Show, false)]
    public void AssemblyReloadRecovery_OnlyRestoresExplicitEditorWork(
        string commandType,
        bool expected
    )
        => Assert.That(
            OperationPersistence.CanRestore(ConduitToolRunner.ParseIncomingCommand(commandType)),
            Is.EqualTo(expected)
        );

    [Test]
    public void AssemblyReloadInterruptionDiagnostic_WarnsAboutUnknownSideEffects()
    {
        var diagnostic = ConduitToolRunner.BuildAssemblyReloadInterruptionDiagnostic(
            BridgeCommandTypes.ExecuteCode
        );

        Assert.That(diagnostic, Does.Contain(BridgeCommandTypes.ExecuteCode));
        Assert.That(diagnostic, Does.Contain("domain reload"));
        Assert.That(diagnostic, Does.Contain("side effects may have occurred"));
        Assert.That(diagnostic, Does.Contain("not re-executed"));
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
    public void CancelledTestResultState_DetectsUnityCancelledLabels()
    {
        Assert.That(run_tests.IsCancelledResultState("Failed:Cancelled"), Is.True);
        Assert.That(run_tests.IsCancelledResultState("Cancelled"), Is.True);
        Assert.That(run_tests.IsCancelledResultState("Failed:Error"), Is.False);
        Assert.That(run_tests.IsCancelledResultState(null), Is.False);
    }

    [Test]
    public void RequestCancellation_UsesBridgeEnvelopeAndCancelledResult()
    {
        var message = BridgeProtocol.Deserialize(
            BridgeProtocol.Serialize(BridgeMessage.CreateCancelCommand("cancel-request"))
        );
        var result = run_tests.CreateRequestCancelledResult();

        Assert.That(message, Is.Not.Null);
        Assert.That(message!.message_type, Is.EqualTo(BridgeMessageTypes.CancelCommand));
        Assert.That(message.request_id, Is.EqualTo("cancel-request"));
        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Cancelled));
        Assert.That(result.diagnostic, Does.Contain("MCP request ended"));
    }

    [Test]
    public void ReimportSettlement_WaitsForIdleSettleWindow()
    {
        Assert.That(ConduitToolRunner.ReimportIdleSettleUpdates, Is.EqualTo(8));

        Assert.That(ConduitToolRunner.ShouldWaitForReimportIdle(false, false, false, 8), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForReimportIdle(true, true, false, 8), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForReimportIdle(true, false, true, 8), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForReimportIdle(true, false, false, 0), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForReimportIdle(true, false, false, 7), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForReimportIdle(true, false, false, 8), Is.False);
    }

    [TestCase("Assets/Example.cs", false, true)]
    [TestCase("Assets/Example.ASMDEF", false, true)]
    [TestCase("Assets/Example.asmref", false, true)]
    [TestCase("Assets/csc.rsp", false, true)]
    [TestCase("Assets/Managed.dll", true, true)]
    [TestCase("Assets/Native.dll", false, false)]
    [TestCase("Assets/Example.shader", false, false)]
    public void ReimportAssets_IdentifiesScriptCompilationInputs(
        string assetPath,
        bool isManagedAssembly,
        bool expected
    )
        => Assert.That(
            AssetImportMonitor.IsCompilationInputAssetPath(assetPath, isManagedAssembly),
            Is.EqualTo(expected)
        );

    [Test]
    public void ReimportAssets_CompilationInputDiagnosticDirectsToRefresh()
    {
        const string assetPath = "Assets/Example.cs";

        var diagnostic = AssetImportMonitor.BuildCompilationInputReimportDiagnostic(assetPath);

        Assert.That(diagnostic, Does.Contain(assetPath));
        Assert.That(diagnostic, Does.Contain(BridgeCommandTypes.ReimportAssets));
        Assert.That(diagnostic, Does.Contain("No assets were reimported"));
        Assert.That(diagnostic, Does.Contain(BridgeCommandTypes.RefreshAssetDatabase));
    }

    [Test]
    public void SimplifyStackTrace_RestoresAsyncLocalFunction()
    {
        string stackTrace = string.Join("\n",
            "UnityEngine.Logger:Log",
            "HK.Analytics/<<EnsureInitialized>g__InitializeAsync|17_0>d:MoveNext ()",
            "System.Runtime.CompilerServices.AsyncVoidMethodBuilder:Start<HK.Analytics/<<EnsureInitialized>g__InitializeAsync|17_0>d>",
            "HK.Analytics:<EnsureInitialized>g__InitializeAsync|17_0",
            "HK.Analytics:EnsureInitialized ()",
            "HK.Analytics:Bootstrap ()");

        Assert.That(
            ConduitUtility.SimplifyStackTrace(stackTrace),
            Is.EqualTo(string.Join("\n",
                "UnityEngine.Logger:Log",
                "HK.Analytics:EnsureInitialized.InitializeAsync",
                "HK.Analytics:EnsureInitialized",
                "HK.Analytics:Bootstrap")));
    }

    [Test]
    public void CommandLogStackCleanup_RemovesAsyncBuilderFramesAndSchedulerTail()
    {
        string stackTrace = string.Join("\n",
            "UnityEngine.Debug:LogWarning ",
            "Unity.Services.Analytics.Internal.Dispatcher:Flush () ",
            "Unity.Services.Analytics.AnalyticsServiceInstance:Flush () ",
            "Unity.Services.Analytics.AnalyticsServiceInstance:ApplicationQuit () ",
            "Unity.Services.Analytics.AnalyticsContainer:CleanUp () ",
            "Unity.Services.Analytics.AnalyticsContainer:EditorCleanUp (UnityEditor.PlayModeStateChange) ",
            "UnityEditor.EditorApplication:Internal_PlayModeStateChanged ",
            "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<bool>:SetResult ",
            "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<bool>:SetResult ",
            "UnityEngine.UnitySynchronizationContext:ExecuteTasks ()");

        Assert.That(
            ConduitToolRunner.CleanCapturedLogStack(BridgeCommandKind.Show, stackTrace, LogType.Warning),
            Is.EqualTo(string.Join("\n",
                "UnityEngine.Debug:LogWarning",
                "Unity.Services.Analytics.Internal.Dispatcher:Flush",
                "Unity.Services.Analytics.AnalyticsServiceInstance:Flush",
                "Unity.Services.Analytics.AnalyticsServiceInstance:ApplicationQuit",
                "Unity.Services.Analytics.AnalyticsContainer:CleanUp",
                "Unity.Services.Analytics.AnalyticsContainer:EditorCleanUp",
                "UnityEditor.EditorApplication:Internal_PlayModeStateChanged")));
    }

    [Test]
    public void ExecuteCode_LogStackCleanup_RemovesDebugLogCompilerCallbackStack()
    {
        string stackTrace = string.Join("\n",
            "UnityEngine.Debug:Log",
            "System.Reflection.MethodBase:Invoke",
            "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<UnityEditor.Compilation.CompilerMessage[]>:SetResult",
            "System.Threading.Tasks.TaskCompletionSource`1<UnityEditor.Compilation.CompilerMessage[]>:TrySetResult",
            "UnityEditor.EditorApplication:get_isCompiling",
            "HK.Constants:get_AnyAssetReloadInProgress",
            "HK.BlitzRenderer.Animation.BlitzAnimationManager:OnUpdate",
            "HK.UpdateManager:RuntimeLateUpdate");

        Assert.That(
            ConduitToolRunner.CleanCapturedLogStack(BridgeCommandKind.ExecuteCode, stackTrace, LogType.Log),
            Is.Null);
    }

    [Test]
    public void ExecuteCode_LogStackCleanup_TrimsWarningCompilerCallbackStack()
    {
        string stackTrace = string.Join("\n",
            "UnityEngine.Debug:LogWarning",
            "System.Reflection.MethodBase:Invoke",
            "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<UnityEditor.Compilation.CompilerMessage[]>:SetResult",
            "System.Threading.Tasks.TaskCompletionSource`1<UnityEditor.Compilation.CompilerMessage[]>:TrySetResult",
            "UnityEditor.EditorApplication:get_isCompiling",
            "HK.Constants:get_AnyAssetReloadInProgress",
            "HK.BlitzRenderer.Animation.BlitzAnimationManager:OnUpdate",
            "HK.UpdateManager:RuntimeLateUpdate");

        Assert.That(
            ConduitToolRunner.CleanCapturedLogStack(BridgeCommandKind.ExecuteCode, stackTrace, LogType.Warning),
            Is.EqualTo("UnityEngine.Debug:LogWarning"));
    }

    [Test]
    public void ExecuteCode_LogStackCleanup_TrimsAtExecuteCodeInvokeAndPreservesUserReflection()
    {
        string stackTrace = string.Join("\n",
            "UnityEngine.Debug:LogWarning",
            "Game.ReflectedTarget:Run",
            "System.Reflection.MethodBase:Invoke",
            "Game.ReflectionCaller:Call",
            "ConduitGenerated.ExecuteCode.SnippetHost_1:Execute",
            "System.Reflection.MethodBase:Invoke",
            "Conduit.execute_code:InvokeAsync",
            "Conduit.execute_code:ExecuteCachedCompilationAsync");

        Assert.That(
            ConduitToolRunner.CleanCapturedLogStack(BridgeCommandKind.ExecuteCode, stackTrace, LogType.Warning),
            Is.EqualTo(string.Join("\n",
                "UnityEngine.Debug:LogWarning",
                "Game.ReflectedTarget:Run",
                "System.Reflection.MethodBase:Invoke",
                "Game.ReflectionCaller:Call")));
    }

    [Test]
    public void CommandLogStackCleanup_DoesNotUseExecuteCodeBoundaryForOtherCommands()
    {
        string stackTrace = string.Join("\n",
            "UnityEngine.Debug:LogWarning",
            "System.Reflection.MethodBase:Invoke",
            "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<UnityEditor.Compilation.CompilerMessage[]>:SetResult",
            "HK.UpdateManager:RuntimeLateUpdate");

        Assert.That(
            ConduitToolRunner.CleanCapturedLogStack(BridgeCommandKind.Show, stackTrace, LogType.Warning),
            Is.EqualTo(string.Join("\n",
                "UnityEngine.Debug:LogWarning",
                "System.Reflection.MethodBase:Invoke",
                "HK.UpdateManager:RuntimeLateUpdate")));
    }

    [Test]
    public void ExecuteCode_LogStackCleanup_RejectsGeneratedSnippetFrameAsBoundaryEvidence()
    {
        string stackTrace = string.Join("\n",
            "UnityEngine.Debug:LogWarning",
            "ConduitGenerated.ExecuteCode.SnippetHost_1:Execute",
            "System.Reflection.MethodBase:Invoke",
            "HK.UpdateManager:RuntimeLateUpdate");

        Assert.That(
            ConduitToolRunner.CleanCapturedLogStack(BridgeCommandKind.ExecuteCode, stackTrace, LogType.Warning),
            Is.EqualTo(string.Join("\n",
                "UnityEngine.Debug:LogWarning",
                "System.Reflection.MethodBase:Invoke",
                "HK.UpdateManager:RuntimeLateUpdate")));
    }

    [Test]
    public void CommandLogStackCleanup_RemovesPlainDebugLogStacks()
    {
        string stackTrace = string.Join("\n",
            "UnityEngine.Debug:Log ",
            "Game.Action:Run");

        Assert.That(
            ConduitToolRunner.CleanCapturedLogStack(BridgeCommandKind.Show, stackTrace, LogType.Log),
            Is.Null);
    }

    [Test]
    public void LogCapture_SeparatesQuotedMessageFromStackAndRepeatCount()
    {
        string formatted = ConduitToolRunner.FormatCapturedLogEntryForTest(
            "line 1\nline 2",
            string.Join("\n",
                "UnityEngine.Debug:LogWarning",
                "Game.Action:Run"),
            repeatCount: 3);

        Assert.That(
            formatted,
            Is.EqualTo(string.Join("\n",
                "> line 1",
                "> line 2",
                "",
                "UnityEngine.Debug:LogWarning",
                "Game.Action:Run",
                "",
                "*log repeated 3 times*")));
    }

    [Test]
    public void LogCapture_OmitsCompilerMessagesAlreadyShownInDiagnostic()
    {
        const string logMessage = "Assets/Scripts/Foo.cs(12,34): error CS0103: The name 'Missing' does not exist in the current context";
        const string diagnostic = "Library/ScriptAssemblies/Assembly-CSharp.dll: Assets/Scripts/Foo.cs(12,34): error CS0103: The name 'Missing' does not exist in the current context (Assets/Scripts/Foo.cs:12)";

        Assert.That(ConduitToolRunner.ShouldOmitDiagnosticLogEntry(logMessage, diagnostic), Is.True);
    }

    [Test]
    public void LogCapture_KeepsNonCompilerMessages()
    {
        const string logMessage = "Failed to import Assets/Scripts/Foo.cs";
        const string diagnostic = "Failed to import Assets/Scripts/Foo.cs";

        Assert.That(ConduitToolRunner.ShouldOmitDiagnosticLogEntry(logMessage, diagnostic), Is.False);
    }

    [Test]
    public void LogCapture_TestRunLogPolicyKeepsFullLogsForSmallRuns()
    {
        Assert.That(run_tests.ShouldIncludeAllTestLogs(0), Is.True);
        Assert.That(run_tests.ShouldIncludeAllTestLogs(1), Is.True);
        Assert.That(run_tests.ShouldIncludeAllTestLogs(3), Is.True);
        Assert.That(run_tests.ShouldIncludeAllTestLogs(4), Is.False);
        Assert.That(run_tests.LargeTestRunLogNote, Is.EqualTo("*Non-error logs are omitted when more than 3 tests run.*"));

        Assert.That(ToolLogCapture.ShouldIncludeTestLogEntry(LogType.Log, includeAllLogs: true), Is.True);
        Assert.That(ToolLogCapture.ShouldIncludeTestLogEntry(LogType.Warning, includeAllLogs: true), Is.True);
        Assert.That(ToolLogCapture.ShouldIncludeTestLogEntry(LogType.Error, includeAllLogs: false), Is.True);
        Assert.That(ToolLogCapture.ShouldIncludeTestLogEntry(LogType.Assert, includeAllLogs: false), Is.True);
        Assert.That(ToolLogCapture.ShouldIncludeTestLogEntry(LogType.Exception, includeAllLogs: false), Is.True);
        Assert.That(ToolLogCapture.ShouldIncludeTestLogEntry(LogType.Log, includeAllLogs: false), Is.False);
        Assert.That(ToolLogCapture.ShouldIncludeTestLogEntry(LogType.Warning, includeAllLogs: false), Is.False);
    }

    [Test]
    public void LogCapture_DropsIgnoredBurstWarning()
    {
        Assert.That(
            ConduitToolRunner.ShouldSuppressCapturedLogEntry(
                "/home/apk/src/hk2/Assets/Foo.cs(164,17): Burst warning BC1371: A discarded call is irrelevant."),
            Is.True);
        Assert.That(
            ConduitToolRunner.ShouldSuppressCapturedLogEntry(
                "Burst warning BC1371: A discarded call is irrelevant."),
            Is.True);
        Assert.That(
            ConduitToolRunner.ShouldSuppressCapturedLogEntry(
                "Assets/Foo.cs(12,3): Burst warning BC1370: A relevant warning."),
            Is.False);
    }

    [Test]
    public void LogCapture_SimplifiesBurstDiagnostics()
    {
        const string hash = "7435d70d723590c51e89202ae2f9be71";
        const string gameAssembly = "Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null";
        const string coreAssembly = "UnityEngine.CoreModule, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null";
        const string mscorlib = "mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";
        var input = string.Join("\n",
            "/home/apk/src/hk2/Assets/Scripts/Foo.cs(164,17): Burst error BC1091: " +
            "A call to the method Unity.Mathematics.math.any(Unity.Mathematics.bool3 x) -> " +
            $"System.Boolean, {mscorlib}_{hash} from {gameAssembly} failed.",
            "While compiling job:",
            "Unity.Jobs.IJobParallelForExtensions+ParallelForJobStruct`1" +
            "[[HK.PhysicsCharacterUpdateJobExtensions+DirectWrapper`1" +
            $"[[HK.CapsuleCollisionSolver+FinalizeSlide, {gameAssembly}]], {gameAssembly}]], {coreAssembly}" +
            "::Execute(" +
            "HK.PhysicsCharacterUpdateJobExtensions+DirectWrapper`1" +
            $"[[HK.CapsuleCollisionSolver+FinalizeSlide, {gameAssembly}]]&, {gameAssembly}" +
            $"|System.Int32, {mscorlib})");

        var output = ConduitToolRunner.NormalizeCapturedLogMessage(input);

        Assert.That(output, Does.Contain("math.any(bool3 x) -> bool failed."));
        Assert.That(
            output,
            Does.Contain(
                "IJobParallelForExtensions+ParallelForJobStruct<PhysicsCharacterUpdateJobExtensions+DirectWrapper<CapsuleCollisionSolver+FinalizeSlide>>" +
                "::Execute(ref PhysicsCharacterUpdateJobExtensions+DirectWrapper<CapsuleCollisionSolver+FinalizeSlide>, int)"));
        Assert.That(output, Does.Not.Contain("Version="));
        Assert.That(output, Does.Not.Contain("PublicKeyToken"));
        Assert.That(output, Does.Not.Contain("Unity.Mathematics."));
        Assert.That(output, Does.Not.Contain("&|"));
        Assert.That(output, Does.Not.Contain(hash));
    }

    [Test]
    public void LogCapture_FormatsBurstRawParameterSignatures()
    {
        const string gameAssembly = "Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null";
        const string coreAssembly = "UnityEngine.CoreModule, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null";
        const string mscorlib = "mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

        Assert.That(
            ConduitToolRunner.NormalizeCapturedLogMessage(
                "Burst error BC1000: While compiling job:\n" +
                $"Example.Job::Execute(System.Int32&, {mscorlib}|System.Single&, {mscorlib}|System.Boolean, {mscorlib})"),
            Does.Contain("Job::Execute(ref int, ref float, bool)"));

        Assert.That(
            ConduitToolRunner.NormalizeCapturedLogMessage(
                "Burst error BC1001: While compiling job:\n" +
                "Unity.Jobs.IJobExtensions+JobStruct`1" +
                $"[[Foo.Namespace.MyJob, {gameAssembly}]], {coreAssembly}" +
                "::Execute(" +
                "Foo.Namespace.NativeBox`1" +
                $"[[Foo.Namespace.MyValue, {gameAssembly}]], {gameAssembly}&, {gameAssembly}" +
                $"|System.Int32, {mscorlib})"),
            Does.Contain("IJobExtensions+JobStruct<MyJob>::Execute(ref NativeBox<MyValue>, int)"));

        Assert.That(
            ConduitToolRunner.NormalizeCapturedLogMessage(
                "Burst error BC1002: While compiling job:\n" +
                "Example.Runner::Execute(Outer<Inner<int,float>>&|Unity.Mathematics.float3|System.IntPtr)"),
            Does.Contain("Runner::Execute(ref Outer<Inner<int,float>>, float3, nint)"));

        Assert.That(
            ConduitToolRunner.NormalizeCapturedLogMessage(
                "Burst error BC1003: While compiling job:\n" +
                "First.Call(System.Int32&|System.Boolean) then Second.Call(Unity.Mathematics.bool3&|System.UInt64)"),
            Does.Contain("Call(ref int, bool) then Call(ref bool3, ulong)"));

        Assert.That(
            ConduitToolRunner.NormalizeCapturedLogMessage("Burst error BC1004: message contains (left|right) as text"),
            Does.Contain("message contains (left|right) as text"));
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

sealed class BurstInspectorOptionsFixture
{
    public bool EnableBurstSafetyChecks { get; set; } = true;
    public bool ForceEnableBurstSafetyChecks { get; set; } = true;
    public bool EnableBurstDebug { get; set; } = true;
}

sealed class ConduitCustomShowAsset : ScriptableObject
{
    string ToStringForMCP() => "Custom MCP show output";
}

sealed class ConduitThrowingEnumerableAsset : ScriptableObject
{
    readonly ConduitThrowingEnumerable throwingEnumerable = new();
}

sealed class ConduitThrowingEnumerable : IEnumerable
{
    public IEnumerator GetEnumerator() => throw new NotImplementedException();
}

sealed class ConduitNativeIndexableAsset : ScriptableObject
{
    object? indexableNumbers;

    public void Initialize() => indexableNumbers = CreateNativeList(1, 2, 3);

    public void Dispose()
    {
        if (indexableNumbers is IDisposable disposable)
            disposable.Dispose();

        indexableNumbers = null;
    }

    static object CreateNativeList(params int[] values)
    {
        var collectionsAssembly = FindLoadedAssembly("Unity.Collections")
                                  ?? throw new InvalidOperationException("Unity.Collections assembly is not loaded.");
        var allocatorType = FindLoadedType("Unity.Collections.Allocator")
                            ?? throw new InvalidOperationException("Unity.Collections.Allocator type is not loaded.");
        var allocatorManagerType = collectionsAssembly.GetType("Unity.Collections.AllocatorManager")
                                   ?? throw new InvalidOperationException("Unity.Collections.AllocatorManager type is not loaded.");
        var nativeListType = collectionsAssembly.GetType("Unity.Collections.NativeList`1")
                             ?.MakeGenericType(typeof(int))
                             ?? throw new InvalidOperationException("Unity.Collections.NativeList<T> type is not loaded.");
        var allocator = Enum.Parse(allocatorType, "Persistent");
        var allocatorHandle = allocatorManagerType
                                  .GetMethod("ConvertToAllocatorHandle", BindingFlags.Public | BindingFlags.Static)
                                  ?.Invoke(null, new[] { allocator })
                              ?? throw new InvalidOperationException("Could not create a persistent allocator handle.");
        object list = Activator.CreateInstance(nativeListType, new[] { allocatorHandle })
                      ?? throw new InvalidOperationException("Could not create NativeList<int>.");
        var add = nativeListType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance)
                  ?? throw new InvalidOperationException("NativeList<int>.Add was not found.");

        foreach (var value in values)
            add.Invoke(list, new object[] { value });

        return list;

        static System.Reflection.Assembly? FindLoadedAssembly(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == name)
                    return assembly;

            return null;
        }

        static Type? FindLoadedType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetType(fullName) is { } type)
                    return type;

            return null;
        }
    }
}

sealed class ConduitShowFormatAsset : ScriptableObject
{
}

sealed class ConduitNestedShowAsset : ScriptableObject
{
    [SerializeField] ConduitNestedShowLoadout loadout = new();
}

[Serializable]
sealed class ConduitNestedShowLoadout
{
    [SerializeField] ConduitNestedShowInventoryLoot inventoryLoot = new();
}

[Serializable]
sealed class ConduitNestedShowInventoryLoot
{
    [SerializeField] int[] entries = { 1, 2 };
    [SerializeField] bool chooseSingle;
}

interface ConduitReflectInterfaceFixture
{
    void ReflectInterfaceMethod();
}

class ConduitReflectBaseFixture
{
    protected int baseProtectedField;

    protected string ReflectBaseOnlyMethod() => string.Empty;

    public virtual string ReflectVirtualMethod() => string.Empty;
}

sealed class ConduitReflectDerivedFixture : ConduitReflectBaseFixture, ConduitReflectInterfaceFixture
{
    int derivedPrivateField;

    public string DerivedProperty { get; private set; } = string.Empty;

    public ConduitReflectDerivedFixture() { }

    static ConduitReflectDerivedFixture() { }

    public T GenericMethod<T>(ref int value, out string text, params T[] items)
    {
        value += items.Length;
        text = string.Empty;
        return items.Length == 0 ? default! : items[0];
    }

    public override string ReflectVirtualMethod() => DerivedProperty;

    public void ReflectInterfaceMethod() { }
}

struct ConduitReflectStructFixture
{
    public int Value;
}

enum ConduitReflectEnumFixture
{
    First,
    Second,
}

delegate void ConduitReflectDelegateFixture();

sealed class ConduitReflectExactRankFixture
{
    public void ReflectRank() { }
}

sealed class ConduitReflectLooseRankFixture
{
    public void PrefixReflectRankSuffix() { }
}

sealed class ConduitReflectAmbiguousAlpha
{
}

sealed class ConduitReflectAmbiguousBeta
{
}
