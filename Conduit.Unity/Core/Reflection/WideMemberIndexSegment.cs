#nullable enable

namespace Conduit
{
    readonly struct WideMemberIndexSegment
    {
        internal readonly string AssemblyName;
        internal readonly WideMemberIndexEntry[] Entries;

        internal WideMemberIndexSegment(string assemblyName, WideMemberIndexEntry[] entries)
        {
            AssemblyName = assemblyName;
            Entries = entries;
        }
    }
}
