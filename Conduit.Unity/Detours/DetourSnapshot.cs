#nullable enable

using System;

namespace Conduit
{
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
