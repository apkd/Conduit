#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;
using Object = UnityEngine.Object;

namespace Conduit.Runtime
{
    public static partial class ConduitRuntimeSearch
    {
        internal static Object ResolveOne(string query)
            => SelectSingle<Object>(query, ResolveMany(query, null, MaxSelectionResults));

        internal static string FormatMatches(IReadOnlyList<Object> matches, bool includeHint)
        {
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            for (var index = 0; index < Math.Min(matches.Count, MaxFormattedResults); index++)
            {
                var match = matches[index];
                builder.Append("- ")
                    .Append(match.name)
                    .Append(" (")
                    .Append(match.GetType().Name)
                    .Append(") | ");
                AppendLocation(builder, match);
                builder.Append(" | ")
                    .Append(BridgeObjectId.Format(match))
                    .AppendLine();
            }

            if (matches.Count > MaxFormattedResults)
            {
                builder.AppendLine();
                builder.Append("Showing the first ")
                    .Append(MaxFormattedResults)
                    .AppendLine(" results; additional matches were omitted.");
                builder.AppendLine("More specific queries return a narrower result set.");
            }

            if (includeHint && matches.Count > 1)
            {
                builder.AppendLine();
                builder.AppendLine("Multiple objects match your query.");
                builder.Append("Rerun with ")
                    .Append(BridgeObjectId.Prefix)
                    .AppendLine("<number> to select a specific match.");
            }

            while (builder.Length > 0 && builder[^1] is '\r' or '\n')
                --builder.Length;
            return builder.ToString();
        }

        static List<T> Filter<T>(IReadOnlyList<Object> objects) where T : Object
        {
            var results = new List<T>(objects.Count);
            List<Component>? components = null;
            var includesComponents = typeof(Component).IsAssignableFrom(typeof(T));
            try
            {
                foreach (var candidate in objects)
                {
                    if (candidate is T typed)
                    {
                        results.Add(typed);
                        continue;
                    }

                    if (candidate is not GameObject gameObject || !includesComponents)
                        continue;

                    components ??= ListPool<Component>.Get();
                    gameObject.GetComponents(typeof(T), components);
                    foreach (var component in components)
                        if (component is T typedComponent)
                            results.Add(typedComponent);
                    components.Clear();
                }
            }
            finally
            {
                if (components != null)
                    ListPool<Component>.Release(components);
            }

            return results;
        }

        static T SelectSingle<T>(string query, IReadOnlyList<T> values) where T : Object
        {
            if (values.Count == 0)
                throw new InvalidOperationException($"No matches for '{query}'.");

            if (values.Count == 1)
                return values[0];

            var count = Math.Min(values.Count, MaxFormattedResults + 1);
            using var pooledMatches = ListPool<Object>.Get(out var matches);
            matches.Clear();
            if (matches.Capacity < count)
                matches.Capacity = count;
            for (var index = 0; index < count; index++)
                matches.Add(values[index]);

            throw new InvalidOperationException(FormatMatches(matches, includeHint: true));
        }

        static int CompareResults(SearchResult left, SearchResult right)
        {
            var name = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            return name != 0
                ? name
                : left.ObjectId.CompareTo(right.ObjectId);
        }

        static void BuildMaxHeap(List<SearchResult> results)
        {
            for (var index = results.Count / 2 - 1; index >= 0; --index)
                SiftDown(results, index);
        }

        static void SiftDown(List<SearchResult> results, int index)
        {
            while (true)
            {
                var left = index * 2 + 1;
                if (left >= results.Count)
                    return;

                var right = left + 1;
                var worse = right < results.Count
                            && CompareResults(results[right], results[left]) > 0
                    ? right
                    : left;
                if (CompareResults(results[worse], results[index]) <= 0)
                    return;

                (results[index], results[worse]) = (results[worse], results[index]);
                index = worse;
            }
        }

        readonly struct SearchResult
        {
            internal readonly Object Target;
            internal readonly string Name;
            internal readonly ulong ObjectId;

            internal SearchResult(Object target, string name, ulong objectId)
            {
                Target = target;
                Name = name;
                ObjectId = objectId;
            }
        }
    }
}
