#nullable enable

using System;

namespace Conduit
{
    [Serializable]
    sealed class BridgeAssemblyReferenceManifest
    {
        public BridgeAssemblyReference[] references = Array.Empty<BridgeAssemblyReference>();
        public bool preserve_snippets;

        public BridgeAssemblyReference[] References { get => references; set => references = value; }
        public bool PreserveSnippets { get => preserve_snippets; set => preserve_snippets = value; }
    }
}
