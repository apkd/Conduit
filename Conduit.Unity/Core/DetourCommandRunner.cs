#nullable enable

using System;

namespace Conduit
{
    /// <summary>Executes a validated detour command in either Unity target.</summary>
    static class DetourCommandRunner
    {
        internal static BridgeCommandResult Execute(
            string[] args,
            BridgeArtifact[] artifacts,
            string? generatedTypeName,
            string? displayName,
            Func<BridgeArtifact, byte[]> decode)
        {
            try
            {
                if (args.Length < 6)
                    throw new InvalidOperationException("The MCP server provided an incomplete detour request.");

                BridgeArtifact? assembly = null;
                BridgeArtifact? pdb = null;
                foreach (var artifact in artifacts)
                {
                    if (artifact.name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        assembly ??= artifact;
                    else if (artifact.name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                        pdb ??= artifact;
                }

                return new()
                {
                    display_name = displayName,
                    return_value = DetourRuntime.Execute(
                        args[0],
                        args[1],
                        args[2],
                        args[3],
                        args[4],
                        args[5],
                        assembly == null ? null : decode(assembly),
                        pdb == null ? null : decode(pdb),
                        generatedTypeName,
                        displayName
                    ),
                };
            }
            catch (Exception exception)
            {
                var result = BridgeCommandResult.FromException(exception);
                result.display_name = displayName;
                return result;
            }
        }
    }
}
