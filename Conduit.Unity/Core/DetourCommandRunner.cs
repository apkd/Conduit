#nullable enable

using System;
using System.Linq;

namespace Conduit
{
    /// <summary>Executes a validated detour command in either Unity target.</summary>
    static class DetourCommandRunner
    {
        public static BridgeCommandResult Execute(
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

                var assembly = artifacts.FirstOrDefault(
                    static artifact => artifact.name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                );
                var pdb = artifacts.FirstOrDefault(
                    static artifact => artifact.name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                );
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
