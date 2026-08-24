namespace Conduit;

static class ProfilerToolModelExtensions
{
    internal static string ToWireName(this ProfilerRecordAction action) =>
        action switch
        {
            ProfilerRecordAction.Capture => "capture",
            ProfilerRecordAction.Save    => "save",
            ProfilerRecordAction.Load    => "load",
            ProfilerRecordAction.List    => "list",
            _                            => action.ToString(),
        };

    internal static string ToWireName(this ProfilerOverviewMode mode) =>
        mode switch
        {
            ProfilerOverviewMode.CpuMs => "cpu_ms",
            ProfilerOverviewMode.GcKb  => "gc_kb",
            _                          => mode.ToString(),
        };

    internal static string ToWireName(this ProfilerBrowseSort sort) =>
        sort switch
        {
            ProfilerBrowseSort.TotalMs => "total_ms",
            ProfilerBrowseSort.SelfMs  => "self_ms",
            ProfilerBrowseSort.GcBytes => "gc_bytes",
            ProfilerBrowseSort.Calls   => "calls",
            _                          => sort.ToString(),
        };
}
