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
    public void Status_IncludesTestRunnerStateInSnapshot()
    {
        var snapshot = StatusTool.Status();

        Assert.That(snapshot, Does.Contain("\"is_test_runner_active\""));
        Assert.That(snapshot, Does.Contain("\"active_test_mode\""));
    }

    [Test]
    public void TestRunBusyGuard_BlocksCompilingUpdatingAndPlayModeTransition()
    {
        Assert.That(RunTestsTool.ShouldBlockTestRun(true, false, false), Is.True);
        Assert.That(RunTestsTool.ShouldBlockTestRun(false, true, false), Is.True);
        Assert.That(RunTestsTool.ShouldBlockTestRun(false, false, true), Is.True);
        Assert.That(RunTestsTool.ShouldBlockTestRun(false, false, false), Is.False);

        var diagnostic = RunTestsTool.BuildBlockedTestRunDiagnostic(BridgeCommandTypes.RunTestsPlayMode, true, true, true);
        Assert.That(diagnostic, Is.EqualTo(
            "Cannot start 'run_tests_playmode' while Unity is busy: compiling scripts, updating assets, changing play mode."));
    }

    [Test]
    public void TestRunCompileErrorGuard_BlocksWhenCompilationHasFailed()
    {
        Assert.That(RunTestsTool.ShouldFailTestRunForCompileErrors(true), Is.True);
        Assert.That(RunTestsTool.ShouldFailTestRunForCompileErrors(false), Is.False);

        var diagnostic = RunTestsTool.BuildCompileErrorTestRunDiagnostic(BridgeCommandTypes.RunTestsPlayMode);
        Assert.That(diagnostic, Is.EqualTo("The project has compilation errors."));
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

    [Test]
    public void UserStoppedPlayModeTestRun_MapsToCancelledDiagnosticWithoutException()
    {
        var matched = RunTestsTool.TryCreateUserStoppedPlayModeTestRunResult(
            "Exception: Playmode tests were aborted because the player was stopped.\nUnityEditor.TestTools.TestRunner.TestRun.Tasks.PlayModeRunTask",
            true,
            out var result);

        Assert.That(matched, Is.True);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.outcome, Is.EqualTo(ToolOutcome.Cancelled));
        Assert.That(result.diagnostic, Is.EqualTo("The user has manually stopped the play mode test run."));
        Assert.That(result.exception, Is.Null);

        matched = RunTestsTool.TryCreateUserStoppedPlayModeTestRunResult(
            "Playmode tests were aborted because the player was stopped.",
            false,
            out result);

        Assert.That(matched, Is.False);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void CancelledTestResultState_DetectsUnityCancelledLabels()
    {
        Assert.That(RunTestsTool.IsCancelledResultState("Failed:Cancelled"), Is.True);
        Assert.That(RunTestsTool.IsCancelledResultState("Cancelled"), Is.True);
        Assert.That(RunTestsTool.IsCancelledResultState("Failed:Error"), Is.False);
        Assert.That(RunTestsTool.IsCancelledResultState(null), Is.False);
    }

    [Test]
    public void TestFilterRegex_UsesSubstringMatchByDefaultAndSupportsGlobTokens()
    {
        Assert.That(RunTestsTool.BuildTestNameRegexPattern("Resolve"), Is.EqualTo("^.*Resolve.*$"));
        Assert.That(RunTestsTool.BuildTestNameRegexPattern("Resolve_*"), Is.EqualTo("^Resolve_.*$"));
        Assert.That(RunTestsTool.BuildTestNameRegexPattern("Foo?Bar"), Is.EqualTo("^Foo.Bar$"));
    }

    [Test]
    public void TestCompletionSummary_ReportsMixedPassAndSkipCountsWithTheSkipReason()
    {
        var summary = RunTestsTool.BuildCompletionCategorySummary(
            passCount: 8,
            failCount: 0,
            skipCount: 3,
            inconclusiveCount: 0,
            skippedMessage: "OneTimeSetUp: bridge client attached."
        );

        Assert.That(
            summary,
            Is.EqualTo(
                "Passed 8 tests. Skipped 3 tests.\n\nOneTimeSetUp: bridge client attached."
            )
        );
    }

    [Test]
    public void FilteredTestDiagnostic_IncludesStartedTests()
    {
        try
        {
            RunTestsTool.ResetState();
            RunTestsTool.SetActiveFilterPattern("*Resolve*");
            RunTestsTool.RecordStartedFilteredTestLabel("ConduitMcpToolsTests.Resolve_TracksMatchSource");
            RunTestsTool.RecordStartedFilteredTestLabel("ConduitMcpToolsTests.Resolve_AcceptsWhitespaceAfterExactObjectIdPrefix");

            var diagnostic = RunTestsTool.BuildFilteredTestRunDiagnostic("Passed 2 tests.");
            Assert.That(diagnostic, Does.StartWith("Passed 2 tests."));
            Assert.That(diagnostic, Does.Contain("RAN TESTS:"));
            Assert.That(diagnostic, Does.Contain("ConduitMcpToolsTests.Resolve_TracksMatchSource"));
            Assert.That(diagnostic, Does.Contain("ConduitMcpToolsTests.Resolve_AcceptsWhitespaceAfterExactObjectIdPrefix"));
        }
        finally
        {
            RunTestsTool.ResetState();
        }
    }
}
