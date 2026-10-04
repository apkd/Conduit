#nullable enable

using System;

namespace Conduit
{
    /// <summary>Inspects loaded metadata and managed bodies without running the inspected code.</summary>
    static class ViewIlTool
    {
        internal static BridgeCommandResult Execute(string? target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return BridgeCommandResult.Error("Specify a type or member, e.g. Game.Agent::Update(float).");

            try
            {
                var matches = ReflectionQueryEngine.ResolveTarget(target);
                return matches.Length switch
                {
                    0 => BridgeCommandResult.Error($"No type or member matched '{target}'."),
                    1 => BridgeCommandResult.Success(IlFormatter.Format(matches[0])),
                    _ => BridgeCommandResult.Ambiguous(ReflectionMemberFormatter.TargetCandidates(matches, 25)),
                };
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                              or NotSupportedException or TypeLoadException or BadImageFormatException)
            {
                return BridgeCommandResult.Error($"Could not inspect IL for '{target}': {exception.Message}");
            }
        }

    }
}
