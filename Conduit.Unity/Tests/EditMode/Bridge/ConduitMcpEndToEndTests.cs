#nullable enable

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Conduit;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed partial class ConduitMcpEndToEndTests
{
    const string TestAssetsRoot = "Packages/dev.tryfinally.conduit/Tests/EditMode/TestAssets";
    const string MaterialAsset = TestAssetsRoot + "/JsonOverwriteMaterial.mat";
    const string MaterialShaderAsset = TestAssetsRoot + "/IntegerPropertyFixture.shader";
    const string MissingScriptPrefabAsset = TestAssetsRoot + "/MissingScriptFixture.prefab";
    const string SceneAsset = TestAssetsRoot + "/BridgeFixtureScene.unity";
    const string SourceAsset = MaterialAsset;
    const string DependencyAsset = MaterialShaderAsset;
    const string PackageScriptAsset = "Packages/dev.tryfinally.conduit/Editor/Bridge/Execution/ConduitToolRunner.cs";
    const string MissingScenePath = "Assets/ConduitMcpDefinitelyMissingScene.unity";
    const string MissingQuery = "ConduitMcpDefinitelyMissingObject";
    static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);

    readonly List<string> temporaryAssetPaths = new();
    readonly List<string> temporaryDirectories = new();
    McpStdioTestClient client = null!;
    bool canonicalAssetsValidated;
    bool searchProvidersWarmed;
    string editorProjectPath = string.Empty;
    string projectPath = string.Empty;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        if (ConduitConnection.GetConnectionStatus() == ConduitConnectionStatus.Connected)
            Assert.Ignore("This end-to-end suite must run without another active Conduit bridge client attached to the editor.");

        editorProjectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        projectPath = ConduitProjectIdentity.NormalizeProjectPath(editorProjectPath);

        ValidateCanonicalAssets();
        canonicalAssetsValidated = true;

        ConduitToolRunner.Initialize();
        ConduitConnection.EnsureStarted();

        try
        {
            client = McpStdioTestClient.StartAsync(StartupTimeout)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            client?.Dispose();
            throw;
        }
    }

    [SetUp]
    public void SetUp()
    {
        temporaryAssetPaths.Clear();
        temporaryDirectories.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        if (canonicalAssetsValidated)
            OpenSampleScene();

        CleanupTemporaryAssets();
        CleanupTemporaryDirectories();
        ConduitTestAssets.CleanupTemporaryRoot();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => client?.Dispose();

    [Test]
    [Order(1)]
    public void Initialize_Succeeds()
    {
        Assert.That(client.NegotiatedProtocolVersion, Is.Not.Empty);
        Assert.That(client.ServerName, Is.Not.Empty);
        Assert.That(client.ServerName.IndexOf("conduit", StringComparison.OrdinalIgnoreCase), Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    [Order(2)]
    public async Task ToolsList_ContainsBridgeCommandSurface()
    {
        var tools = await client.ListToolsAsync();

        foreach (var tool in new[]
                 {
                     BridgeCommandTypes.Status,
                     BridgeCommandTypes.PlayMode,
                     BridgeCommandTypes.EditMode,
                     BridgeCommandTypes.Screenshot,
                     BridgeCommandTypes.GetDependencies,
                     BridgeCommandTypes.FindReferencesTo,
                     BridgeCommandTypes.FindMissingScripts,
                     BridgeCommandTypes.Show,
                     BridgeCommandTypes.Search,
                     BridgeCommandTypes.ToJson,
                     BridgeCommandTypes.FromJsonOverwrite,
                     BridgeCommandTypes.SaveScenes,
                     BridgeCommandTypes.DiscardScenes,
                     BridgeCommandTypes.RefreshAssetDatabase,
                     BridgeCommandTypes.ReimportAssets,
                     BridgeCommandTypes.ExecuteCode,
                     BridgeCommandTypes.Detour,
                     BridgeCommandTypes.Reflect,
                     BridgeCommandTypes.ProjectSettings,
                     BridgeCommandTypes.ProfilerRecord,
                     BridgeCommandTypes.ProfilerOverview,
                     BridgeCommandTypes.ProfilerBrowse,
                 })
            Assert.That(tools, Has.Member(tool));
    }

    [Test]
    [Order(3)]
    public async Task Status_ReportsReachableAndInvalidProjectFailure()
    {
        var reachable = await client.CallToolAsync(
            BridgeCommandTypes.Status,
            Args(("projectPath", projectPath))
        );

        Assert.That(reachable.IsError, Is.False, reachable.Text);
        AssertTextContainsAny(reachable.Text, "Bridge: reachable", "Status:");
        AssertTextContainsAny(reachable.Text, projectPath, "Unity ");
        Assert.That(reachable.Text, Does.Contain("Editor log:"));

        var invalidProjectPath = CreateInvalidProjectPath();
        var invalidProject = await client.CallToolAsync(
            BridgeCommandTypes.Status,
            Args(("projectPath", invalidProjectPath))
        );

        AssertTextContainsAny(invalidProject.Text, "not a valid Unity project", "Project:");
    }

}
#endif
