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

        public static BridgeCommandResult GetAssemblyBlob(string? referenceId)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic
                        || assembly.ManifestModule.ModuleVersionId.ToString("N") != referenceId
                        || string.IsNullOrWhiteSpace(assembly.Location))
                        continue;

                    return new()
                    {
                        artifacts = new[]
                        {
                            BridgeArtifact.FromBytes(
                                Path.GetFileName(assembly.Location),
                                "application/vnd.microsoft.portable-executable",
                                File.ReadAllBytes(assembly.Location)
                            ),
                        },
                    };
                }
                catch (Exception exception)
                {
                    return BridgeCommandResult.FromException(exception);
                }
            }

            return new()
            {
                outcome = ToolOutcome.Exception,
                diagnostic = $"Loaded assembly reference '{referenceId}' was not found.",
            };
        }
    }
}
