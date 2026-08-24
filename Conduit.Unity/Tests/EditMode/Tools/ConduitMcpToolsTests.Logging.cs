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
            BridgeExceptionFormatter.SimplifyStackTrace(stackTrace),
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
            ToolLogFormatter.CleanCapturedStackTrace(BridgeCommandKind.Show, stackTrace, LogType.Warning),
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
            ToolLogFormatter.CleanCapturedStackTrace(BridgeCommandKind.ExecuteCode, stackTrace, LogType.Log),
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
            ToolLogFormatter.CleanCapturedStackTrace(BridgeCommandKind.ExecuteCode, stackTrace, LogType.Warning),
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
            "Conduit.ExecuteCodeTool:InvokeAsync",
            "Conduit.ExecuteCodeTool:ExecuteCachedCompilationAsync");

        Assert.That(
            ToolLogFormatter.CleanCapturedStackTrace(BridgeCommandKind.ExecuteCode, stackTrace, LogType.Warning),
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
            ToolLogFormatter.CleanCapturedStackTrace(BridgeCommandKind.Show, stackTrace, LogType.Warning),
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
            ToolLogFormatter.CleanCapturedStackTrace(BridgeCommandKind.ExecuteCode, stackTrace, LogType.Warning),
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
            ToolLogFormatter.CleanCapturedStackTrace(BridgeCommandKind.Show, stackTrace, LogType.Log),
            Is.Null);
    }

    [Test]
    public void LogCapture_SeparatesQuotedMessageFromStackAndRepeatCount()
    {
        string formatted = ToolLogFormatter.FormatCapturedLogEntryForTest(
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

        Assert.That(ToolLogFormatter.ShouldOmitDiagnosticLogEntry(logMessage, diagnostic), Is.True);
    }

    [Test]
    public void LogCapture_KeepsNonCompilerMessages()
    {
        const string logMessage = "Failed to import Assets/Scripts/Foo.cs";
        const string diagnostic = "Failed to import Assets/Scripts/Foo.cs";

        Assert.That(ToolLogFormatter.ShouldOmitDiagnosticLogEntry(logMessage, diagnostic), Is.False);
    }

    [Test]
    public void LogCapture_TestRunLogPolicyKeepsFullLogsForSmallRuns()
    {
        Assert.That(RunTestsTool.ShouldIncludeAllTestLogs(0), Is.True);
        Assert.That(RunTestsTool.ShouldIncludeAllTestLogs(1), Is.True);
        Assert.That(RunTestsTool.ShouldIncludeAllTestLogs(3), Is.True);
        Assert.That(RunTestsTool.ShouldIncludeAllTestLogs(4), Is.False);
        Assert.That(RunTestsTool.LargeTestRunLogNote, Is.EqualTo("*Non-error logs are omitted when more than 3 tests run.*"));

        Assert.That(ToolLogFormatter.ShouldIncludeTestLogEntry(LogType.Log, includeAllLogs: true), Is.True);
        Assert.That(ToolLogFormatter.ShouldIncludeTestLogEntry(LogType.Warning, includeAllLogs: true), Is.True);
        Assert.That(ToolLogFormatter.ShouldIncludeTestLogEntry(LogType.Error, includeAllLogs: false), Is.True);
        Assert.That(ToolLogFormatter.ShouldIncludeTestLogEntry(LogType.Assert, includeAllLogs: false), Is.True);
        Assert.That(ToolLogFormatter.ShouldIncludeTestLogEntry(LogType.Exception, includeAllLogs: false), Is.True);
        Assert.That(ToolLogFormatter.ShouldIncludeTestLogEntry(LogType.Log, includeAllLogs: false), Is.False);
        Assert.That(ToolLogFormatter.ShouldIncludeTestLogEntry(LogType.Warning, includeAllLogs: false), Is.False);
    }

    [Test]
    public void LogCapture_DropsIgnoredBurstWarning()
    {
        Assert.That(
            ToolLogFormatter.ShouldSuppressCapturedLogEntry(
                "/home/apk/src/hk2/Assets/Foo.cs(164,17): Burst warning BC1371: A discarded call is irrelevant."),
            Is.True);
        Assert.That(
            ToolLogFormatter.ShouldSuppressCapturedLogEntry(
                "Burst warning BC1371: A discarded call is irrelevant."),
            Is.True);
        Assert.That(
            ToolLogFormatter.ShouldSuppressCapturedLogEntry(
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

        var output = ToolLogFormatter.NormalizeCapturedLogMessage(input);

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
            ToolLogFormatter.NormalizeCapturedLogMessage(
                "Burst error BC1000: While compiling job:\n" +
                $"Example.Job::Execute(System.Int32&, {mscorlib}|System.Single&, {mscorlib}|System.Boolean, {mscorlib})"),
            Does.Contain("Job::Execute(ref int, ref float, bool)"));

        Assert.That(
            ToolLogFormatter.NormalizeCapturedLogMessage(
                "Burst error BC1001: While compiling job:\n" +
                "Unity.Jobs.IJobExtensions+JobStruct`1" +
                $"[[Foo.Namespace.MyJob, {gameAssembly}]], {coreAssembly}" +
                "::Execute(" +
                "Foo.Namespace.NativeBox`1" +
                $"[[Foo.Namespace.MyValue, {gameAssembly}]], {gameAssembly}&, {gameAssembly}" +
                $"|System.Int32, {mscorlib})"),
            Does.Contain("IJobExtensions+JobStruct<MyJob>::Execute(ref NativeBox<MyValue>, int)"));

        Assert.That(
            ToolLogFormatter.NormalizeCapturedLogMessage(
                "Burst error BC1002: While compiling job:\n" +
                "Example.Runner::Execute(Outer<Inner<int,float>>&|Unity.Mathematics.float3|System.IntPtr)"),
            Does.Contain("Runner::Execute(ref Outer<Inner<int,float>>, float3, nint)"));

        Assert.That(
            ToolLogFormatter.NormalizeCapturedLogMessage(
                "Burst error BC1003: While compiling job:\n" +
                "First.Call(System.Int32&|System.Boolean) then Second.Call(Unity.Mathematics.bool3&|System.UInt64)"),
            Does.Contain("Call(ref int, bool) then Call(ref bool3, ulong)"));

        Assert.That(
            ToolLogFormatter.NormalizeCapturedLogMessage("Burst error BC1004: message contains (left|right) as text"),
            Does.Contain("message contains (left|right) as text"));
    }
}
