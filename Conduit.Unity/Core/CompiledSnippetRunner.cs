#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit
{
    /// <summary>Loads and invokes server-compiled execute-code artifacts.</summary>
    static class CompiledSnippetRunner
    {
        static readonly Dictionary<string, (MethodInfo Method, string DisplayName)> snippets
            = new(StringComparer.OrdinalIgnoreCase);

        public static async Task<BridgeCommandResult> ExecuteAsync(
            BridgeArtifact[] artifacts,
            string? typeName,
            string? requestedDisplayName,
            Func<BridgeArtifact, byte[]> decode,
            CancellationToken ct = default)
        {
            var displayName = requestedDisplayName;
            try
            {
                var assemblyArtifact = artifacts.FirstOrDefault(
                    static artifact => artifact.name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ) ?? throw new InvalidOperationException("The MCP server did not provide a compiled snippet assembly.");

                if (!snippets.TryGetValue(assemblyArtifact.sha256, out var snippet))
                {
                    var pdb = artifacts.FirstOrDefault(
                        static artifact => artifact.name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                    );
                    var assembly = CompiledAssembly.Load(
                        decode(assemblyArtifact),
                        pdb == null ? null : decode(pdb)
                    );
                    var generatedTypeName = typeName ?? string.Empty;
                    var type = assembly.GetType(generatedTypeName, throwOnError: true)
                               ?? throw new TypeLoadException($"Generated snippet type '{generatedTypeName}' was not found.");
                    var method = type.GetMethod(
                        "Execute",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                    ) ?? throw new MissingMethodException(type.FullName, "Execute");
                    snippet = (method, requestedDisplayName ?? assemblyArtifact.name);
                    snippets[assemblyArtifact.sha256] = snippet;
                }

                displayName = snippet.DisplayName;
                ct.ThrowIfCancellationRequested();
                var value = snippet.Method.Invoke(null, null);
                if (value is Task task)
                {
                    await task;
                    value = task.GetType()
                        .GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)
                        ?.GetValue(task, null);
                }

                return new()
                {
                    display_name = displayName,
                    return_value = BridgeValueFormatter.Format(value),
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                exception = exception is TargetInvocationException { InnerException: { } inner }
                    ? inner
                    : exception;
                var result = BridgeCommandResult.FromException(exception);
                result.display_name = displayName;
                return result;
            }
        }
    }
}
