#nullable enable

using System;
using Conduit;
using NUnit.Framework;
using UnityEngine;

public sealed class BackgroundLogSummaryTests
{
    [TestCase("Index 1 outside length 4", "Index -900 outside length 80")]
    [TestCase("Value 1.25e-4 failed", "Value -2.5E+10 failed")]
    [TestCase("Pointer 0xDEADBEEF failed", "Pointer 0x01 failed")]
    [TestCase("Object 'alpha' failed", "Object 'beta' failed")]
    [TestCase("Object \"escaped \\\"value\" failed", "Object \"other\" failed")]
    [TestCase("Asset 7435d70d-7235-906c-51e8-9202ae2f9be7 failed", "Asset 00000000-0000-0000-0000-000000000000 failed")]
    [TestCase("Asset 7435d70d7235906c51e89202ae2f9be7 failed", "Asset abcdef0123456789abcdef0123456789 failed")]
    public void GroupsChangingValuesAndRetainsFirstExample(string first, string second)
    {
        var summary = new BackgroundLogSummary();
        summary.Record(first, "Game.Initial:Run ()", LogType.Error);
        summary.Record(second, "Game.Other:Run ()", LogType.Error);
        var text = summary.Format("/project/Editor.log");

        Assert.That(text, Does.Contain(first));
        Assert.That(text, Does.Not.Contain(second));
        Assert.That(text, Does.Contain("/project/Editor.log"));
    }

    [TestCase("error CS0001", "error CS0002")]
    [TestCase("Object Vector2 failed", "Object Vector3 failed")]
    [TestCase("First system failed", "Other system failed")]
    public void DistinguishesIdentifiersAndDiagnosticCodes(string first, string second)
    {
        Assert.That(LogMessageFingerprint.Compute(first), Is.Not.EqualTo(LogMessageFingerprint.Compute(second)));
    }

    [Test]
    public void SameMessageWithDifferentStacksKeepsFirstStack()
    {
        var summary = new BackgroundLogSummary();
        summary.Record("failure", "Game.Initial:Run ()", LogType.Error);
        summary.Record("failure", "Game.Other:Run ()", LogType.Error);
        var text = summary.Format("");
        Assert.That(text, Does.Contain("Game.Initial"));
        Assert.That(text, Does.Not.Contain("Game.Other"));
    }

    [Test]
    public void ShortenedQuietStacksIncludeFullLogPath()
    {
        var summary = new BackgroundLogSummary();
        var stack = "Game.Root:Run ()\n" + string.Concat(System.Linq.Enumerable.Repeat("Game.Caller:Run ()\n", 20));
        summary.Record("failure", stack, LogType.Error);
        var text = summary.Format("/project/Player.log");
        Assert.That(text, Does.Contain("Game.Root"));
        Assert.That(text, Does.Not.Contain(stack));
        Assert.That(text, Does.Contain("/project/Player.log"));
    }

    [Test]
    public void InfoNeverIncludesStacks()
    {
        var summary = new BackgroundLogSummary();
        summary.Record("information", "Game.Hidden:Run ()", LogType.Log);
        var text = summary.Format("");
        Assert.That(text, Does.Contain("information"));
        Assert.That(text, Does.Not.Contain("Game.Hidden"));
    }

    [Test]
    public void FirstErrorsDisplaceInfoEvenAfterInfoInspectionBudgetIsExhausted()
    {
        var summary = new BackgroundLogSummary();
        for (var index = 0; index < BackgroundLogSummary.InspectionLimit * 2; index++)
            summary.Record("info " + (char)('a' + index), "", LogType.Log);

        summary.Record("root cause", "Game.Root:Run ()", LogType.Exception);
        for (var index = 0; index < BackgroundLogSummary.InspectionLimit * 2; index++)
            summary.Record("cascade " + (char)('a' + index), "Game.Cascade:Run ()", LogType.Error);

        var text = summary.Format("/project/Editor.log");
        Assert.That(text, Does.Contain("root cause"));
        Assert.That(text, Does.Not.Contain("info "));
        Assert.That(text, Does.Not.Contain("Game.Cascade"));
        Assert.That(text, Does.Contain("/project/Editor.log"));
        Assert.That(text.Length, Is.LessThanOrEqualTo(BackgroundLogSummary.MaxOutputLength));
    }

    [Test]
    public void EarlierWarningsSurviveLaterWarningsAndLowerSeverityNoise()
    {
        var summary = new BackgroundLogSummary();
        for (var index = 0; index < BackgroundLogSummary.MaxGroups; index++)
            summary.Record("warning " + (char)('a' + index), "", LogType.Warning);
        summary.Record("late warning", "", LogType.Warning);
        summary.Record("information", "", LogType.Log);
        var text = summary.Format("/project/Editor.log");
        Assert.That(text, Does.Contain("warning a"));
        Assert.That(text, Does.Not.Contain("late warning"));
        Assert.That(text, Does.Not.Contain("information"));
    }

    [Test]
    public void HugeMessagesAndStacksHaveBoundedOutputWithFallbackPath()
    {
        var summary = new BackgroundLogSummary();
        var stack = string.Concat(System.Linq.Enumerable.Repeat("Game.Method:Run ()\n", 1000));
        for (var index = 0; index < BackgroundLogSummary.MaxGroups; index++)
            summary.Record((char)('a' + index) + new string('x', 10000), stack, LogType.Error);
        var text = summary.Format("/project/Editor.log");
        Assert.That(text.Length, Is.LessThanOrEqualTo(BackgroundLogSummary.MaxOutputLength));
        Assert.That(text, Does.Contain("/project/Editor.log"));
        Assert.That(text, Does.Not.Contain("Game.Method"));
    }

    [Test]
    public void RepeatAndBudgetExhaustionPathsDoNotAllocate()
    {
        var summary = new BackgroundLogSummary();
        const string message = "Index 3 outside length 2";
        summary.Record(message, "", LogType.Error);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < BackgroundLogSummary.InspectionLimit * 10; index++)
            summary.Record(message, "", LogType.Error);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(allocated, Is.Zero);
    }
}
