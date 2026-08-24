#nullable enable

using System;
using System.Linq;
using Conduit;
using NUnit.Framework;
using UnityEngine;

public sealed class ProjectSettingsToolTests
{
    static string? emptyValue = "initial";
    static string alphaValue = "alpha";
    static string alphabetValue = "alphabet";

    [SetUp]
    public void SetUp()
    {
        emptyValue = "initial";
        alphaValue = "alpha";
        alphabetValue = "alphabet";
        OperationPersistence.ClearActiveOperation();
    }

    [TearDown]
    public void TearDown() => OperationPersistence.ClearActiveOperation();

    [TestCase("TestProjectSettings.EmptyValue")]
    [TestCase("test project settings empty value")]
    [TestCase("testprojectsettingsemptyvalue")]
    public void Get_MatchesCaseAndSeparatorVariants(string key)
    {
        string result = Execute("get", key);

        Assert.That(result, Is.EqualTo("test_project_settings.empty_value = initial"));
    }

    [Test]
    public void Set_WritesAnEmptyString()
    {
        string result = Execute("set", "test_project_settings.empty_value", string.Empty);

        Assert.That(emptyValue, Is.EqualTo(string.Empty));
        Assert.That(result, Is.EqualTo("Set test_project_settings.empty_value: initial -> \"\""));
    }

    [Test]
    public void Set_UsesNullForClearingAndJsonQuotesForTheLiteralNullString()
    {
        string cleared = Execute("set", "test_project_settings.empty_value");
        Assert.That(emptyValue, Is.Null);
        Assert.That(cleared, Is.EqualTo("Set test_project_settings.empty_value: initial -> null"));

        OperationPersistence.ClearActiveOperation();
        string literal = Execute("set", "test_project_settings.empty_value", "\"null\"");
        Assert.That(emptyValue, Is.EqualTo("null"));
        Assert.That(literal, Is.EqualTo("Set test_project_settings.empty_value: null -> \"null\""));
    }

    [Test]
    public void AmbiguousGetShowsValuesAndAmbiguousSetDoesNotChangeAnything()
    {
        string read = Execute("get", "test_project_settings.alph");
        Assert.That(read, Does.StartWith("Found 2 project settings"));
        Assert.That(read, Does.Contain("test_project_settings.alpha = alpha"));
        Assert.That(read, Does.Contain("test_project_settings.alphabet = alphabet"));

        string write = Execute("set", "test_project_settings.alph", "changed");
        Assert.That(write, Does.StartWith("Cannot set 'test_project_settings.alph' because it matches 2"));
        Assert.That(alphaValue, Is.EqualTo("alpha"));
        Assert.That(alphabetValue, Is.EqualTo("alphabet"));
    }

    [Test]
    public void DuplicateCanonicalKeyFailsWhenAccessed()
    {
        Assert.Throws<InvalidOperationException>(
            () => Execute("get", "test_project_settings.duplicate")
        );
        var exception = Assert.Throws<InvalidOperationException>(
            () => Execute("set", "test_project_settings.duplicate", "changed")
        );

        Assert.That(exception!.Message, Does.Contain("registered 2 times"));
    }

