#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Conduit
{
    static partial class ProfilerTool
    {
        static void Flatten(HierarchyRow root, List<HierarchyRow> rows)
        {
            using var pooledPending = ConduitPool.GetPooledList<HierarchyRow>(out var pending);
            pending.Add(root);
            while (pending.Count > 0)
            {
                var lastIndex = pending.Count - 1;
                var row = pending[lastIndex];
                pending.RemoveAt(lastIndex);
                rows.Add(row);
                for (var index = row.Children.Count - 1; index >= 0; --index)
                    pending.Add(row.Children[index]);
            }
        }

        static string NormalizeIdentitySegment(string name)
            => name.Replace('/', '∕').Trim();

        static string BuildDisplayPath(HierarchyRow row)
        {
            using var pooledAncestors = ConduitPool.GetPooledList<HierarchyRow>(out var ancestors);
            for (var current = row; current != null; current = current.Parent)
                ancestors.Add(current);

            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            for (var index = ancestors.Count - 1; index >= 0; --index)
            {
                if (index < ancestors.Count - 1)
                    builder.Append('/');

                builder.Append(ancestors[index].Name);
            }

            return builder.ToString();
        }

        static uint AppendIdentitySegment(
            uint hash,
            string segment,
            int occurrence,
            bool includeSeparator)
        {
            unchecked
            {
                if (includeSeparator)
                    hash = AppendStableHash(hash, '/');

                hash = AppendStableHash(hash, segment);
                hash = AppendStableHash(hash, '[');
                hash = AppendPositiveIntegerHash(hash, occurrence);
                return AppendStableHash(hash, ']');
            }
        }

        static uint AppendPositiveIntegerHash(uint hash, int value)
        {
            Span<char> digits = stackalloc char[10];
            var offset = digits.Length;
            do
            {
                digits[--offset] = (char)('0' + value % 10);
                value /= 10;
            }
            while (value > 0);

            foreach (var digit in digits[offset..])
                hash = AppendStableHash(hash, digit);

            return hash;
        }

        static uint StableHash(string value)
            => AppendStableHash(2166136261, value);

        static uint AppendStableHash(uint hash, string value)
        {
            foreach (var character in value)
                hash = AppendStableHash(hash, character);

            return hash;
        }

        static uint AppendStableHash(uint hash, char value)
        {
            unchecked
            {
                hash ^= value;
                return hash * 16777619;
            }
        }

        static string StableId(string value) => StableId(StableHash(value));

        static string StableId(uint hash) => hash.ToString("x8", CultureInfo.InvariantCulture);

    }
}
