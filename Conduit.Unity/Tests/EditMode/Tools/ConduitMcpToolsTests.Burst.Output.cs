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
    public void ViewBurstAsmOutput_IncludesCompilationContextAndDeduplicatedRemarks()
    {
        var target = new BurstTarget("Example.Execute()", "Execute", "Example", "Example");
        const string options = "--target=ARMV9A\n--opt-level=3\n--float-mode=Fast\n--float-precision=Low\n--disable-safety-checks";
        const string remark =
            "--- !Analysis\n" +
            "Remark Type: Analysis\n" +
            "Pass: loop-vectorize\n" +
            "Remark: CantVectorizeInstructionReturnType\n" +
            "Function: Example.Execute\n" +
            "Message: /src/Example.cs:42:0: loop not vectorized: instruction return type cannot be vectorized";
        var remarks = remark + "\n" + remark;

        var output = BurstOutputFormatter.BuildOutput(
            target,
            "ret",
            BurstCompilationParser.ParseCompilationContext(options, "armv9"),
            remarks
        );

        Assert.That(output, Does.Contain("**Compilation:** `armv9/ARMV9A` · `Performance` · floats `Fast/Low` · safety checks `Off`"));
        Assert.That(output, Does.Contain("`loop-vectorize/CantVectorizeInstructionReturnType` · `Example.cs:42`"));
        Assert.That(output, Does.Not.Contain("function `Example.Execute`"));
        Assert.That(output.Split(new[] { "instruction return type cannot be vectorized" }, StringSplitOptions.None), Has.Length.EqualTo(2));
    }

    [Test]
    public void ViewBurstAsmOutput_IncludesEveryDistinctOptimizationRemark()
    {
        var target = new BurstTarget("Example.Execute()", "Execute", "Example", "Example");
        var remarks = new System.Text.StringBuilder();
        for (var index = 0; index < 12; ++index)
        {
            remarks.AppendLine("--- !Analysis");
            remarks.AppendLine("Remark Type: Analysis");
            remarks.AppendLine("Pass: loop-vectorize");
            remarks.AppendLine($"Function: Example.Helper{index}");
            remarks.AppendLine($"Message: remark {index}");
        }

        var output = BurstOutputFormatter.BuildOutput(
            target,
            "ret",
            default,
            remarks.ToString()
        );

        Assert.That(output, Does.Contain("function `Helper11` — remark 11"));
        Assert.That(output, Does.Not.Contain("compiler remarks omitted"));
    }

    [Test]
    public void ViewBurstAsmOutput_SavesLargeOutputToTempFile()
    {
        var target = new BurstTarget("Example.GenerateInteriorMesh()", "GenerateInteriorMesh", "", "");
        var path = Path.Combine("Temp", "Conduit", "Burst", "GenerateInteriorMesh.txt");
        try
        {
            var builder = new System.Text.StringBuilder();
            for (var i = 0; i < 1000; i++)
            {
                if (i > 0)
                    builder.Append('\n');

                builder.Append("nop");
            }

            var result = BurstOutputFormatter.CompleteOutput(target, builder.ToString());

            Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
            Assert.That(result.return_value, Does.StartWith("# Summary\n\n**Function:** `GenerateInteriorMesh()`\n\n- Instructions: 1000"));
            Assert.That(result.return_value, Does.Contain("- Top instructions: nop=1000"));
            Assert.That(result.return_value, Does.Contain("Assembly output very large ("));
            Assert.That(result.return_value, Does.Not.Contain("\n\n---\n\n"));
            Assert.That(result.return_value, Does.EndWith(" KB); saved to `Temp/Conduit/Burst/GenerateInteriorMesh.txt`.*"));
            Assert.That(File.Exists(path), Is.True);
            Assert.That(File.ReadAllText(path), Does.StartWith("# Summary\n\n**Function:** `GenerateInteriorMesh()`\n\n- Instructions: 1000"));
            Assert.That(File.ReadAllText(path), Does.Contain("\n\n# Asm\n\n```asm\nnop\nnop"));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [TestCase("cil", ".il", "CIL")]
    [TestCase("llvmir", ".ll", "Optimized LLVM IR")]
    public void ViewBurstAsmOutput_SavesLargeRawCompilerOutputWithoutMarkdown(
        string selector,
        string extension,
        string displayName)
    {
        var target = new BurstTarget("Example.GenerateInteriorMesh()", "GenerateInteriorMesh", "", "");
        var path = Path.Combine("Temp", "Conduit", "Burst", "GenerateInteriorMesh" + extension);
        Assert.That(ViewBurstAsmTool.TryParseOutputTarget(selector, out var outputTarget), Is.True);
        try
        {
            var builder = new System.Text.StringBuilder();
            for (var i = 0; i < 1000; ++i)
                builder.AppendLine("nop");

            var rawOutput = builder.ToString().TrimEnd();
            var result = BurstOutputFormatter.CompleteRawOutput(target, rawOutput, outputTarget);

            Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
            Assert.That(result.return_value, Does.Contain($"{displayName} output very large ("));
            Assert.That(result.return_value, Does.EndWith($" KB); saved to `{path.Replace('\\', '/')}`.*"));
            Assert.That(File.ReadAllText(path), Is.EqualTo(rawOutput));
            Assert.That(File.ReadAllText(path), Does.Not.StartWith("# `"));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
