#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditorInternal;

namespace Conduit
{
    static partial class ProfilerTool
    {
        const int DefaultCaptureFrameCount = 120;
        const int MaxCaptureFrameCount = 600;
        const double DefaultCaptureDelaySeconds = 1;
        const double MaxCaptureDelaySeconds = 60;
        static readonly MethodInfo? setMaxFrameHistoryLengthMethod = typeof(ProfilerDriver).GetMethod(
            "SetMaxFrameHistoryLength",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        static readonly PropertyInfo? configuredFrameHistoryLengthProperty = Type
            .GetType("UnityEditor.Profiling.ProfilerUserSettings,UnityEditor.CoreModule")
            ?.GetProperty("frameCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        static async Task<BridgeCommandResult> CaptureAsync(ProfilerOptions options)
        {
            var frames = options.GetInt(
                "frames",
                DefaultCaptureFrameCount,
                1,
                MaxCaptureFrameCount
            );
            var delaySeconds = options.GetDouble(
                "delay_seconds",
                DefaultCaptureDelaySeconds,
                0,
                MaxCaptureDelaySeconds
            );
            var target = options.GetString("target", "play_mode");
            var fileName = options.GetString("file_name", "");
            if (!TryValidateTarget(target, out var targetDiagnostic))
                return Failure("Unable to capture profile.", targetDiagnostic, null);

            if (delaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

            var previousProfileEditor = ProfilerDriver.profileEditor;
            var previousFrameHistoryLength = GetConfiguredFrameHistoryLength();
            var outputPath = string.IsNullOrWhiteSpace(fileName)
                ? (CapturePath?)null
                : ResolveCapturePath(fileName, allocateDefault: false);
            var boundedCapturePath = outputPath?.AbsolutePath
                ?? Path.Combine(
                    ConduitAssetPathUtility.GetProjectRootPath(),
                    CaptureDirectory,
                    $".capture_{Guid.NewGuid():N}.data"
                );
            var capturedFrames = 0;
            try
            {
                try
                {
                    ProfilerDriver.enabled = false;
                    ProfilerDriver.profileEditor = target == "edit_mode";
                    SetMaxFrameHistoryLength(frames);
                    ProfilerDriver.ClearAllFrames();
                    ProfilerDriver.enabled = true;

                    var deadlineUtc = DateTime.UtcNow + BuildCaptureTimeout(frames);
                    while (CountAvailableFrames() < frames && DateTime.UtcNow < deadlineUtc)
                        await Task.Delay(50);

                    ProfilerDriver.enabled = false;
                    capturedFrames = CountAvailableFrames();
                    if (capturedFrames >= frames)
                        SaveProfile(boundedCapturePath);
                }
                finally
                {
                    ProfilerDriver.enabled = false;
                    ProfilerDriver.profileEditor = previousProfileEditor;
                    SetMaxFrameHistoryLength(previousFrameHistoryLength);
                }

                if (capturedFrames < frames)
                    return Failure("Unable to capture profile.", $"Captured {capturedFrames} of {frames} requested frames before the internal capture deadline.", null);

                if (!ProfilerDriver.LoadProfile(boundedCapturePath, false))
                    return Failure("Unable to capture profile.", "Unity could not restore the bounded profiler history.", outputPath?.DisplayPath);

                capturedFrames = CountAvailableFrames();
                if (outputPath is { } path)
                {
                    return BridgeCommandResult.Success(
                        $"Profile captured and saved!\nFrame count: {capturedFrames.ToString(CultureInfo.InvariantCulture)}\nFile: {path.DisplayPath}"
                    );
                }

                return BridgeCommandResult.Success(
                    $"Profile captured!\nFrame count: {capturedFrames.ToString(CultureInfo.InvariantCulture)}"
                );
            }
            finally
            {
                if (outputPath is null && File.Exists(boundedCapturePath))
                    File.Delete(boundedCapturePath);
            }
        }

        static int GetConfiguredFrameHistoryLength()
            => configuredFrameHistoryLengthProperty?.GetValue(null) is int frameCount
                ? frameCount
                : MaxScanFrameCount;

        static void SetMaxFrameHistoryLength(int frameCount)
            => setMaxFrameHistoryLengthMethod?.Invoke(null, new object[] { frameCount });

        static bool TryValidateTarget(string target, out string diagnostic)
        {
            if (target != "play_mode" && target != "edit_mode")
            {
                diagnostic = "Target must be play_mode or edit_mode.";
                return false;
            }

            if (target == "play_mode" && !EditorApplication.isPlaying)
            {
                diagnostic = "Unity is in edit mode. Enter play mode first or set target=\"edit_mode\".";
                return false;
            }

            if (target == "edit_mode" && EditorApplication.isPlaying)
            {
                diagnostic = "Unity is in play mode. Exit play mode first or set target=\"play_mode\".";
                return false;
            }

            if (EditorApplication.isPaused)
            {
                diagnostic = "Unity is paused. Resume the editor before capturing profiler frames.";
                return false;
            }

            diagnostic = string.Empty;
            return true;
        }

        static TimeSpan BuildCaptureTimeout(int frames)
            => TimeSpan.FromSeconds(Math.Min(120, Math.Max(10, frames / 5.0 + 10)));

    }
}
