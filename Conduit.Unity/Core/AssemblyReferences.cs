#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Conduit
{
    /// <summary>Reports the managed assemblies available to server-side snippet compilation.</summary>
    static class AssemblyReferences
    {
        public static BridgeCommandResult GetManifest()
        {
            try
            {
                var references = new List<BridgeAssemblyReference>();
                var seen = new HashSet<Guid>();
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (assembly.IsDynamic || string.IsNullOrWhiteSpace(assembly.Location))
                            continue;

                        var mvid = assembly.ManifestModule.ModuleVersionId;
                        if (!seen.Add(mvid))
                            continue;

                        var file = new FileInfo(assembly.Location);
                        references.Add(
                            new()
                            {
                                id = mvid.ToString("N"),
                                assembly_name = assembly.FullName ?? assembly.GetName().Name ?? string.Empty,
                                path = assembly.Location,
                                length = file.Exists ? file.Length : 0,
                            }
                        );
                    }
                    catch (Exception) { }
                }

                references.Sort(static (left, right) => string.Compare(
                    left.assembly_name,
                    right.assembly_name,
                    StringComparison.Ordinal
                ));
                return BridgeCommandResult.Success(
                    JsonUtility.ToJson(
                        new BridgeAssemblyReferenceManifest { references = references.ToArray() }
                    )
                );
            }
            catch (Exception exception)
            {
                return BridgeCommandResult.FromException(exception);
            }
        }

        public static BridgeCommandResult GetAssemblyBlobs(string[] referenceIds)
        {
            if (referenceIds.Length == 0)
                return new()
                {
                    outcome = ToolOutcome.Exception,
                    diagnostic = "No loaded assembly reference IDs were requested.",
                };

            var assemblies = new Dictionary<string, System.Reflection.Assembly>(StringComparer.Ordinal);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic
                        || string.IsNullOrWhiteSpace(assembly.Location))
                        continue;

                    assemblies[assembly.ManifestModule.ModuleVersionId.ToString("N")] = assembly;
                }
                catch (Exception exception)
                {
                    return BridgeCommandResult.FromException(exception);
                }
            }

            var artifacts = new BridgeArtifact[referenceIds.Length];
            for (var index = 0; index < referenceIds.Length; index++)
            {
                var referenceId = referenceIds[index];
                if (!assemblies.TryGetValue(referenceId, out var assembly))
                    return new()
                    {
                        outcome = ToolOutcome.Exception,
                        diagnostic = $"Loaded assembly reference '{referenceId}' was not found.",
                    };

                try
                {
                    artifacts[index] = BridgeArtifact.FromBytes(
                        referenceId + ".dll",
                        "application/vnd.microsoft.portable-executable",
                        File.ReadAllBytes(assembly.Location)
                    );
                }
                catch (Exception exception)
                {
                    return BridgeCommandResult.FromException(exception);
                }
            }

            return new() { artifacts = artifacts };
        }
    }
}
