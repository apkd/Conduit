#nullable enable

using System;
using System.Linq;
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
        var text = summary.Format();

        Assert.That(text, Does.Contain(first));
        Assert.That(text, Does.Not.Contain(second));
        Assert.That(text, Does.Contain("(×2 similar)"));
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
        var text = summary.Format();
        Assert.That(text, Does.Contain("Game.Initial"));
        Assert.That(text, Does.Not.Contain("Game.Other"));
    }

    [Test]
    public void ShortenedStacksMarkOmittedFramesInline()
    {
        var summary = new BackgroundLogSummary();
        var stack = "Game.Root:Run ()\n" + string.Concat(System.Linq.Enumerable.Repeat("Game.Caller:Run ()\n", 20));
        summary.Record("failure", stack, LogType.Error);
        var text = summary.Format();
        Assert.That(text, Does.Contain("\n  Game.Root:Run\n  Game.Caller:Run…"));
        Assert.That(text, Does.Not.Contain(stack));
        Assert.That(text, Does.EndWith("…"));
    }

    [Test]
    public void InfoNeverIncludesStacks()
    {
        var summary = new BackgroundLogSummary();
        summary.Record("information", "Game.Hidden:Run ()", LogType.Log);
        var text = summary.Format();
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

        var text = summary.Format();
        Assert.That(text, Does.Contain("root cause"));
        Assert.That(text, Does.Not.Contain("info "));
        Assert.That(text, Does.Contain("Game.Root"));
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
        var text = summary.Format();
        Assert.That(text, Does.Contain("warning a"));
        Assert.That(text, Does.Not.Contain("late warning"));
        Assert.That(text, Does.Not.Contain("information"));
    }

    [Test]
    public void HugeMessagesAndStacksStayBounded()
    {
        var summary = new BackgroundLogSummary();
        var stack = string.Concat(System.Linq.Enumerable.Repeat("Game.Method:Run ()\n", 1000));
        for (var index = 0; index < BackgroundLogSummary.MaxGroups; index++)
            summary.Record((char)('a' + index) + new string('x', 10000), stack, LogType.Error);
        var text = summary.Format();
        Assert.That(text.Length, Is.LessThanOrEqualTo(BackgroundLogSummary.MaxOutputLength));
        Assert.That(text, Does.Contain("Game.Method"));
        Assert.That(text, Does.Contain("more events omitted."));
    }

    [Test]
    public void FormatsLevelsChronologicallyAndPreservesSourceTags()
    {
        var summary = new BackgroundLogSummary();
        summary.Record("[Fixture:27] initialized", "", LogType.Log);
        summary.Record("target missing", "Game.Combat:Strike ()\nGame.Agent:Tick ()", LogType.Exception);
        summary.Record("navigation unavailable", "", LogType.Warning);

        Assert.That(summary.Format(), Is.EqualTo(
            "> [Fixture:27] initialized\n"
            + "> [ERROR] target missing\n"
            + "  Game.Combat:Strike\n"
            + "  Game.Agent:Tick\n"
            + "> [WARN] navigation unavailable"
        ));
    }

    [Test]
    public void WarningsRetainStacksInBusySummaries()
    {
        var summary = new BackgroundLogSummary();
        var names = new[] { "Alpha", "Beta", "Gamma", "Delta" };
        foreach (var name in names)
            summary.Record(name, $"Game.{name}:Run ()", LogType.Warning);

        var text = summary.Format();
        foreach (var name in names)
            Assert.That(text, Does.Contain($"\n  Game.{name}:Run"));
    }

    [Test]
    public void CollectsConfiguredNumberOfDistinctGroups()
    {
        var summary = new BackgroundLogSummary();
        for (var index = 0; index < BackgroundLogSummary.MaxGroups; index++)
            summary.Record("message " + (char)('a' + index), "", LogType.Log);

        var lines = summary.Format().Split('\n');
        Assert.That(lines.Length, Is.EqualTo(BackgroundLogSummary.MaxGroups));
        Assert.That(lines.All(static line => line.StartsWith("> message ", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void OutputLimitOmitsWholeGroupsAndCountsTheirEvents()
    {
        var summary = new BackgroundLogSummary();
        for (var index = 0; index < BackgroundLogSummary.MaxGroups; index++)
        {
            var message = (char)('a' + index) + new string('x', BackgroundLogSummary.MaxOutputLength);
            summary.Record(message, "", LogType.Log);
            summary.Record(message, "", LogType.Log);
        }

        var text = summary.Format();
        var lines = text.Split('\n');
        var messages = lines.Where(static line => line.StartsWith("> ", StringComparison.Ordinal)).ToArray();
        var omitted = (BackgroundLogSummary.MaxGroups - messages.Length) * 2;
        Assert.That(text.Length, Is.LessThanOrEqualTo(BackgroundLogSummary.MaxOutputLength));
        Assert.That(messages, Is.Not.Empty);
        Assert.That(messages.All(static line => line.EndsWith("… (×2 similar)", StringComparison.Ordinal)), Is.True);
        Assert.That(lines[^1], Is.EqualTo($"{omitted} more events omitted."));
    }

    [Test]
    public void OutputLimitKeepsLateErrorsAndDisplaysSelectedGroupsChronologically()
    {
        var summary = new BackgroundLogSummary();
        for (var index = 0; index < BackgroundLogSummary.MaxGroups - 1; index++)
            summary.Record((char)('a' + index) + new string('x', BackgroundLogSummary.MaxOutputLength), "", LogType.Log);
        summary.Record("root cause", "Game.Root:Run ()", LogType.Error);

        var text = summary.Format();
        Assert.That(text, Does.Contain("> [ERROR] root cause\n  Game.Root:Run"));
        Assert.That(text, Does.StartWith("> a"));
        Assert.That(text.Length, Is.LessThanOrEqualTo(BackgroundLogSummary.MaxOutputLength));
    }

    [Test]
    public void OneOmittedEventUsesSingularNotice()
    {
        var summary = new BackgroundLogSummary();
        for (var index = 0; index < BackgroundLogSummary.MaxGroups; index++)
            summary.Record("message " + (char)('a' + index), "", LogType.Log);
        summary.Record("late message", "", LogType.Log);

        Assert.That(summary.Format(), Does.EndWith("1 more event omitted."));
    }

    [Test]
    public void ShortenedMessagesUseAnEllipsisWithoutAFooter()
    {
        var summary = new BackgroundLogSummary();
        summary.Record(new string('x', BackgroundLogSummary.MaxOutputLength), "", LogType.Log);

        var text = summary.Format();
        Assert.That(text, Does.EndWith("…"));
        Assert.That(text, Does.Not.Contain("\n"));
    }

    [Test]
    public void EmptySummaryIsEmpty()
    {
        var summary = new BackgroundLogSummary();
        Assert.That(summary.Format(), Is.Empty);
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
