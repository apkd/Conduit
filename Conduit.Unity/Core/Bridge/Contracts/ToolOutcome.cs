#nullable enable

namespace Conduit
{
    static class ToolOutcome
    {
        public const string Success = "success";
        public const string Exception = "exception";
        public const string CompileError = "compile_error";
        public const string TestFailed = "test_failed";
        public const string Timeout = "timeout";
        public const string NotConnected = "not_connected";
        public const string DirtyScene = "dirty_scene";
        public const string AmbiguousTarget = "ambiguous_target";
        public const string Cancelled = "cancelled";
    }
}
