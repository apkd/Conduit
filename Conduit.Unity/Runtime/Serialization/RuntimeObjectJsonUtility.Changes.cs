#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine.Pool;

namespace Conduit.Runtime
{
    static partial class RuntimeObjectJsonUtility
    {
        static string FormatChanges(string before, string after, HashSet<string> requestedPaths)
        {
            using var pooledChanged = ListPool<string>.Get(out var changed);
            changed.Clear();
            CollectChangedPaths(RuntimeJsonObject.Parse(before), RuntimeJsonObject.Parse(after), string.Empty, changed);
            var retainedCount = 0;
            for (var index = 0; index < changed.Count; ++index)
            {
                var path = changed[index];
                if (IsRequestedPath(path))
                    changed[retainedCount++] = path;
            }
            if (retainedCount < changed.Count)
                changed.RemoveRange(retainedCount, changed.Count - retainedCount);
            if (changed.Count == 0)
                return "No serialized properties changed.";

            changed.Sort(StringComparer.Ordinal);
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            builder.Append("Applied changes:");
            foreach (var path in changed)
                builder.Append("\n- ").Append(path);

            return builder.ToString();

            bool IsRequestedPath(string path)
            {
                if (requestedPaths.Contains(path))
                    return true;

                foreach (var requested in requestedPaths)
                    if (IsNestedPath(path, requested) || IsNestedPath(requested, path))
                        return true;

                return false;
            }
        }

        static bool IsNestedPath(string path, string prefix)
            => path.Length > prefix.Length
               && path.StartsWith(prefix, StringComparison.Ordinal)
               && path[prefix.Length] == '.';

        static bool IsJsonObject(string json)
        {
            foreach (var character in json)
            {
                if (char.IsWhiteSpace(character))
                    continue;

                return character == '{';
            }

            return false;
        }

        static void CollectChangedPaths(
            RuntimeJsonObject before,
            RuntimeJsonObject after,
            string prefix,
            List<string> changed)
        {
            using var pooledAfterMembers = DictionaryPool<string, RuntimeJsonMember>.Get(
                out var afterMembers
            );
            afterMembers.Clear();
            afterMembers.EnsureCapacity(after.Members.Count);
            foreach (var member in after.Members)
                afterMembers.Add(member.Name, member);

            foreach (var beforeMember in before.Members)
            {
                var name = beforeMember.Name;
                var path = prefix.Length == 0 ? name : prefix + "." + name;
                if (!afterMembers.Remove(name, out var afterMember))
                {
                    changed.Add(path);
                    continue;
                }

                if (beforeMember.Source == afterMember.Source)
                    continue;
                if (beforeMember.IsObject && afterMember.IsObject)
                    CollectChangedPaths(
                        RuntimeJsonObject.Parse(beforeMember.Source),
                        RuntimeJsonObject.Parse(afterMember.Source),
                        path,
                        changed
                    );
                else
                    changed.Add(path);
            }

            foreach (var name in afterMembers.Keys)
                changed.Add(prefix.Length == 0 ? name : prefix + "." + name);
        }
    }
}
