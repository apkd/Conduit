#nullable enable

#if UNITY_EDITOR
using System;
using System.IO;
using System.Threading.Tasks;
using Conduit;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public sealed partial class ConduitMcpEndToEndTests
{
    [Test]
    [Order(20)]
    public async Task PlayModeAndEditMode_EnterRequestedMode()
    {
        var originalOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
        var originalOptions = EditorSettings.enterPlayModeOptions;

        try
        {
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;

            var enteredPlay = await client.CallToolAsync(
                BridgeCommandTypes.PlayMode,
                Args(("projectPath", projectPath))
            );

            AssertSuccessful(enteredPlay, "Entered play mode", "Paused:");

            var alreadyPlay = await client.CallToolAsync(
                BridgeCommandTypes.PlayMode,
                Args(("projectPath", projectPath))
            );

            AssertSuccessful(alreadyPlay, "Already in play mode", "Paused:");

            var enteredEdit = await client.CallToolAsync(
                BridgeCommandTypes.EditMode,
                Args(("projectPath", projectPath))
            );

            AssertSuccessful(enteredEdit, "Entered edit mode.");

            var alreadyEdit = await client.CallToolAsync(
                BridgeCommandTypes.EditMode,
                Args(("projectPath", projectPath))
            );

            AssertSuccessful(alreadyEdit, "Already in edit mode.");
        }
        finally
        {
            if (EditorApplication.isPlaying)
                EditorApplication.isPlaying = false;

            EditorSettings.enterPlayModeOptions = originalOptions;
            EditorSettings.enterPlayModeOptionsEnabled = originalOptionsEnabled;
        }
    }

    [Test]
    [Order(16)]
    public async Task Screenshot_CapturesCameraSceneAssetAndAmbiguousSelector()
    {
        OpenSampleScene();

        if (ConduitTestEnvironment.SupportsRenderedScreenshots)
        {
            var cameraCapture = await client.CallToolAsync(
                BridgeCommandTypes.Screenshot,
                Args(
                    ("projectPath", projectPath),
                    ("target", ConduitObjectId.FormatObjectId(Camera.main))
                )
            );

            AssertSuccessful(cameraCapture, "Main_Camera image captured:", "Temp/screenshot/");
            AssertCapturedImageExists(cameraCapture.Text);

            var sceneAssetPath = CreateTemporaryScreenshotSceneAsset();
            var sceneCapture = await client.CallToolAsync(
                BridgeCommandTypes.Screenshot,
                Args(
                    ("projectPath", projectPath),
                    ("target", sceneAssetPath)
                )
            );

            AssertSuccessful(sceneCapture, "ScreenshotScene image captured:", "Temp/screenshot/");
            AssertCapturedImageExists(sceneCapture.Text);
        }

        OpenSampleScene();

        var ambiguous = await client.CallToolAsync(
            BridgeCommandTypes.Screenshot,
            Args(
                ("projectPath", projectPath),
                ("target", "ConduitDirtySave")
            )
        );

        AssertTextContainsAny(ambiguous.Text, "Multiple objects match your query.", "ConduitDirtySave");
    }

    [Test]
    [Order(100)]
    public async Task RefreshAssetDatabase_ImportsNewTextAsset()
    {
        var assetPath = RegisterTemporaryAsset(ConduitTestAssets.GetTemporaryPath("Refresh", $"RefreshAsset_{Guid.NewGuid():N}.txt"));
        File.WriteAllText(ToAbsoluteProjectPath(assetPath), "hello from refresh");

        Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath), Is.Null);

        var result = await client.CallToolAsync(
            BridgeCommandTypes.RefreshAssetDatabase,
            Args(("projectPath", projectPath))
        );

        Assert.That(result.IsError, Is.False, result.Text);
        AssertTextContainsAny(result.Text, "Success");

        var importedAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);
        Assert.That(importedAsset, Is.Not.Null, $"Asset '{assetPath}' was not imported after refresh.");
        Assert.That(importedAsset!.text, Is.EqualTo("hello from refresh"));
    }

    [Test]
    [Order(101)]
    public async Task RefreshAssetDatabase_NoChangesReturnsPromptly()
    {
        var startedAt = DateTime.UtcNow;
        var result = await client.CallToolAsync(
            BridgeCommandTypes.RefreshAssetDatabase,
            Args(("projectPath", projectPath)),
            timeout: TimeSpan.FromSeconds(10)
        );

        var elapsed = DateTime.UtcNow - startedAt;
        Assert.That(result.IsError, Is.False, result.Text);
        AssertTextContainsAny(result.Text, "Success");
        Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(10)), $"No-op refresh took {elapsed.TotalSeconds:0.000}s.");
    }

    [Test]
    [Order(102)]
    public async Task ReimportAssets_UpdatesExistingTextAssetAndListsFilename()
    {
        var fileName = $"ReimportAsset_{Guid.NewGuid():N}.txt";
        var assetPath = RegisterTemporaryAsset(ConduitTestAssets.GetTemporaryPath("Reimport", fileName));
        File.WriteAllText(ToAbsoluteProjectPath(assetPath), "before reimport");
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

        var importedAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);
        Assert.That(importedAsset, Is.Not.Null);
        Assert.That(importedAsset!.text, Is.EqualTo("before reimport"));

        File.WriteAllText(ToAbsoluteProjectPath(assetPath), "after reimport");
        var result = await client.CallToolAsync(
            BridgeCommandTypes.ReimportAssets,
            Args(
                ("projectPath", projectPath),
                ("query", assetPath)
            )
        );

        Assert.That(result.IsError, Is.False, result.Text);
        AssertTextContainsAny(result.Text, "Reimported assets:", fileName);
        Assert.That(result.Text, Does.Not.Contain(assetPath));

        importedAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);
        Assert.That(importedAsset, Is.Not.Null);
        Assert.That(importedAsset!.text, Is.EqualTo("after reimport"));
    }

    [Test]
    [Order(103)]
    public async Task ReimportAssets_RejectsScriptCompilationInputs()
    {
        var result = await client.CallToolAsync(
            BridgeCommandTypes.ReimportAssets,
            Args(
                ("projectPath", projectPath),
                ("query", PackageScriptAsset)
            ),
            timeout: TimeSpan.FromSeconds(10)
        );

        Assert.That(result.Text, Does.Contain(PackageScriptAsset));
        Assert.That(result.Text, Does.Contain("No assets were reimported"));
        Assert.That(result.Text, Does.Contain(BridgeCommandTypes.RefreshAssetDatabase));
    }

    [Test]
    [Order(104)]
    public async Task RefreshAssetDatabase_PlayModeRefusesPromptly()
    {
        var originalOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
        var originalOptions = EditorSettings.enterPlayModeOptions;

        try
        {
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;

            var enteredPlay = await client.CallToolAsync(
                BridgeCommandTypes.PlayMode,
                Args(("projectPath", projectPath))
            );

            AssertSuccessful(enteredPlay, "Entered play mode", "Paused:");

            var startedAt = DateTime.UtcNow;
            var result = await client.CallToolAsync(
                BridgeCommandTypes.RefreshAssetDatabase,
                Args(("projectPath", projectPath)),
                timeout: TimeSpan.FromSeconds(10)
            );

            var elapsed = DateTime.UtcNow - startedAt;
            AssertTextContainsAny(result.Text, "Cannot run 'refresh_asset_database' while Unity is in play mode");
            Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(10)), $"Play-mode refresh refusal took {elapsed.TotalSeconds:0.000}s.");
        }
        finally
        {
            if (EditorApplication.isPlaying)
                EditorApplication.isPlaying = false;

            EditorSettings.enterPlayModeOptions = originalOptions;
            EditorSettings.enterPlayModeOptionsEnabled = originalOptionsEnabled;
        }
    }

}
#endif
