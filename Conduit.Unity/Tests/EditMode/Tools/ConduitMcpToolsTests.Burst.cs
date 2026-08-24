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
    public void ViewBurstAsmCommand_Parses()
    {
        var command = ConduitToolRunner.ParseIncomingCommand(BridgeCommandTypes.ViewBurstAsm);

        Assert.That(command, Is.EqualTo(BridgeCommandKind.ViewBurstAsm));
    }

    [Test]
    public void ViewBurstAsmMatch_SelectsExactAndUniqueSubstringTargets()
    {
        var targets = CreateBurstAsmTargets();

        var exact = BurstTargetMatcher.MatchTarget("Gameplay.Motion.MoveJob - (IJob)", targets);
        var substring = BurstTargetMatcher.MatchTarget("RenderChunk", targets);

        Assert.That(exact.Kind, Is.EqualTo(BurstAsmTargetMatchKind.Matched));
        Assert.That(exact.SelectedIndex, Is.EqualTo(0));
        Assert.That(substring.Kind, Is.EqualTo(BurstAsmTargetMatchKind.Matched));
        Assert.That(substring.SelectedIndex, Is.EqualTo(2));
    }

    [Test]
    public void ViewBurstAsmMatch_UsesTokenScoringForFuzzyNames()
    {
        var targets = CreateBurstAsmTargets();

        var match = BurstTargetMatcher.MatchTarget("render execute", targets);

        Assert.That(match.Kind, Is.EqualTo(BurstAsmTargetMatchKind.Matched));
        Assert.That(match.SelectedIndex, Is.EqualTo(2));
    }

    [Test]
    public void ViewBurstAsmMatch_RejectsAmbiguousTargets()
    {
        var targets = CreateBurstAsmTargets();

        var match = BurstTargetMatcher.MatchTarget("motion job", targets);

        Assert.That(match.Kind, Is.EqualTo(BurstAsmTargetMatchKind.Ambiguous));
        Assert.That(match.CandidateIndexes, Is.EquivalentTo(new[] { 0, 1 }));
    }

    [Test]
    public void ViewBurstAsmMatch_ReturnsNoMatchWithCandidates()
    {
        var targets = CreateBurstAsmTargets();

        var match = BurstTargetMatcher.MatchTarget("missing target", targets);

        Assert.That(match.Kind, Is.EqualTo(BurstAsmTargetMatchKind.None));
        Assert.That(match.CandidateIndexes, Has.Length.EqualTo(3));
    }

    [Test]
    public void ViewBurstAsmNoMatch_EmptyQueryShowsOnlyCandidates()
    {
        var targets = CreateBurstAsmTargets();

        var diagnostic = BurstTargetMatcher.NoMatchDiagnostic(string.Empty, targets, new[] { 0, 1 });

        Assert.That(diagnostic, Does.StartWith("Candidates:"));
        Assert.That(diagnostic, Does.Not.Contain("No Burst compile target matched"));
        Assert.That(diagnostic, Does.Contain("- Gameplay.Motion.MoveJob - (IJob)"));
        Assert.That(diagnostic, Does.Contain("- Gameplay.Motion.MoveParticlesJob - (IJob)"));
    }

    [Test]
    public void ViewBurstAsmNoMatch_ReportsOmittedCandidates()
    {
        var targets = Enumerable.Range(0, 12)
            .Select(index => new BurstTarget($"Target {index}", "Execute", "", ""))
            .ToArray();
        var match = BurstTargetMatcher.MatchTarget(string.Empty, targets);

        var diagnostic = BurstTargetMatcher.NoMatchDiagnostic(
            string.Empty,
            targets,
            match.CandidateIndexes,
            match.CandidateCount
        );

        Assert.That(match.CandidateCount, Is.EqualTo(12));
        Assert.That(match.CandidateIndexes, Has.Length.EqualTo(10));
        Assert.That(diagnostic, Does.Contain("2 additional candidates were omitted."));
    }

    [Test]
    public void ViewBurstAsmOptions_UseInspectorDefaults()
    {
        var fixture = new BurstInspectorOptionsFixture();

        ViewBurstAsmTool.ApplyInspectorOptionOverrides(fixture);
        var options = ViewBurstAsmTool.BuildInspectorOptions("--float-mode=Default");

        Assert.That(fixture.EnableBurstSafetyChecks, Is.False);
        Assert.That(fixture.ForceEnableBurstSafetyChecks, Is.False);
        Assert.That(fixture.EnableBurstDebug, Is.False);
        Assert.That(options, Does.Contain("--float-mode=Default"));
        Assert.That(options, Does.Contain("--disable-warnings=BC1370;BC1322"));
        Assert.That(options, Does.Contain("--target=AVX2"));
        Assert.That(options, Does.Contain("--debug=2"));
        Assert.That(options, Does.Not.Contain("--disable-function-caching"));
        Assert.That(options, Does.Not.Contain("--disable-assembly-caching"));
        Assert.That(options, Does.EndWith("--dump=Asm"));
    }

    [TestCase("x86", "x86", "AVX2", "Intel", "Assembly", "Asm", "2")]
    [TestCase("wasm32", "wasm32", "WASM32", "Wasm", "Assembly", "Asm", "2")]
    [TestCase("armv8", "armv8", "ARMV8A_AARCH64_HALFFP", "ARM", "Assembly", "Asm", "2")]
    [TestCase("armv9", "armv9", "ARMV9A", "ARM", "Assembly", "Asm", "2")]
    [TestCase("cil", "cil", "Auto", "", "Cil", "IL", "0")]
    [TestCase("llvmir", "llvmir", "Auto", "", "OptimizedLlvmIr", "IROptimized", "0")]
    public void ViewBurstAsmOutputTarget_MapsSimplifiedNames(
        string input,
        string name,
        string compilerTarget,
        string asmKind,
        string outputKind,
        string dump,
        string debugLevel)
    {
        Assert.That(ViewBurstAsmTool.TryParseOutputTarget(input, out var target), Is.True);
        Assert.That(target.Name, Is.EqualTo(name));
        Assert.That(target.CompilerTarget, Is.EqualTo(compilerTarget));
        Assert.That(target.AsmKind, Is.EqualTo(asmKind));
        Assert.That(target.OutputKind.ToString(), Is.EqualTo(outputKind));
        Assert.That(target.Dump, Is.EqualTo(dump));
        Assert.That(target.DebugLevel, Is.EqualTo(debugLevel));
    }

    [Test]
    public void ViewBurstAsmOutputTarget_RejectsUnknownNames()
    {
        Assert.That(ViewBurstAsmTool.TryParseOutputTarget("sse2", out _), Is.False);
    }
}
