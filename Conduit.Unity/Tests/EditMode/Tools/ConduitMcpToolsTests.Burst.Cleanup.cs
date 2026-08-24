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
    public void ViewBurstAsmOutput_EmptyDisassemblyReportsCompileFailure()
    {
        var target = new BurstTarget("Example.BrokenJob - (IJob)", "Execute", "Example", "Example.BrokenJob");

        var diagnostic = BurstOutputFormatter.BuildEmptyDisassemblyDiagnostic(target);

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

        var result = BurstOutputFormatter.StripTrailingTemporaryLabelBlocks(input);

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

        Assert.That(BurstOutputFormatter.StripTrailingTemporaryLabelBlocks(middleLabel), Is.EqualTo(middleLabel));
        Assert.That(
            BurstOutputFormatter.StripTrailingTemporaryLabelBlocks(instructionSuffix),
            Is.EqualTo(instructionSuffix)
        );
    }

    [Test]
    public void ViewBurstAsmCleanup_JoinsSplitSourceFileDirectives()
    {
        var input = string.Join("\n",
            ".file \"main\"",
            ".file 3 \"./Library/PackageCache/com.unity.collections/Unity.Collections\" \"AllocatorManager.cs\"",
            ".file 8 \"/build/Runtime/Jobs/Managed\" \"IJob.cs\" checksum 1");

        var result = ViewBurstAsmTool.NormalizeSourceFileDirectives(input);

        Assert.That(result, Is.EqualTo(string.Join("\n",
            ".file \"main\"",
            ".file 3 \"./Library/PackageCache/com.unity.collections/Unity.Collections/AllocatorManager.cs\"",
            ".file 8 \"/build/Runtime/Jobs/Managed/IJob.cs\" checksum 1")));
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

        var result = BurstOutputFormatter.CleanDisassembly(input);

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

        var output = BurstOutputFormatter.BuildOutput(target, "ret");

        Assert.That(output, Is.EqualTo(
            "# Summary\n\n" +
            "**Function:** `GenerateInteriorMesh(NativeArray<ShadowMeshVertex>&, NativeArray<int>&, ShadowEdge&, int&)`\n\n" +
            "- Instructions: 1\n" +
            "- Control flow: 1 return\n" +
            "- Natural loops: 0\n" +
            "- Calls: direct 0, indirect 0\n" +
            "- Top instructions: ret=1\n\n" +
            "# Asm\n\n" +
            "```asm\nret\n```"));
    }

    [Test]
    public void ViewBurstAsmOutput_FormatsCilAsRawCompilerOutput()
    {
        var target = new BurstTarget("Example.Execute()", "Execute", "Example", "Example");
        Assert.That(ViewBurstAsmTool.TryParseOutputTarget("cil", out var outputTarget), Is.True);

        var output = BurstOutputFormatter.BuildRawOutput(target, ".method Execute", outputTarget);

        Assert.That(output, Is.EqualTo(
            "**Function:** `Execute()`\n\n" +
            "## CIL\n\n" +
            "```cil\n.method Execute\n```"));
        Assert.That(output, Does.Not.Contain("# Summary"));
        Assert.That(output, Does.Not.Contain("Compilation"));
    }

    [Test]
    public void ViewBurstAsmOutput_FormatsOptimizedLlvmIrWithCompilerRemarks()
    {
        var target = new BurstTarget("Example.Execute()", "Execute", "Example", "Example");
        Assert.That(ViewBurstAsmTool.TryParseOutputTarget("llvmir", out var outputTarget), Is.True);
        const string remarks =
            "--- !Passed\n" +
            "Remark Type: Passed\n" +
            "Pass: inline\n" +
            "Function: Example.Execute\n" +
            "Message: inlined entry work\n" +
            "--- !Analysis\n" +
            "Remark Type: Analysis\n" +
            "Pass: loop-vectorize\n" +
            "Function: Example.Helper\n" +
            "Message: /src/Example.cs:12:0: loop not vectorized";
        var context = BurstCompilationParser.ParseCompilationContext(
            "--target=Auto\n--opt-level=2\n--float-mode=Default",
            "llvmir"
        );

        var output = BurstOutputFormatter.BuildRawOutput(
            target,
            "define void @Execute() { ret void }",
            outputTarget,
            context,
            remarks
        );

        Assert.That(output, Does.Contain(
            "**Compilation:** target `Compiler default` · `Balanced` · floats `Strict/Standard`"));
        Assert.That(output, Does.Contain("- `Passed` · `inline` — inlined entry work"));
        Assert.That(output, Does.Not.Contain("function `Example.Execute`"));
        Assert.That(output, Does.Contain(
            "- `Analysis` · `loop-vectorize` · function `Helper` · `Example.cs:12` — loop not vectorized"));
        Assert.That(output, Does.Contain("## Optimized LLVM IR\n\n```llvm\ndefine void @Execute()"));
        Assert.That(output, Does.Not.Contain("# Summary"));
    }
}