    [Test]
    public void ReadOnlySettingRejectsSet()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => Execute(
                "set",
                "test_project_settings.read_only",
                "changed"
            )
        );

        Assert.That(exception!.Message, Does.Contain("read-only"));
    }

    [Test]
    public void EmptyGetListsOnlyTopLevelGroups()
    {
        var registry = CreateListingRegistry();

        string result = Execute(registry, "get", string.Empty);

        Assert.That(
            result,
            Is.EqualTo("Found 2 project settings groups:\nlarge_group\nother_group")
        );
    }

    [Test]
    public void ExactGroupGetListsEverySettingWithoutARowLimit()
    {
        var registry = CreateListingRegistry();

        string result = Execute(registry, "get", "largegroup");

        Assert.That(result, Does.StartWith("Found 40 project settings in 'large_group':"));
        Assert.That(result, Does.Not.Contain("showing 32"));
        Assert.That(result.Split('\n'), Has.Length.EqualTo(41));
        Assert.That(result, Does.Contain("large_group.setting_39 = 39"));
    }

    [Test]
    public void PartialResultsRemainCappedAtThirtyTwoEntries()
    {
        var registry = CreateListingRegistry();

        string read = Execute(registry, "get", "large");
        Assert.That(read, Does.Contain("(showing 32):"));
        Assert.That(read.Split('\n'), Has.Length.EqualTo(33));

        string write = Execute(registry, "set", "large", "true");
        Assert.That(write, Does.Contain("Use a more specific key (showing 32):"));
        Assert.That(write.Split('\n'), Has.Length.EqualTo(33));
    }

    [Test]
    public void MatcherCombinesTokenAndSeparatorFreeMatches()
    {
        var registry = new ProjectSettingsRegistry();
        registry.Add("quality_settings.quality_levels.pc.async_asset_upload.time_slice", () => 2);
        registry.Add("graphics_settings.log_shader_compilation", () => false);
        registry.Add("foo_settings.bar_value", () => 1);
        registry.Add("other.foobar_option", () => 2);

        Assert.That(
            ProjectSettingKey.Canonicalize("HDRPDefaultSettings"),
            Is.EqualTo("hdrp_default_settings")
        );
        Assert.That(
            ProjectSettingMatcher.Match(
                    registry.Settings,
                    "QUALITYSettings.QualityLevels.PC.AsyncAssetUpload.TimeSlice"
                )
                .Single().Key,
            Is.EqualTo("quality_settings.quality_levels.pc.async_asset_upload.time_slice")
        );
        Assert.That(
            ProjectSettingMatcher.Match(registry.Settings, "qual pc async time").Single().Key,
            Is.EqualTo("quality_settings.quality_levels.pc.async_asset_upload.time_slice")
        );
        Assert.That(
            ProjectSettingMatcher.Match(
                    registry.Settings,
                    "graphics settings log shader compilation"
                )
                .Single().Key,
            Is.EqualTo("graphics_settings.log_shader_compilation")
        );
        Assert.That(
            ProjectSettingMatcher.Match(registry.Settings, "Foo Bar")
                .Select(setting => setting.Key),
            Is.EqualTo(new[] { "foo_settings.bar_value", "other.foobar_option" })
        );
    }

    [Test]
    public void PersistencePreservesOperationEmptyValueAndPreviousValue()
    {
        OperationPersistence.SaveActiveOperation(
            new PendingOperationState
            {
                RequestID = "project-settings-persistence",
                CommandType = BridgeCommandTypes.ProjectSettings,
                Target = "test_project_settings.empty_value",
                Snippet = string.Empty,
                Args = new[] { "set" },
                ProjectSettingPrevious = "before",
            },
            BridgeCommandKind.ProjectSettings
        );

        var restored = OperationPersistence.RestoreActiveOperation();
        Assert.That(restored, Is.Not.Null);
        Assert.That(restored!.Snippet, Is.EqualTo(string.Empty));
        Assert.That(restored.Args, Is.EqualTo(new[] { "set" }));
        Assert.That(restored.ProjectSettingPrevious, Is.EqualTo("before"));
        Assert.That(restored.IsRestored, Is.True);
    }

    [Test]
    public void RestoredSetConfirmsTheAppliedValueWithoutReplayingIt()
    {
        alphaValue = "already_changed";

        string result = ProjectSettingsTool.Execute(
            new PendingOperationState
            {
                RequestID = "restored-project-settings",
                CommandType = BridgeCommandTypes.ProjectSettings,
                Target = "test_project_settings.alpha",
                Snippet = "replayed_value",
                Args = new[] { "set" },
                ProjectSettingPrevious = "alpha",
                IsRestored = true,
            }
        );

        Assert.That(alphaValue, Is.EqualTo("already_changed"));
        Assert.That(
            result,
            Is.EqualTo("Set test_project_settings.alpha: alpha -> already_changed")
        );
    }

    [Test]
    public void BridgePreservesOperationAndExplicitEmptyValue()
    {
        var message = BridgeMessage.CreateCommand(
            "project-settings-wire",
            new()
            {
                CommandType = BridgeCommandTypes.ProjectSettings,
                Target = "test_project_settings.empty_value",
                Snippet = string.Empty,
                Args = new[] { "set" },
            }
        );

        var command = BridgeProtocol.Deserialize(BridgeProtocol.Serialize(message))!.command!;

        Assert.That(command.snippet, Is.EqualTo(string.Empty));
        Assert.That(command.args, Is.EqualTo(new[] { "set" }));
    }

    [Test]
    public void CatalogIncludesStableCoreAndProceduralSettings()
    {
        var settings = ProjectSettingsRegistry.Build().Settings;
        var keys = settings.Select(setting => setting.Key).ToHashSet();
        var productGuidSettings = settings
            .Where(setting => setting.Key.StartsWith(
                "player_settings.product_guid",
                StringComparison.Ordinal
            ))
            .ToArray();
        bool ContainsPrefix(string prefix)
            => keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal));

        Assert.That(keys, Does.Contain("graphics_settings.log_shader_compilation"));
        Assert.That(ContainsPrefix("player_settings.platforms.android."), Is.True);
        Assert.That(
            keys.Any(key => key.StartsWith("quality_settings.quality_levels.", StringComparison.Ordinal)
                            && key.EndsWith(".async_asset_upload.time_slice", StringComparison.Ordinal)),
            Is.True
        );
        Assert.That(keys, Does.Contain("quality_settings.platforms.standalone.default_level"));
        Assert.That(keys, Does.Contain("build_settings.active_platform"));
        Assert.That(productGuidSettings, Is.Not.Empty);
        Assert.That(
            productGuidSettings.All(
                setting => setting.Operations == ProjectSettingOperations.None
            ),
            Is.True
        );
        Assert.That(keys, Does.Contain("conduit_settings.platforms.standalone.enable_in_development_mode"));
    }

    [Test]
    public void CollectionOperationsAddAndRemoveSerializedElements()
    {
        var target = ScriptableObject.CreateInstance<ProjectSettingsArrayFixture>();
        try
        {
            var initial = RegisterArray(target);
            var append = initial.Settings.Single(setting => setting.Key == "serialized_array.values.1");
            Assert.That(append.Operations, Is.EqualTo(ProjectSettingOperations.AddElement));

            string added = Execute(initial, "add_element", "serialized array values", "2");
            Assert.That(added, Is.EqualTo("Added serialized_array.values.1: <absent> -> 2"));

            var afterAdd = RegisterArray(target);
            var element = afterAdd.Settings.Single(setting => setting.Key == "serialized_array.values.1");
            Assert.That(element.Read(), Is.EqualTo("2"));
            Assert.That(
                element.Operations,
                Is.EqualTo(ProjectSettingOperations.Set | ProjectSettingOperations.RemoveElement)
            );

            OperationPersistence.ClearActiveOperation();
            Assert.Throws<FormatException>(
                () => Execute(afterAdd, "set", "serialized_array.values.1")
            );
            Assert.That(
                RegisterArray(target).Settings
                    .Single(setting => setting.Key == "serialized_array.values.1")
                    .Read(),
                Is.EqualTo("2")
            );

            OperationPersistence.ClearActiveOperation();
            string removed = Execute(afterAdd, "remove_element", "serialized array values 1");
            Assert.That(removed, Is.EqualTo("Removed serialized_array.values.1: 2 -> <removed>"));

            var afterRemove = RegisterArray(target);
            Assert.That(
                afterRemove.Settings.Single(setting => setting.Key == "serialized_array.values.1").Read(),
                Is.EqualTo("<append>")
            );

            OperationPersistence.ClearActiveOperation();
            string addedNull = Execute(afterRemove, "add_element", "serialized_array.references");
            Assert.That(
                addedNull,
                Is.EqualTo("Added serialized_array.references.0: <absent> -> null")
            );
            Assert.That(
                RegisterArray(target).Settings
                    .Single(setting => setting.Key == "serialized_array.references.count")
                    .Read(),
                Is.EqualTo("1")
            );
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(target);
        }
    }

    [ConduitProjectSettingsProvider]
    static void Register(ProjectSettingsRegistry registry)
    {
        registry.Add<string?>("test_project_settings.empty_value", () => emptyValue, value => emptyValue = value);
        registry.Add("test_project_settings.alpha", () => alphaValue, value => alphaValue = value);
        registry.Add("test_project_settings.alphabet", () => alphabetValue, value => alphabetValue = value);
        registry.Add("test_project_settings.read_only", () => "identity");
        registry.Add("test_project_settings.duplicate", () => "first", _ => { });
        registry.Add("test_project_settings.duplicate", () => "second", _ => { });
    }

    static ProjectSettingsRegistry CreateListingRegistry()
    {
        var registry = new ProjectSettingsRegistry();
        for (int index = 0; index < 40; ++index)
        {
            int value = index;
            registry.Add($"large_group.setting_{index:00}", () => value, _ => { });
        }
        registry.Add("other_group.value", () => true);
        return registry;
    }

    static ProjectSettingsRegistry RegisterArray(ProjectSettingsArrayFixture target)
    {
        var registry = new ProjectSettingsRegistry();
        SerializedProjectSettingsProvider.RegisterObject(
            registry,
            "serialized_array",
            target,
            static () => { }
        );
        return registry;
    }

    static string Execute(string operation, string key, string? value = null)
        => Execute(ProjectSettingsRegistry.Build(), operation, key, value);

    static string Execute(
        ProjectSettingsRegistry registry,
        string operation,
        string key,
        string? value = null)
        => ProjectSettingsTool.Execute(
            new PendingOperationState
            {
                RequestID = "project-settings-test",
                CommandType = BridgeCommandTypes.ProjectSettings,
                Target = key,
                Snippet = value,
                Args = new[] { operation },
            },
            registry
        );
}
