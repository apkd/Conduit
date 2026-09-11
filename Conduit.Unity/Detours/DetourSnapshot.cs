#nullable enable

using System;

namespace Conduit
{
    /// <summary>Retains an MCP replacement's compiled bytes and target identity so it can survive domain reload.</summary>
    /// <remarks>Native pointers and MethodInfo instances belong to the old domain. Reapplication resolves the
    /// recorded module version and token, then loads these bytes to create a new replacement in the new domain.</remarks>
    sealed class DetourSnapshot
    {
        internal string ModuleVersionId = string.Empty;
        internal string MetadataToken = string.Empty;
        internal string SignatureHash = string.Empty;
        internal string CanonicalName = string.Empty;
        internal string Declaration = string.Empty;
        internal byte[] AssemblyBytes = Array.Empty<byte>();
        internal byte[]? PdbBytes;
        internal string GeneratedTypeName = string.Empty;
        internal string DisplayName = string.Empty;
    }
}
