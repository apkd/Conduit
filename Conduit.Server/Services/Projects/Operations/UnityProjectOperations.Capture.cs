using System.Globalization;
using CT = System.Threading.CancellationToken;

namespace Conduit;

public sealed partial class UnityProjectOperations
{
    public Task<ToolExecutionResult> ScreenshotAsync(string projectPath, string target, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.Screenshot, Target = target },
            ct: ct
        );

    public Task<ToolExecutionResult> RecordAsync(
        string projectPath,
        string target,
        float durationSeconds,
        bool adjustDeltaTime,
        int frameRate,
        float resolutionScale,
        string format,
        int crf,
        CT ct
    ) => EnqueueAsync(
        projectPath,
        new()
        {
            CommandType = BridgeCommandTypes.Record,
            Target = target,
            Args = BuildRecordArgs(
                durationSeconds,
                adjustDeltaTime,
                frameRate,
                resolutionScale,
                format,
                crf
            ),
        },
        ct
    );

    internal static string[] BuildRecordArgs(
        float durationSeconds,
        bool adjustDeltaTime,
        int frameRate,
        float resolutionScale,
        string format,
        int crf
    ) =>
    [
        $"duration_seconds={durationSeconds.ToString(CultureInfo.InvariantCulture)}",
        $"adjust_delta_time={(adjustDeltaTime ? "true" : "false")}",
        $"frame_rate={frameRate.ToString(CultureInfo.InvariantCulture)}",
        $"resolution_scale={resolutionScale.ToString(CultureInfo.InvariantCulture)}",
        $"format={format}",
        $"crf={crf.ToString(CultureInfo.InvariantCulture)}",
    ];
}
