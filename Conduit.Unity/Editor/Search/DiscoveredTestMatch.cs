#nullable enable

namespace Conduit
{
    readonly struct DiscoveredTestMatch
    {
        internal DiscoveredTestMatch(string name, TestSearchMode mode)
        {
            Name = name;
            Mode = mode;
        }

        internal string Name { get; }
        internal TestSearchMode Mode { get; }
    }
}
