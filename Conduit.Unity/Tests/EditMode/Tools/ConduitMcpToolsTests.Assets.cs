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
    public void ReimportAssetsCommand_Parses()
    {
        var command = ConduitToolRunner.ParseIncomingCommand(BridgeCommandTypes.ReimportAssets);

        Assert.That(command, Is.EqualTo(BridgeCommandKind.ReimportAssets));
    }

    [Test]
    public void ResolveAssetPaths_UsesObjectQueryAndReturnsAssetsOnly()
    {
        var materialMatches = ConduitSearchUtility.ResolveAssetPaths(MaterialAsset);
        Assert.That(materialMatches, Is.EqualTo(new[] { MaterialAsset }));

        var cameraMatches = ConduitSearchUtility.ResolveAssetPaths(CameraSearchQuery);
        Assert.That(cameraMatches, Is.Empty);
    }

    [Test]
    public void ReimportAssetFilenames_FormatOmitsAssetPaths()
    {
        var output = ConduitToolRunner.FormatReimportedAssetFilenames(
            "Assets/Temp/Foo.asset\nPackages/dev.tryfinally.conduit/Tests/EditMode/TestAssets/JsonOverwriteMaterial.mat"
        );

        Assert.That(output, Does.Contain("- Foo.asset"));
        Assert.That(output, Does.Contain("- JsonOverwriteMaterial.mat"));
        Assert.That(output, Does.Not.Contain("Assets/Temp"));
        Assert.That(output, Does.Not.Contain("Packages/dev.tryfinally.conduit"));
    }

    [Test]
    public void GetDependencies_PatternWithSingleMatchMatchesExactOutput()
    {
        var exact = AssetReferencesTool.GetDependencies(SourceAsset);
        var pattern = AssetReferencesTool.GetDependencies($"{TestAssetsRoot}/JsonOverwriteMaterial*.mat");

        Assert.That(pattern, Is.EqualTo(exact));
    }

    [Test]
    public void ExpandAssetPaths_PackageWildcardMatchesSingleAsset()
    {
        var matches = ConduitAssetPathUtility.ExpandAssetPaths($"{TestAssetsRoot}/JsonOverwriteMaterial*.mat");

        Assert.That(matches, Is.EqualTo(new[] { SourceAsset }));
    }

    [Test]
    public void FindReferencesTo_PatternWithSingleMatchMatchesExactOutput()
    {
        var exact = AssetReferencesTool.FindReferencesTo(DependencyAsset, true);
        var pattern = AssetReferencesTool.FindReferencesTo($"{TestAssetsRoot}/IntegerPropertyFixture*.shader", false);

        Assert.That(pattern, Is.EqualTo(exact));
    }

    [Test]
    public void GetDependencies_PatternWithMultipleMatchesReportsAmbiguity()
    {
        var output = AssetReferencesTool.GetDependencies($"{TestAssetsRoot}/*.*");

        Assert.That(output, Does.StartWith($"Asset selector '{TestAssetsRoot}/*.*' matched "));
        Assert.That(output, Does.Contain("requires a single asset"));
    }

    [Test]
    public void FindReferencesTo_PatternWithNoMatchesReportsNoResults()
    {
        var output = AssetReferencesTool.FindReferencesTo($"{TestAssetsRoot}/Nope*.asset", true);

        Assert.That(output, Is.EqualTo($"No assets matched '{TestAssetsRoot}/Nope*.asset'."));
    }

    [Test]
    public void RefreshAssetDatabasePlayModeGuard_BlocksPlayMode()
    {
        Assert.That(ConduitToolRunner.ShouldBlockReimportForPlayMode(true), Is.True);
        Assert.That(ConduitToolRunner.ShouldBlockReimportForPlayMode(false), Is.False);
        Assert.That(ConduitToolRunner.BuildReimportPlayModeDiagnostic(), Is.EqualTo(
            "Cannot run 'refresh_asset_database' while Unity is in play mode. Use 'editmode' to return to edit mode first."));
        Assert.That(ConduitToolRunner.BuildReimportPlayModeDiagnostic(BridgeCommandTypes.ReimportAssets), Is.EqualTo(
            "Cannot run 'reimport_assets' while Unity is in play mode. Use 'editmode' to return to edit mode first."));
    }

    [Test]
    public void ReimportSettlement_WaitsForIdleSettleWindow()
    {
        Assert.That(ConduitToolRunner.ReimportIdleSettleUpdates, Is.EqualTo(8));

        Assert.That(ConduitToolRunner.ShouldWaitForReimportIdle(false, false, false, 8), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForReimportIdle(true, true, false, 8), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForReimportIdle(true, false, true, 8), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForReimportIdle(true, false, false, 0), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForReimportIdle(true, false, false, 7), Is.True);
        Assert.That(ConduitToolRunner.ShouldWaitForReimportIdle(true, false, false, 8), Is.False);
    }

    [TestCase("Assets/Example.cs", false, true)]
    [TestCase("Assets/Example.ASMDEF", false, true)]
    [TestCase("Assets/Example.asmref", false, true)]
    [TestCase("Assets/csc.rsp", false, true)]
    [TestCase("Assets/Managed.dll", true, true)]
    [TestCase("Assets/Native.dll", false, false)]
    [TestCase("Assets/Example.shader", false, false)]
    public void ReimportAssets_IdentifiesScriptCompilationInputs(
        string assetPath,
        bool isManagedAssembly,
        bool expected
    )
        => Assert.That(
            AssetImportMonitor.IsCompilationInputAssetPath(assetPath, isManagedAssembly),
            Is.EqualTo(expected)
        );

    [Test]
    public void ReimportAssets_CompilationInputDiagnosticDirectsToRefresh()
    {
        const string assetPath = "Assets/Example.cs";

        var diagnostic = AssetImportMonitor.BuildCompilationInputReimportDiagnostic(assetPath);

        Assert.That(diagnostic, Does.Contain(assetPath));
        Assert.That(diagnostic, Does.Contain(BridgeCommandTypes.ReimportAssets));
        Assert.That(diagnostic, Does.Contain("No assets were reimported"));
        Assert.That(diagnostic, Does.Contain(BridgeCommandTypes.RefreshAssetDatabase));
    }
}
