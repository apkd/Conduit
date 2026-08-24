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
        var path = ProfilerTool.ResolveCapturePathForTest("sample", allocateDefault: false);

        Assert.That(path.DisplayPath, Is.EqualTo("Temp/profiler/sample.data"));
    }

    [Test]
    public async Task ProfilerCapture_RestrictsHistoryToRequestedFrameCount()
    {
        var result = await ProfilerTool.RecordAsync(
            new[]
            {
                "action=capture",
                "target=edit_mode",
                "frames=1",
                "delay_seconds=0",
            }
        );

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(result.return_value, Is.EqualTo("Profile captured!\nFrame count: 1"));
        Assert.That(ProfilerTool.GetAvailableFrameCountForTest(), Is.EqualTo(1));
    }

    [Test]
    public void ProfilerCapturePath_RejectsRelativeParentTraversal()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProfilerTool.ResolveCapturePathForTest("../outside.data", allocateDefault: false));

        Assert.That(exception!.Message, Does.Contain("contains parent traversal"));
    }

    [Test]
    public void ProfilerFrameRange_UsesAvailableFrameOrdinalsAndClampsLargeRanges()
    {
        var frames = ProfilerTool.ResolveFrameRangeForTest(10, "0..^1", out var warnings);

        Assert.That(frames, Is.EqualTo(new[] { 1000, 1001, 1002, 1003, 1004, 1005, 1006, 1007, 1008, 1009 }));
        Assert.That(warnings, Is.Empty);

        var clamped = ProfilerTool.ResolveFrameRangeForTest(2500, "0..^1", out warnings);

        Assert.That(clamped, Has.Count.EqualTo(2000));
        Assert.That(clamped[0], Is.EqualTo(1500));
        Assert.That(clamped[^1], Is.EqualTo(3499));
        Assert.That(warnings, Does.Contain("frame_range_clamped_to_latest_2000"));
    }

    [Test]
    public void ProfilerFrameRange_RejectsMalformedEndpoints()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ProfilerTool.ResolveFrameRangeForTest(10, "banana", out _));
        Assert.Throws<InvalidOperationException>(() =>
            ProfilerTool.ResolveFrameRangeForTest(10, "nonsense..garbage", out _));
    }

    [Test]
    public void ProfilerBrowse_NonTrivialFilterDependsOnSort()
    {
        Assert.That(ProfilerTool.IsNonTrivialForTest(totalMs: 1.0, selfMs: 0.1, gcBytes: 0, calls: 1, frameTimeMs: 100, sort: "total_ms"), Is.True);
        Assert.That(ProfilerTool.IsNonTrivialForTest(totalMs: 0.9, selfMs: 0.9, gcBytes: 0, calls: 1, frameTimeMs: 100, sort: "total_ms"), Is.False);
        Assert.That(ProfilerTool.IsNonTrivialForTest(totalMs: 10, selfMs: 0.9, gcBytes: 0, calls: 1, frameTimeMs: 100, sort: "self_ms"), Is.False);
        Assert.That(ProfilerTool.IsNonTrivialForTest(totalMs: 0, selfMs: 0, gcBytes: 1, calls: 1, frameTimeMs: 100, sort: "gc_bytes"), Is.True);
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

        var aggregate = ProfilerTool.AggregateWorkerHierarchiesForTest(worker0, worker1);

        Assert.That(aggregate.Name, Is.EqualTo("Job Workers"));
        Assert.That(aggregate.TotalMs, Is.EqualTo(10));
        Assert.That(aggregate.SelfMs, Is.EqualTo(3));
        Assert.That(aggregate.GcBytes, Is.EqualTo(96));
        Assert.That(aggregate.Calls, Is.EqualTo(2));
        Assert.That(aggregate.ContributingWorkerCount, Is.EqualTo(2));
        Assert.That(aggregate.MinTotalMs, Is.EqualTo(4));
        Assert.That(aggregate.MaxTotalMs, Is.EqualTo(6));
        Assert.That(ProfilerTool.GetWorkerMeanMsForTest(aggregate), Is.EqualTo(5));
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
        Assert.That(ProfilerTool.GetWorkerMeanMsForTest(jobB), Is.EqualTo(1));
        Assert.That(ProfilerTool.GetNormalizedWorkerMsForTest(jobB, workerCount: 2), Is.EqualTo(0.5));
        Assert.That(jobB.MinTotalMs, Is.EqualTo(1));
        Assert.That(jobB.MaxTotalMs, Is.EqualTo(1));

        static ProfilerTool.HierarchyRow Row(
            string name,
            double totalMs,
            double selfMs,
            double gcBytes,
            double calls,
            params ProfilerTool.HierarchyRow[] children
        )
        {
            var row = new ProfilerTool.HierarchyRow
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
        var label = ProfilerThreadLabels.ClassifyThread("Main Thread", "");
        Assert.That(label, Is.EqualTo("main"));

        label = ProfilerThreadLabels.ClassifyThread("Render Thread", "");
        Assert.That(label, Is.EqualTo("render"));

        label = ProfilerThreadLabels.ClassifyThread("Worker 7", "Job");
        Assert.That(label, Is.EqualTo("worker7"));

        label = ProfilerThreadLabels.ClassifyThread("Job Worker 12", "Job");
        Assert.That(label, Is.EqualTo("worker12"));

        Assert.That(ProfilerThreadLabels.ClassifyThread("Background Worker", "Loading"), Is.Null);
        Assert.That(ProfilerThreadLabels.ClassifyThread("GfxDeviceWorker", ""), Is.Null);
    }

    [Test]
    public void ProfilerOverview_ThreadLabelsUseStableDisplayOrder()
    {
        var labels = ProfilerThreadLabels.FormatThreadLabels(
            new[] { "worker10", "render", "worker2", "main", "worker0" }
        );

        Assert.That(labels, Is.EqualTo("main, render, worker0, worker2, worker10"));
    }

    [Test]
    public void ProfilerOverview_InterestingSampleFilterSkipsContainersAndNoise()
    {
        Assert.That(
            ProfilerTool.ShouldIncludeOverviewSampleForTest("EditorLoop", totalMs: 33, selfMs: 33, gcBytes: 0, frameTimeMs: 40, childCount: 0, mode: "cpu_ms"),
            Is.False
        );
        Assert.That(
            ProfilerTool.ShouldIncludeOverviewSampleForTest("EnemySystem.Update", totalMs: 2, selfMs: 2, gcBytes: 0, frameTimeMs: 100, childCount: 0, mode: "cpu_ms"),
            Is.True
        );
        Assert.That(
            ProfilerTool.ShouldIncludeOverviewSampleForTest("Tiny.Marker", totalMs: 0.5, selfMs: 0.5, gcBytes: 0, frameTimeMs: 100, childCount: 0, mode: "cpu_ms"),
            Is.False
        );
        Assert.That(
            ProfilerTool.ShouldIncludeOverviewSampleForTest("Allocator", totalMs: 0.01, selfMs: 0.01, gcBytes: 64, frameTimeMs: 100, childCount: 0, mode: "gc_kb"),
            Is.True
        );
        Assert.That(
            ProfilerTool.ShouldIncludeOverviewSampleForTest("Allocator.Parent", totalMs: 0.01, selfMs: 0.01, gcBytes: 64, frameTimeMs: 100, childCount: 1, mode: "gc_kb"),
            Is.False
        );
    }

    [Test]
    public void ProfilerOverview_ActionableCpuUsesLeafTotalAndParentSelf()
    {
        Assert.That(ProfilerTool.GetActionableCpuMsForTest(totalMs: 20, selfMs: 1, childCount: 0), Is.EqualTo(20));
        Assert.That(ProfilerTool.GetActionableCpuMsForTest(totalMs: 20, selfMs: 1, childCount: 3), Is.EqualTo(1));
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

        var formatted = ProfilerValueFormatter.FormatSamplePath(path);

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
        var snapshot = StatusTool.Status();

        Assert.That(snapshot, Does.Contain("\"profiler_status_line\""));
        Assert.That(snapshot, Does.Contain("Profiler:"));
    }
}
