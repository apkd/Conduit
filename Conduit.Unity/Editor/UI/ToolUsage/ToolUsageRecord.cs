#nullable enable

namespace Conduit
{
    readonly struct ToolUsageRecord
    {
        internal readonly string ToolName;
        internal readonly long CallCount;
        internal readonly double AverageDurationMilliseconds;

        internal ToolUsageRecord(string toolName, long callCount, double averageDurationMilliseconds)
        {
            ToolName = toolName;
            CallCount = callCount;
            AverageDurationMilliseconds = averageDurationMilliseconds;
        }
    }
}
