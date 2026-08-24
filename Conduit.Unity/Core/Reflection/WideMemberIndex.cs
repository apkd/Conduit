#nullable enable

using System;

namespace Conduit
{
    sealed class WideMemberIndex
    {
        internal volatile WideMemberIndexSegment[] Segments;

        public WideMemberIndex(WideMemberIndexSegment[] initial) => Segments = initial;

        internal void AddSegments(WideMemberIndexSegment[] added)
        {
            var current = Segments;
            // loaded assemblies normally add distinct segments, so the upper bound is exact in the common case
            var merged = new WideMemberIndexSegment[current.Length + added.Length];
            var currentIndex = 0;
            var addedIndex = 0;
            var mergedIndex = 0;
            while (currentIndex < current.Length && addedIndex < added.Length)
            {
                var comparison = string.Compare(
                    current[currentIndex].AssemblyName,
                    added[addedIndex].AssemblyName,
                    StringComparison.Ordinal
                );
                if (comparison < 0)
                    merged[mergedIndex++] = current[currentIndex++];
                else if (comparison > 0)
                    merged[mergedIndex++] = added[addedIndex++];
                else
                    merged[mergedIndex++] = Merge(current[currentIndex++], added[addedIndex++]);
            }

            while (currentIndex < current.Length)
                merged[mergedIndex++] = current[currentIndex++];
            while (addedIndex < added.Length)
                merged[mergedIndex++] = added[addedIndex++];
            if (mergedIndex != merged.Length)
                Array.Resize(ref merged, mergedIndex);
            Segments = merged;

            static WideMemberIndexSegment Merge(
                WideMemberIndexSegment left,
                WideMemberIndexSegment right)
            {
                var entries = new WideMemberIndexEntry[left.Entries.Length + right.Entries.Length];
                var leftIndex = 0;
                var rightIndex = 0;
                var destinationIndex = 0;
                while (leftIndex < left.Entries.Length && rightIndex < right.Entries.Length)
                    entries[destinationIndex++] = ReflectionQueryEngine.CompareWideMemberEntries(
                        left.Entries[leftIndex],
                        right.Entries[rightIndex]
                    ) <= 0
                        ? left.Entries[leftIndex++]
                        : right.Entries[rightIndex++];

                while (leftIndex < left.Entries.Length)
                    entries[destinationIndex++] = left.Entries[leftIndex++];
                while (rightIndex < right.Entries.Length)
                    entries[destinationIndex++] = right.Entries[rightIndex++];
                return new(left.AssemblyName, entries);
            }
        }
    }
}
