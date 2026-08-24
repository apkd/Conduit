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
    public void RecordCommand_ParsesAndSupportsWaitCancellation()
    {
        var command = ConduitToolRunner.ParseIncomingCommand(BridgeCommandTypes.Record);

        Assert.That(command, Is.EqualTo(BridgeCommandKind.Record));
        Assert.That(BridgeCommandKinds.SupportsCancellation(command), Is.True);
    }

    [Test]
    public void Screenshot_ModuleUnavailableDiagnosticListsOnlyMissingModules()
    {
        Assert.That(
            ScreenshotTool.BuildModuleUnavailableDiagnostic(false, false),
            Is.EqualTo(
                "ERROR: Unity built-in modules `com.unity.modules.imageconversion` and " +
                "`com.unity.modules.screencapture` are not enabled in this project. " +
                "Ask the user for permission to enable the modules so that the `screenshot` tool can be used."
            )
        );
        Assert.That(
            ScreenshotTool.BuildModuleUnavailableDiagnostic(false, true),
            Is.EqualTo(
                "ERROR: Unity built-in module `com.unity.modules.imageconversion` is not enabled in this project. " +
                "Ask the user for permission to enable the module so that the `screenshot` tool can be used."
            )
        );
        Assert.That(
            ScreenshotTool.BuildModuleUnavailableDiagnostic(true, false),
            Is.EqualTo(
                "ERROR: Unity built-in module `com.unity.modules.screencapture` is not enabled in this project. " +
                "Ask the user for permission to enable the module so that the `screenshot` tool can be used."
            )
        );
    }

    [Test]
    public void Screenshot_OutputPathsUseShortSequentialFileNames()
    {
        var projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var screenshotDirectoryPath = Path.Combine(projectPath, "Temp", "screenshot");
        Directory.CreateDirectory(screenshotDirectoryPath);

        foreach (var existingPath in Directory.EnumerateFiles(screenshotDirectoryPath, "Test_Path_*.jpg"))
            File.Delete(existingPath);

        var first = ScreenshotTool.AllocateOutputPath(projectPath, "Test Path");
        var firstPrefix = first.Prefix;
        var firstRelativePath = first.RelativePath;
        var firstAbsolutePath = first.AbsolutePath;

        Assert.That(firstPrefix, Is.EqualTo("Test_Path"));
        Assert.That(firstRelativePath, Is.EqualTo("Temp/screenshot/Test_Path_1.jpg"));
        File.WriteAllBytes(firstAbsolutePath, new byte[] { 1 });

        try
        {
            var second = ScreenshotTool.AllocateOutputPath(projectPath, "Test Path");
            var secondRelativePath = second.RelativePath;
            Assert.That(secondRelativePath, Is.EqualTo("Temp/screenshot/Test_Path_2.jpg"));
        }
        finally
        {
            if (File.Exists(firstAbsolutePath))
                File.Delete(firstAbsolutePath);
        }
    }

    [Test]
    public void RecordSettings_ParseAndRoundDurationUpToAWholeFrame()
    {
        var settings = RecordSettings.Parse(
            " game_view ",
            new[]
            {
                "duration_seconds=1.01",
                "adjust_delta_time=true",
                "frame_rate=60",
                "resolution_scale=0.5",
                "format=x265",
                "crf=19",
            }
        );

        Assert.That(settings.Target, Is.EqualTo("game_view"));
        Assert.That(settings.FrameCount, Is.EqualTo(61));
        Assert.That(settings.AdjustDeltaTime, Is.True);
        Assert.That(settings.ResolutionScale, Is.EqualTo(0.5f));
        Assert.That(settings.Format, Is.EqualTo("x265"));
        Assert.That(settings.Crf, Is.EqualTo(19));
    }

    [TestCase("duration_seconds=0", "durationSeconds")]
    [TestCase("frame_rate=0", "frameRate")]
    [TestCase("resolution_scale=1.1", "resolution_scale")]
    [TestCase("format=avi", "format")]
    [TestCase("crf=64", "crf")]
    public void RecordSettings_RejectInvalidValues(string replacement, string expectedDiagnostic)
    {
        var args = new[]
        {
            "duration_seconds=1",
            "adjust_delta_time=false",
            "frame_rate=60",
            "resolution_scale=0.5",
            "format=webm",
            "crf=23",
        };
        var key = replacement[..replacement.IndexOf('=')];
        for (var index = 0; index < args.Length; index++)
            if (args[index].StartsWith(key + "=", StringComparison.Ordinal))
                args[index] = replacement;

        var exception = Assert.Throws<InvalidOperationException>(
            () => RecordSettings.Parse("game_view", args)
        );
        Assert.That(exception!.Message, Does.Contain(expectedDiagnostic));
    }

    [Test]
    public void RecordOutputPaths_AreSequentialAcrossFormats()
    {
        var projectPath = Path.Combine(
            Path.GetTempPath(),
            "conduit-record-path-" + Guid.NewGuid().ToString("N")
        );
        try
        {
            var first = RecordOutputPath.Allocate(projectPath, "auto");
            Assert.That(first.RelativePath, Is.EqualTo("Library/Recordings/0.mp4"));
            File.WriteAllBytes(first.AbsolutePath, new byte[] { 1 });

            var second = RecordOutputPath.Allocate(projectPath, "gif");
            Assert.That(second.RelativePath, Is.EqualTo("Library/Recordings/1.gif"));
            File.WriteAllBytes(second.IntermediatePath, new byte[] { 1 });

            var third = RecordOutputPath.Allocate(projectPath, "webm");
            Assert.That(third.RelativePath, Is.EqualTo("Library/Recordings/2.webm"));
        }
        finally
        {
            if (Directory.Exists(projectPath))
                Directory.Delete(projectPath, recursive: true);
        }
    }

    [Test]
    public async Task Screenshot_CameraCaptureCreatesImage()
    {
        var camera = Camera.main;
        Assert.That(camera, Is.Not.Null);

        if (ConduitTestEnvironment.SupportsRenderedScreenshots)
        {
            var result = await InvokeScreenshotWithoutDesktopFocusChangeAsync(
                ConduitObjectId.FormatObjectId(camera)
            );
            try
            {
                Assert.That(result, Does.Contain("Main_Camera image captured: Temp/screenshot/"));
                AssertCapturedImagesHaveVisualVariation(result);
            }
            finally
            {
                DeleteCapturedImages(result);
            }

            return;
        }

        var exception = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await InvokeScreenshotAsync(ConduitObjectId.FormatObjectId(camera))
        );
        Assert.That(exception, Is.Not.Null);
        Assert.That(exception!.Message, Does.Contain("graphics device").Or.Contain("interactive Unity editor window"));
    }

    [Test]
    public async Task Screenshot_SceneAssetCaptureCreatesImage()
    {
        var assetPath = ConduitTestAssets.GetTemporaryPath("UnitTests", $"ScreenshotScene_{Guid.NewGuid():N}.unity");
        CreateTemporaryScreenshotSceneAsset(assetPath);

        try
        {
            if (ConduitTestEnvironment.SupportsRenderedScreenshots)
            {
                var result = await InvokeScreenshotWithoutDesktopFocusChangeAsync(assetPath);
                try
                {
                    Assert.That(result, Does.Contain("ScreenshotScene_").And.Contain(" image captured: Temp/screenshot/"));
                    AssertCapturedImagesHaveVisualVariation(result);
                }
                finally
                {
                    DeleteCapturedImages(result);
                }
            }
            else
            {
                var exception = Assert.ThrowsAsync<InvalidOperationException>(async () => await InvokeScreenshotAsync(assetPath));
                Assert.That(exception, Is.Not.Null);
                Assert.That(exception!.Message, Does.Contain("graphics device").Or.Contain("interactive Unity editor window"));
            }
        }
        finally
        {
            AssetDatabase.DeleteAsset(assetPath);
        }
    }

    [TestCase(PrimitiveType.Cube)]
    [TestCase(PrimitiveType.Quad)]
    public async Task Screenshot_GameObjectPreviewPreservesDesktopFocus(PrimitiveType primitiveType)
    {
        RequireInteractiveEditorWindows();
        var previewScene = EditorSceneManager.NewPreviewScene();
        var gameObject = GameObject.CreatePrimitive(primitiveType);
        SceneManager.MoveGameObjectToScene(gameObject, previewScene);

        try
        {
            var result = await InvokeScreenshotWithoutDesktopFocusChangeAsync(
                ConduitObjectId.FormatObjectId(gameObject)
            );
            try
            {
                AssertCapturedImagesHaveVisualVariation(result);
            }
            finally
            {
                DeleteCapturedImages(result);
            }
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(previewScene);
        }
    }

    [Test]
    public async Task Screenshot_AssetPreviewPreservesDesktopFocus()
    {
        RequireInteractiveEditorWindows();

        var result = await InvokeScreenshotWithoutDesktopFocusChangeAsync(MaterialAsset);
        try
        {
            AssertCapturedImagesHaveVisualVariation(result);
        }
        finally
        {
            DeleteCapturedImages(result);
        }
    }

    [Test]
    public async Task GpuCapture_SaveJpegRestoresActiveRenderTexture()
    {
        RequireInteractiveEditorWindows();
        var previousActive = RenderTexture.active;
        var sentinel = RenderTexture.GetTemporary(4, 4, 0);
        var source = RenderTexture.GetTemporary(4, 4, 0);
        var path = Path.Combine(Path.GetTempPath(), $"conduit-gpu-capture-{Guid.NewGuid():N}.jpg");
        try
        {
            RenderTexture.active = sentinel;
            await GpuCapture.SaveJpegAsync(source, path, flipVertically: false);
            Assert.That(RenderTexture.active, Is.SameAs(sentinel));
        }
        finally
        {
            RenderTexture.active = previousActive;
            RenderTexture.ReleaseTemporary(source);
            RenderTexture.ReleaseTemporary(sentinel);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [TestCase("editor")]
    [TestCase("game_view")]
    [TestCase("scene_view")]
    public async Task Screenshot_EditorViewTargetPreservesDesktopFocus(string target)
    {
        RequireInteractiveEditorWindows();

        var result = await InvokeScreenshotWithoutDesktopFocusChangeAsync(target);
        try
        {
            Assert.That(result, Does.Contain(" image captured: Temp/screenshot/"));
            AssertCapturedImagesHaveVisualVariation(result);
        }
        finally
        {
            DeleteCapturedImages(result);
        }
    }

    [Test]
    public async Task Screenshot_AmbiguousQueryRequestsDisambiguation()
    {
        var result = await InvokeScreenshotAsync("ConduitDirtySave");

        Assert.That(result, Does.Contain("Multiple objects match your query."));
        Assert.That(result, Does.Contain("ConduitDirtySave"));
    }

    [Test]
    public async Task Screenshot_WindowTarget_NoMatchesReportsNoResults()
    {
        RequireInteractiveEditorWindows();

        var result = await InvokeScreenshotAsync("window:NoSuchConduitScreenshotWindow");

        Assert.That(result, Is.EqualTo("No matches for 'window:NoSuchConduitScreenshotWindow'."));
    }

    [Test]
    public async Task Screenshot_WindowTarget_AmbiguousOpenWindowQueryRequestsDisambiguation()
    {
        RequireInteractiveEditorWindows();

        var alphaWindow = OpenScreenshotTestWindow<ConduitWindowMatchAlphaWindow>();
        var betaWindow = OpenScreenshotTestWindow<ConduitWindowMatchBetaWindow>();

        Assert.That(ConduitEditorWindowDocking.IsDockedInMainWindow(alphaWindow), Is.True);
        Assert.That(ConduitEditorWindowDocking.IsDockedInMainWindow(betaWindow), Is.True);

        var result = await InvokeScreenshotAsync("window:window match");

        Assert.That(result, Does.Contain("Multiple editor windows match your query."));
        Assert.That(result, Does.Contain("Conduit Window Match Alpha | EditorWindow:Conduit Window Match Alpha (ConduitWindowMatchAlphaWindow)"));
        Assert.That(result, Does.Contain("Conduit Window Match Beta | EditorWindow:Conduit Window Match Beta (ConduitWindowMatchBetaWindow)"));
    }

    [Test]
    public async Task Screenshot_WindowTarget_AmbiguousWindowTypeQueryRequestsDisambiguation()
    {
        RequireInteractiveEditorWindows();

        var result = await InvokeScreenshotAsync("window:TypeMatch");

        Assert.That(result, Does.Contain("Multiple editor windows match your query."));
        Assert.That(result, Does.Contain("ConduitTypeMatchAlphaWindow | EditorWindow type"));
        Assert.That(result, Does.Contain("ConduitTypeMatchBetaWindow | EditorWindow type"));
    }

    [Test]
    public async Task Screenshot_WindowTargetCapturesWithoutChangingDesktopFocus()
    {
        if (ConduitTestEnvironment.SupportsRenderedScreenshots)
        {
            var result = await InvokeScreenshotWithoutDesktopFocusChangeAsync("window:CaptureProbe");
            try
            {
                Assert.That(result, Does.Contain("Conduit_Capture_Probe image captured: Temp/screenshot/"));
                AssertCapturedImagesHaveVisualVariation(result);
            }
            finally
            {
                DeleteCapturedImages(result);
            }

            Assert.That(Resources.FindObjectsOfTypeAll<ConduitCaptureProbeWindow>(), Is.Empty);
            return;
        }

        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () => await InvokeScreenshotAsync("window:CaptureProbe"));
        Assert.That(exception, Is.Not.Null);
        Assert.That(exception!.Message, Does.Contain("graphics device").Or.Contain("interactive Unity editor window"));
    }

    [Test]
    public async Task Screenshot_HiddenEditorWindowRestoresSelectedTab()
    {
        RequireInteractiveEditorWindows();
        var target = OpenScreenshotTestWindow<ConduitCaptureProbeWindow>();
        var selected = OpenScreenshotTestWindow<ConduitWindowMatchAlphaWindow>();
        Assert.That(
            EditorCaptureSource.EditorWindowState.Capture().GetTabToRestore(target),
            Is.SameAs(selected)
        );

        var result = await InvokeScreenshotWithoutDesktopFocusChangeAsync("window:CaptureProbe");
        try
        {
            AssertCapturedImagesHaveVisualVariation(result);
            Assert.That(
                EditorCaptureSource.EditorWindowState.Capture().GetTabToRestore(target),
                Is.SameAs(selected)
            );
        }
        finally
        {
            DeleteCapturedImages(result);
        }
    }

    [Test]
    public async Task Screenshot_HiddenEditorTargetRejectsMaximizedLayoutWithoutChangingIt()
    {
        RequireInteractiveEditorWindows();
        var maximized = OpenScreenshotTestWindow<ConduitCaptureProbeWindow>();
        ExpectStaleFallbackWindowErrorsOnLayoutChange();
        maximized.maximized = true;
        await EditorCaptureSource.WaitForNextEditorUpdateAsync();

        try
        {
            var previouslyFocusedWindow = EditorWindow.focusedWindow;
            var wasApplicationActive = UnityEditorInternal.InternalEditorUtility.isApplicationActive;
            var exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await InvokeScreenshotAsync("game_view")
            );

            Assert.That(exception, Is.Not.Null);
            Assert.That(exception!.Message, Does.Contain("maximized").And.Contain("Restore the editor layout"));
            Assert.That(maximized.maximized, Is.True);
            Assert.That(EditorWindow.focusedWindow, Is.SameAs(previouslyFocusedWindow));
            Assert.That(
                UnityEditorInternal.InternalEditorUtility.isApplicationActive,
                Is.EqualTo(wasApplicationActive)
            );
        }
        finally
        {
            maximized.maximized = false;
            await EditorCaptureSource.WaitForNextEditorUpdateAsync();
            maximized.Close();
        }
    }

    [TestCase("editor")]
    [TestCase("window:CaptureProbe")]
    public async Task Screenshot_MaximizedTargetRemainsMaximized(string screenshotTarget)
    {
        RequireInteractiveEditorWindows();
        var target = OpenScreenshotTestWindow<ConduitCaptureProbeWindow>();
        ExpectStaleFallbackWindowErrorsOnLayoutChange();
        target.maximized = true;
        await EditorCaptureSource.WaitForNextEditorUpdateAsync();

        try
        {
            var result = await InvokeScreenshotWithoutDesktopFocusChangeAsync(
                screenshotTarget
            );
            try
            {
                AssertCapturedImagesHaveVisualVariation(result);
                Assert.That(target.maximized, Is.True);
            }
            finally
            {
                DeleteCapturedImages(result);
            }
        }
        finally
        {
            target.maximized = false;
            await EditorCaptureSource.WaitForNextEditorUpdateAsync();
            target.Close();
        }
    }

    static void ExpectStaleFallbackWindowErrorsOnLayoutChange()
    {
        foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
        {
            if (window.GetType().FullName != "UnityEditor.FallbackEditorWindow")
                continue;

            // unity's layout pass reports stale placeholders while maximizing an unrelated valid window
            LogAssert.Expect(
                LogType.Error,
                $"Invalid editor window of type: {window.GetType().FullName}, title: {window.titleContent.text}"
            );
        }
    }
}
