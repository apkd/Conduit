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

        var output = BurstOutputFormatter.BuildOutput(target, disassembly);

        Assert.That(output, Does.StartWith("# Summary\n\n**Function:** `JobStruct<MoveJob>.Execute(ref MoveJob data) -> void`\n\n- Instructions: 8\n"));
        Assert.That(output, Does.Contain("\n\n# Asm\n\n```asm\n.text"));
        Assert.That(output, Does.EndWith("```"));
        Assert.That(output, Does.Contain("- Control flow: 1 conditional branch, 1 return"));
        Assert.That(output, Does.Contain("- Calls: direct 1 (`helper`), indirect 0"));
        Assert.That(output, Does.Contain("- SIMD: packed compute 1, scalar compute 1, transfer 1; widest packed compute 256-bit"));
        Assert.That(output, Does.Contain("- Memory access instructions: 3 loads; stack/frame 1"));
        Assert.That(output, Does.Contain("- Explicit stack operations: push 1"));
        Assert.That(output, Does.Contain("- Top instructions:"));
    }

    [Test]
    public void ViewBurstAsmOutput_AttributesStaticInstructionsToTheTopSixteenSourceLines()
    {
        var target = new BurstTarget("Example.Execute()", "Execute", "Example", "Example");
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("nop");
        builder.AppendLine("# Example.cs:10");
        builder.AppendLine("mov               eax, dword ptr [rbx]");
        builder.AppendLine("mov               dword ptr [rbx], eax");
        builder.AppendLine("vaddps            ymm0, ymm1, ymm2");
        builder.AppendLine("jne               .Ldone");
        builder.AppendLine("call              helper");
        builder.AppendLine("# unknown");
        builder.AppendLine("nop");
        for (var line = 20; line <= 37; ++line)
        {
            builder.AppendLine($"# Example.cs:{line}");
            builder.AppendLine("add               eax, 1");
        }

        var output = BurstOutputFormatter.BuildOutput(target, builder.ToString());

        Assert.That(output, Does.Contain("# Source attribution"));
        Assert.That(output, Does.Contain("- Coverage: 23/25 instructions mapped; 2 unmapped/compiler-generated"));
        Assert.That(output, Does.Contain(
            "- `Example.cs:10`: 5 instr (loads 1, stores 1, packed compute 1, branches 1, calls 1)"));
        Assert.That(output, Does.Contain("- `Example.cs:34`: 1 instr"));
        Assert.That(output, Does.Not.Contain("- `Example.cs:35`:"));
        Assert.That(output, Does.Contain("- 3 more source mappings omitted."));
    }

    [Test]
    public void ViewBurstAsmOutput_OmitsSourceAttributionWithoutSourceMarkers()
    {
        var target = new BurstTarget("Example.Execute()", "Execute", "Example", "Example");

        var output = BurstOutputFormatter.BuildOutput(target, "ret");

        Assert.That(output, Does.Not.Contain("Source attribution"));
    }

    [Test]
    public void ViewBurstAsmOutput_ReportsNotableX86OpcodeClasses()
    {
        var target = new BurstTarget("Example.Execute()", "Execute", "Example", "Example");
        var disassembly = string.Join("\n",
            "idiv              ecx",
            "vsqrtps           ymm0, ymm1",
            "vfmadd231ps       ymm0, ymm1, ymm2",
            "vgatherdps        ymm0, [rax + ymm1 * 4], ymm2",
            "vscatterdps       [rax + ymm1 * 4], ymm0, ymm2",
            "lock xadd         dword ptr [rax], ecx",
            "mfence",
            "cpuid",
            "ret");

        var output = BurstOutputFormatter.BuildOutput(target, disassembly);

        Assert.That(output, Does.Contain(
            "- Notable opcode classes: divide 1, square root 1, FMA 1, gather 1, scatter 1, atomic 1, fence 1, serializing 1"));
        Assert.That(output, Does.Contain("lock xadd=1"));
    }

    [Test]
    public void ViewBurstAsmOutput_ReportsFrequencySortedTopInstructions()
    {
        var target = new BurstTarget("Example.Execute()", "Execute", "Example", "Example");
        var disassembly = string.Join("\n",
            "mov               eax, ebx",
            "sub               eax, 1",
            "mov               ecx, edx",
            "add               eax, ecx",
            "ret");

        var output = BurstOutputFormatter.BuildOutput(target, disassembly);

        Assert.That(output, Does.Contain(
            "- Top instructions: mov=2, add=1, ret=1, sub=1"));
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
            "dmb               ish",
            "isb",
            "ret");

        var output = BurstOutputFormatter.BuildOutput(target, disassembly);

        Assert.That(output, Does.Contain("- Instructions: 7"));
        Assert.That(output, Does.Contain("- SIMD: packed compute 1, transfer 1; widest packed compute 128-bit"));
        Assert.That(output, Does.Contain("- Control flow: 1 conditional branch, 1 return"));
        Assert.That(output, Does.Contain("- Calls: direct 1 (`helper`), indirect 0"));
        Assert.That(output, Does.Contain("- Memory access instructions: 1 load"));
        Assert.That(output, Does.Contain("- Notable opcode classes: FMA 1, fence 1, serializing 1"));
    }

    [Test]
    public void ViewBurstAsmOutput_PrefersTheTargetMethodOverLargerHelpers()
    {
        var target = new BurstTarget(
            "Unity.Collections.xxHash3.Hash64Long(byte*, byte*, long, byte*)",
            "Hash64Long",
            "Unity.Collections.xxHash3",
            "Unity.Collections.xxHash3"
        );
        var disassembly = string.Join("\n",
            "\"Collections.xxHash3.DefaultHashLongInternalLoop(byte* input) -> void\":",
            "nop",
            "nop",
            "nop",
            "ret",
            "\"Collections.xxHash3.Hash64Long(byte* input, byte* dest, long length, byte* secret) -> ulong\":",
            "ret");

        var output = BurstOutputFormatter.BuildOutput(target, disassembly);

        Assert.That(output, Does.StartWith(
            "# Summary\n\n**Function:** `Collections.xxHash3.Hash64Long(byte* input, byte* dest, long length, byte* secret) -> ulong`"));
        Assert.That(output, Does.Contain("- Instructions: 1"));
    }

    [Test]
    public void ViewBurstAsmOutput_FollowsScaffoldingOnlyEntryForwarders()
    {
        var target = new BurstTarget(
            "Unity.Collections.xxHash3.Hash64Long(byte*, byte*, long, byte*)",
            "Hash64Long",
            "Unity.Collections.xxHash3",
            "Unity.Collections.xxHash3"
        );
        var disassembly = string.Join("\n",
            "\"Collections.xxHash3+Hash64Long_1234$Invoke(byte* input) -> ulong\":",
            "vaddps            ymm0, ymm1, ymm2",
            "ret",
            "\"Collections.xxHash3.Hash64Long(byte* input) -> ulong\":",
            "push              rbp",
            "mov               rbp, rsp",
            "pop               rbp",
            "jmp               \"Collections.xxHash3+Hash64Long_1234$Invoke(byte* input) -> ulong\"@PLT");

        var output = BurstOutputFormatter.BuildOutput(target, disassembly);

        Assert.That(output, Does.StartWith(
            "# Summary\n\n**Function:** `Collections.xxHash3.Hash64Long(byte* input) -> ulong`"));
        Assert.That(output, Does.Contain("- SIMD: packed compute 1; widest packed compute 256-bit"));
    }

    [Test]
    public void ViewBurstAsmOutput_SeparatesDataMovementIdiomsAndReportsNaturalLoops()
    {
        var target = new BurstTarget("Example.Hash()", "Hash", "Example", "Example");
        var disassembly = string.Join("\n",
            ".Lloop:",
            "xor               eax, eax",
            "xor               ecx, edx",
            "movabs            r8, 0x12345678",
            "movabs            r9, helper",
            "vmovdqu           ymm0, ymmword ptr [rax]",
            "vpinsrd           xmm0, xmm0, eax, 1",
            "jne               .Lloop",
            "ret");

        var output = BurstOutputFormatter.BuildOutput(target, disassembly);

        Assert.That(output, Does.Contain("- SIMD: transfer 1, lane/shuffle 1"));
        Assert.That(output, Does.Contain("- Memory access instructions: 1 load"));
        Assert.That(output, Does.Contain("- XOR instructions: 2; zeroing 1; non-zeroing 1"));
        Assert.That(output, Does.Contain("- `movabs` materialization: numeric constants 1, symbol addresses 1"));
        Assert.That(output, Does.Contain("- Control flow: 1 conditional branch, 1 return"));
        Assert.That(output, Does.Contain("- Natural loops: 1; 1 backedge"));
        Assert.That(output, Does.Not.Contain("Backward branches (loop candidates)"));
        Assert.That(output, Does.Contain("Vector registers are used only for transfers or lane manipulation"));
    }

    [Test]
    public void ViewBurstAsmOutput_ReportsNestedNaturalLoopsWithExclusiveCounts()
    {
        var target = new BurstTarget("Example.Execute()", "Execute", "Example", "Example");
        var disassembly = string.Join("\n",
            "# Example.cs:10",
            ".LBB0_0:",
            "cmp               eax, 0",
            "je                .LBB0_3",
            "# Example.cs:20",
            ".LBB0_1:",
            "mov               eax, dword ptr [rdi]",
            "vaddps            ymm0, ymm1, ymm2",
            "jne               .LBB0_1",
            "# Example.cs:12",
            ".LBB0_2:",
            "add               ecx, 1",
            "jne               .LBB0_0",
            ".LBB0_3:",
            "ret");

        var output = BurstOutputFormatter.BuildOutput(target, disassembly);

        Assert.That(output, Does.Contain("- Natural loops: 2; 2 backedges; max depth 2; 7/8 instr in loop regions"));
        Assert.That(output, Does.Contain("# Loops\n\n- `L1`"));
        Assert.That(output, Does.Contain(
            "- `L1` `.LBB0_0` @ `Example.cs:10`: 4 instr + 3 nested (branches 2; exits 2)"));
        Assert.That(output, Does.Contain(
            "  - `L2` `.LBB0_1` @ `Example.cs:20`: 3 instr (loads 1, packed compute 1, branches 1; exits 1)"));
    }

    [Test]
    public void ViewBurstAsmOutput_RequiresDominanceAndReachabilityForNaturalLoops()
    {
        var target = new BurstTarget("Example.Execute()", "Execute", "Example", "Example");
        var disassembly = string.Join("\n",
            "test              eax, eax",
            "je                .LBB0_2",
            ".LBB0_1:",
            "jmp               .LBB0_3",
            ".LBB0_2:",
            "jne               .LBB0_1",
            ".LBB0_3:",
            "ret",
            ".LBB0_4:",
            "jne               .LBB0_4",
            "jmp               rax");

        var output = BurstOutputFormatter.BuildOutput(target, disassembly);

        Assert.That(output, Does.Contain("- Natural loops: 0"));
        Assert.That(output, Does.Not.Contain("Loop analysis suppressed"));
        Assert.That(output, Does.Not.Contain("# Loops"));
    }

    [Test]
    public void ViewBurstAsmOutput_ReportsMultipleLatchesAndExits()
    {
        var target = new BurstTarget("Example.Execute()", "Execute", "Example", "Example");
        var disassembly = string.Join("\n",
            ".LBB0_0:",
            "test              eax, eax",
            "je                .LBB0_2",
            ".LBB0_1:",
            "jne               .LBB0_0",
            ".LBB0_2:",
            "jne               .LBB0_0",
            "ret");

        var output = BurstOutputFormatter.BuildOutput(target, disassembly);

        Assert.That(output, Does.Contain("- Natural loops: 1; 2 backedges"));
        Assert.That(output, Does.Contain("exits 1; backedges 2"));
    }

    [Test]
    public void ViewBurstAsmOutput_SuppressesIncompleteReachableControlFlow()
    {
        var target = new BurstTarget("Example.Execute()", "Execute", "Example", "Example");
        var indirectJump = string.Join("\n",
            "test              eax, eax",
            "je                .LBB0_2",
            ".LBB0_1:",
            "jmp               rax",
            ".LBB0_2:",
            "ret");

        var indirectOutput = BurstOutputFormatter.BuildOutput(target, indirectJump);
        var missingTargetOutput = BurstOutputFormatter.BuildOutput(target, "jne .Lmissing\nret");
        var missingJumpOutput = BurstOutputFormatter.BuildOutput(target, "jmp .Lmissing");

        Assert.That(indirectOutput, Does.Contain(
            "- Loop analysis suppressed: reachable indirect jump at `.LBB0_1`."));
        Assert.That(missingTargetOutput, Does.Contain("- Loop analysis suppressed: missing conditional target `.Lmissing`"));
        Assert.That(missingJumpOutput, Does.Contain("- Loop analysis suppressed: missing jump target `.Lmissing`"));
        Assert.That(indirectOutput, Does.Not.Contain("# Loops"));
        Assert.That(missingTargetOutput, Does.Not.Contain("# Loops"));
    }

    [Test]
    public void ViewBurstAsmOutput_TreatsCallsAndExternalTailJumpsAsCompleteControlFlow()
    {
        var target = new BurstTarget("Example.Execute()", "Execute", "Example", "Example");

        var output = BurstOutputFormatter.BuildOutput(target, "call rax\njmp helper");

        Assert.That(output, Does.Contain("- Natural loops: 0"));
        Assert.That(output, Does.Contain("- Calls: direct 0, indirect 1"));
        Assert.That(output, Does.Not.Contain("Loop analysis suppressed"));
    }

    [Test]
    public void ViewBurstAsmOutput_ReportsArmNaturalLoops()
    {
        var target = new BurstTarget("Example.Execute()", "Execute", "Example", "Example");
        var disassembly = string.Join("\n",
            "# Example.cs:42",
            ".LBB0_0:",
            "ldr               w8, [x0]",
            "subs              w8, w8, 1",
            "b.ne              .LBB0_0",
            "ret");

        var output = BurstOutputFormatter.BuildOutput(target, disassembly);

        Assert.That(output, Does.Contain("- Natural loops: 1; 1 backedge; max depth 1; 3/4 instr in loop regions"));
        Assert.That(output, Does.Contain(
            "- `L1` `.LBB0_0` @ `Example.cs:42`: 3 instr (loads 1, branches 1; exits 1)"));
    }

    [Test]
    public void ViewBurstAsmOutput_LimitsLoopDetailsToSixteenRows()
    {
        var target = new BurstTarget("Example.Execute()", "Execute", "Example", "Example");
        var disassembly = new System.Text.StringBuilder();
        for (var index = 0; index < 17; ++index)
        {
            disassembly.AppendLine($".LBB0_{index}:");
            disassembly.AppendLine($"jne               .LBB0_{index}");
        }
        disassembly.AppendLine("ret");

        var output = BurstOutputFormatter.BuildOutput(target, disassembly.ToString());

        Assert.That(output, Does.Contain("- Natural loops: 17"));
        Assert.That(output, Does.Contain("- `L16` `.LBB0_15`"));
        Assert.That(output, Does.Not.Contain("- `L17` `.LBB0_16`"));
        Assert.That(output, Does.Contain("- 1 more loops omitted."));
    }

    [Test]
    public void ViewBurstAsmOutput_ReportsWasmControlMemoryAndSimdRoles()
    {
        var target = new BurstTarget("Example.Execute()", "Execute", "Example", "Example");
        var disassembly = string.Join("\n",
            ".Lloop:",
            "i32.load          0",
            "v128.load         0",
            "i32x4.add",
            "f32.div",
            "f32.sqrt",
            "i32.atomic.rmw.add 0",
            "atomic.fence      0",
            "br_if             .Lloop",
            "call_indirect     0",
            "end_function");

        var output = BurstOutputFormatter.BuildOutput(target, disassembly);

        Assert.That(output, Does.Contain("- Control flow: 1 conditional branch, 1 return"));
        Assert.That(output, Does.Not.Contain("Natural loops"));
        Assert.That(output, Does.Contain("- Calls: direct 0, indirect 1"));
        Assert.That(output, Does.Contain("- SIMD: packed compute 1, transfer 1; widest packed compute 128-bit"));
        Assert.That(output, Does.Contain("- Memory access instructions: 2 loads"));
        Assert.That(output, Does.Contain("- Notable opcode classes: divide 1, square root 1, atomic 1, fence 1"));
    }
}
