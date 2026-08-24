using System.Globalization;
using CT = System.Threading.CancellationToken;

namespace Conduit;

public sealed partial class UnityProjectOperations
{
    public Task<ToolExecutionResult> ProfilerRecordAsync(
        string projectPath,
        ProfilerRecordAction action,
        int frames,
        double delaySeconds,
        string target,
        string? fileName,
        CT ct
    ) => EnqueueAsync(
        projectPath,
        new()
        {
            CommandType = BridgeCommandTypes.ProfilerRecord,
            Args = BuildProfilerRecordArgs(action, frames, delaySeconds, target, fileName),
        },
        ct
    );

    internal static string[] BuildProfilerRecordArgs(
        ProfilerRecordAction action,
        int frames,
        double delaySeconds,
        string target,
        string? fileName
    ) =>
    [
        $"action={action.ToWireName()}",
        $"frames={frames}",
        $"delay_seconds={delaySeconds.ToString(CultureInfo.InvariantCulture)}",
        $"target={target}",
        $"file_name={fileName ?? string.Empty}",
    ];

    public Task<ToolExecutionResult> ProfilerOverviewAsync(
        string projectPath,
        ProfilerOverviewMode mode,
        string frameRange,
        CT ct
    ) => EnqueueAsync(
        projectPath,
        new()
        {
            CommandType = BridgeCommandTypes.ProfilerOverview,
            Args =
            [
                $"mode={mode.ToWireName()}",
                $"frame_range={frameRange}",
            ],
        },
        ct
    );

    public Task<ToolExecutionResult> ProfilerBrowseAsync(
        string projectPath,
        string frame,
        string thread,
        string root,
        int depth,
        ProfilerBrowseSort sort,
        int limit,
        bool onlyNonTrivial,
        CT ct
    ) => EnqueueAsync(
        projectPath,
        new()
        {
            CommandType = BridgeCommandTypes.ProfilerBrowse,
            Args =
            [
                $"frame={frame}",
                $"thread={thread}",
                $"root={root}",
                $"depth={depth}",
                $"sort={sort.ToWireName()}",
                $"limit={limit}",
                $"only_non_trivial={(onlyNonTrivial ? "true" : "false")}",
            ],
        },
        ct
    );
}
