#nullable enable

using System;

namespace Conduit
{
    [Serializable]
    sealed class BridgeAssemblyReference
    {
        public string id = string.Empty;
        public string assembly_name = string.Empty;
        public string path = string.Empty;
        public long length;

        public string Id { get => id; set => id = value; }
        public string AssemblyName { get => assembly_name; set => assembly_name = value; }
        public string Path { get => path; set => path = value; }
        public long Length { get => length; set => length = value; }
    }
}
