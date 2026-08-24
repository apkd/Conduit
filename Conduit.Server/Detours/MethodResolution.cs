namespace Conduit;

readonly record struct MethodResolution(MethodTarget? Target, string? Outcome, string? Diagnostic)
{
    internal static MethodResolution Succeeded(MethodTarget target) => new(target, null, null);
    internal static MethodResolution Failed(string diagnostic) => new(null, ToolOutcome.Exception, diagnostic);
    internal static MethodResolution Ambiguous(string diagnostic) => new(null, ToolOutcome.AmbiguousTarget, diagnostic);
}
