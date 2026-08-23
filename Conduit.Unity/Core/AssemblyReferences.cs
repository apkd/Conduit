#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace Conduit
{
    /// <summary>Reports the managed assemblies available to server-side snippet compilation.</summary>
    static class AssemblyReferences
    {
        const int MaximumArtifactReadWorkers = 16;
        static readonly object manifestGate = new();
        static readonly ParallelOptions artifactReadOptions = new()
        {
            MaxDegreeOfParallelism = Math.Min(
                Environment.ProcessorCount,
                MaximumArtifactReadWorkers
            ),
        };
        static string? cachedManifest;
        static Dictionary<string, System.Reflection.Assembly>? cachedAssembliesById;
        static int manifestGeneration;

        static AssemblyReferences()
            => AppDomain.CurrentDomain.AssemblyLoad += static (_, args) => InvalidateManifest(args.LoadedAssembly);

        public static BridgeCommandResult GetManifest()
        {
            try
            {
                lock (manifestGate)
                {
                    EnsureManifest();
                    return BridgeCommandResult.Success(cachedManifest);
                }
            }
            catch (Exception exception)
            {
                return BridgeCommandResult.FromException(exception);
            }
        }

        static void EnsureManifest()
        {
            while (cachedManifest == null)
            {
                var generation = manifestGeneration;
                var manifest = BuildManifest(out var assembliesById);
                if (generation != manifestGeneration)
                    continue;

                cachedManifest = manifest;
                cachedAssembliesById = assembliesById;
            }
        }

        static string BuildManifest(
            out Dictionary<string, System.Reflection.Assembly> assembliesById)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var references = new List<BridgeAssemblyReference>(assemblies.Length);
            var seen = new HashSet<Guid>(assemblies.Length);
            assembliesById = new(assemblies.Length, StringComparer.Ordinal);
            foreach (var assembly in assemblies)
            {
                try
                {
                    if (assembly.IsDynamic)
                        continue;
                    var location = assembly.Location;
                    if (string.IsNullOrWhiteSpace(location))
                        continue;

                    var mvid = assembly.ManifestModule.ModuleVersionId;
                    if (!seen.Add(mvid))
                        continue;

                    var id = mvid.ToString("N");
                    assembliesById.Add(id, assembly);
                    var file = new FileInfo(location);
                    references.Add(
                        new()
                        {
                            id = id,
                            assembly_name = assembly.FullName ?? assembly.GetName().Name ?? string.Empty,
                            path = location,
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
            return JsonUtility.ToJson(
                new BridgeAssemblyReferenceManifest { references = references.ToArray() }
            );
        }

        static void InvalidateManifest(System.Reflection.Assembly assembly)
        {
            try
            {
                // generated snippets have no location and cannot become compiler references.
                if (assembly.IsDynamic)
                    return;
                var location = assembly.Location;
                if (string.IsNullOrWhiteSpace(location))
                    return;
            }
            catch (Exception) { }

            lock (manifestGate)
            {
                cachedManifest = null;
                cachedAssembliesById = null;
                manifestGeneration++;
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

            Dictionary<string, System.Reflection.Assembly> assemblies;
            try
            {
                lock (manifestGate)
                {
                    EnsureManifest();
                    assemblies = cachedAssembliesById!;
                }
            }
            catch (Exception exception)
            {
                return BridgeCommandResult.FromException(exception);
            }

            var selectedAssemblies = new System.Reflection.Assembly[referenceIds.Length];
            for (var index = 0; index < referenceIds.Length; index++)
            {
                var referenceId = referenceIds[index];
                if (!assemblies.TryGetValue(referenceId, out var assembly))
                    return new()
                    {
                        outcome = ToolOutcome.Exception,
                        diagnostic = $"Loaded assembly reference '{referenceId}' was not found.",
                    };

                selectedAssemblies[index] = assembly;
            }

            var artifacts = new BridgeArtifact[referenceIds.Length];
            var failures = new Exception?[referenceIds.Length];
            // file reads and hashes are independent; result arrays retain request order.
            if (referenceIds.Length < 4 || artifactReadOptions.MaxDegreeOfParallelism == 1)
                for (var index = 0; index < referenceIds.Length; index++)
                    ReadArtifact(index);
            else
                Parallel.For(0, referenceIds.Length, artifactReadOptions, ReadArtifact);

            foreach (var failure in failures)
                if (failure != null)
                    return BridgeCommandResult.FromException(failure);

            return new() { artifacts = artifacts };

            void ReadArtifact(int index)
            {
                try
                {
                    artifacts[index] = BridgeArtifact.FromBytes(
                        referenceIds[index] + ".dll",
                        "application/vnd.microsoft.portable-executable",
                        File.ReadAllBytes(selectedAssemblies[index].Location)
                    );
                }
                catch (Exception exception)
                {
                    failures[index] = exception;
                }
            }
        }
    }
}
