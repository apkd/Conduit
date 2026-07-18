#nullable enable

using System;
using System.Globalization;
using System.Linq;
using Conduit;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed class ConduitToolUsageTests
{
    bool hadProjectData;
    bool hadAllProjectsData;
    bool hadEnabledPreference;
    string projectData = string.Empty;
    string allProjectsData = string.Empty;
    bool enabledPreference;
    string? restartEnvironmentValue;

    [SetUp]
    public void SetUp()
    {
        hadProjectData = PlayerPrefs.HasKey(ConduitToolUsage.ProjectDataPreferenceKey);
        hadAllProjectsData = EditorPrefs.HasKey(ConduitToolUsage.AllProjectsDataPreferenceKey);
        hadEnabledPreference = EditorPrefs.HasKey(ConduitToolUsage.EnabledPreferenceKey);
        projectData = PlayerPrefs.GetString(ConduitToolUsage.ProjectDataPreferenceKey, string.Empty);
        allProjectsData = EditorPrefs.GetString(ConduitToolUsage.AllProjectsDataPreferenceKey, string.Empty);
        enabledPreference = ConduitToolUsage.Enabled;
        restartEnvironmentValue = Environment.GetEnvironmentVariable(
            ConduitToolUsage.RestartStartedUtcTicksEnvironmentVariable
        );

        PlayerPrefs.DeleteKey(ConduitToolUsage.ProjectDataPreferenceKey);
        EditorPrefs.DeleteKey(ConduitToolUsage.AllProjectsDataPreferenceKey);
        EditorPrefs.SetBool(ConduitToolUsage.EnabledPreferenceKey, true);
        Environment.SetEnvironmentVariable(
            ConduitToolUsage.RestartStartedUtcTicksEnvironmentVariable,
            null
        );
    }

    [TearDown]
    public void TearDown()
    {
        RestorePlayerPreference(
            ConduitToolUsage.ProjectDataPreferenceKey,
            hadProjectData,
            projectData
        );
        RestoreEditorPreference(
            ConduitToolUsage.AllProjectsDataPreferenceKey,
            hadAllProjectsData,
            allProjectsData
        );
        if (hadEnabledPreference)
            EditorPrefs.SetBool(ConduitToolUsage.EnabledPreferenceKey, enabledPreference);
        else
            EditorPrefs.DeleteKey(ConduitToolUsage.EnabledPreferenceKey);

        Environment.SetEnvironmentVariable(
            ConduitToolUsage.RestartStartedUtcTicksEnvironmentVariable,
            restartEnvironmentValue
        );
        PlayerPrefs.Save();
    }

    [Test]
    public void Enabled_DefaultsToTrue()
    {
        EditorPrefs.DeleteKey(ConduitToolUsage.EnabledPreferenceKey);

        Assert.That(ConduitToolUsage.Enabled, Is.True);
    }

    [Test]
    public void ToolList_IncludesEveryTrackedToolOnceInDisplayOrder()
    {
        var declaredTools = typeof(BridgeCommandTypes)
            .GetFields()
            .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(static field => (string)field.GetRawConstantValue()!)
            .Where(static toolName => toolName != BridgeCommandTypes.Help)
            .ToArray();

        Assert.That(
            ConduitToolUsage.ToolNames,
            Is.Ordered.Using<string>(StringComparer.Ordinal)
        );
        Assert.That(ConduitToolUsage.ToolNames, Is.Unique);
        Assert.That(ConduitToolUsage.ToolNames, Is.EquivalentTo(declaredTools));
    }

    [Test]
    public void RecordDuration_UpdatesProjectAndAllProjectOnlineAverages()
    {
        ConduitToolUsage.RecordDuration(BridgeCommandTypes.Show, 10d);
        ConduitToolUsage.RecordDuration(BridgeCommandTypes.Show, 20d);
        ConduitToolUsage.RecordDuration(BridgeCommandTypes.Show, 30d);

        AssertRecord(ConduitToolUsage.GetProjectData(), BridgeCommandTypes.Show, 3L, 20d);
        AssertRecord(ConduitToolUsage.GetAllProjectsData(), BridgeCommandTypes.Show, 3L, 20d);
    }

    [Test]
    public void DisabledTracking_DoesNotStartCalls()
    {
        ConduitToolUsage.Enabled = false;

        Assert.That(ConduitToolUsage.BeginCall(BridgeCommandTypes.Search), Is.Zero);
    }

    [Test]
    public void RestartEnvironment_CompletesCallAfterProcessBoundary()
    {
        Environment.SetEnvironmentVariable(
            ConduitToolUsage.RestartStartedUtcTicksEnvironmentVariable,
            DateTime.UtcNow
                .AddMilliseconds(-10d)
                .Ticks
                .ToString(CultureInfo.InvariantCulture)
        );

        ConduitToolUsage.CompleteRestartFromEnvironment();

        AssertRecord(
            ConduitToolUsage.GetProjectData(),
            BridgeCommandTypes.Restart,
            1L,
            minimumAverage: 0d
        );
        Assert.That(
            Environment.GetEnvironmentVariable(
                ConduitToolUsage.RestartStartedUtcTicksEnvironmentVariable
            ),
            Is.Null
        );
    }

    [Test]
    public void DeleteAllStoredData_ClearsBothViews()
    {
        ConduitToolUsage.RecordDuration(BridgeCommandTypes.ExecuteCode, 12d);

        ConduitToolUsage.DeleteAllStoredData();

        AssertRecord(ConduitToolUsage.GetProjectData(), BridgeCommandTypes.ExecuteCode, 0L, 0d);
        AssertRecord(ConduitToolUsage.GetAllProjectsData(), BridgeCommandTypes.ExecuteCode, 0L, 0d);
    }

    static void AssertRecord(
        ToolUsageRecord[] records,
        string toolName,
        long expectedCount,
        double expectedAverage = 0d,
        double? minimumAverage = null
    )
    {
        var record = records.Single(item => item.ToolName == toolName);
        Assert.That(record.CallCount, Is.EqualTo(expectedCount));
        Assert.That(
            record.AverageDurationMilliseconds,
            minimumAverage.HasValue
                ? Is.GreaterThanOrEqualTo(minimumAverage.Value)
                : Is.EqualTo(expectedAverage).Within(0.0001d)
        );
    }

    static void RestorePlayerPreference(string key, bool hadValue, string value)
    {
        if (hadValue)
            PlayerPrefs.SetString(key, value);
        else
            PlayerPrefs.DeleteKey(key);
    }

    static void RestoreEditorPreference(string key, bool hadValue, string value)
    {
        if (hadValue)
            EditorPrefs.SetString(key, value);
        else
            EditorPrefs.DeleteKey(key);
    }
}
