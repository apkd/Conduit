#nullable enable

using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit.Runtime
{
    public static partial class ConduitRuntimeSearch
    {
        static bool TryResolveObjectId(string query, out Object? value)
        {
            value = null;
            var prefixLength = query.StartsWith("eid:", StringComparison.OrdinalIgnoreCase)
                || query.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
                ? query.IndexOf(':') + 1
                : 0;
            if (prefixLength == 0
                || !BridgeObjectId.TryParse(query.AsSpan(prefixLength), out var objectId))
                return false;

            value = BridgeObjectId.Resolve(objectId);
            if (value != null && !IsSupportedType(value))
                value = null;
            return true;
        }

        static void ParseQuery(
            string query,
            out Type? requestedType,
            out string nameQuery,
            out bool unresolvedType)
        {
            requestedType = null;
            unresolvedType = false;
            if (query.IndexOf(' ') < 0
                && !query.StartsWith("t:", StringComparison.OrdinalIgnoreCase)
                && !query.StartsWith("t=", StringComparison.OrdinalIgnoreCase))
            {
                nameQuery = query[0] == '+' ? query[1..] : query;
                return;
            }

            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            var partCount = 0;
            var offset = 0;
            while (offset < query.Length)
            {
                while (offset < query.Length && query[offset] == ' ')
                    ++offset;
                if (offset == query.Length)
                    break;

                var start = offset;
                while (offset < query.Length && query[offset] != ' ')
                    ++offset;
                var length = offset - start;
                var part = query.AsSpan(start, length);
                if ((part.StartsWith("t:".AsSpan(), StringComparison.OrdinalIgnoreCase)
                     || part.StartsWith("t=".AsSpan(), StringComparison.OrdinalIgnoreCase))
                    && length > 2)
                {
                    requestedType = ConduitReflect.ResolveType(query.Substring(start + 2, length - 2));
                    unresolvedType |= requestedType == null;
                    continue;
                }

                while (length > 0 && query[start] == '+')
                {
                    ++start;
                    --length;
                }

                if (partCount++ > 0)
                    builder.Append(' ');
                builder.Append(query, start, length);
            }

            nameQuery = builder.ToString();
        }

        static bool MatchesType(Object candidate, Type requestedType)
        {
            if (requestedType.IsInstanceOfType(candidate))
                return true;

            return candidate is GameObject gameObject
                   && typeof(Component).IsAssignableFrom(requestedType)
                   && gameObject.GetComponent(requestedType) != null;
        }

        static bool IsInspectable(Object candidate) =>
            (candidate.hideFlags & HideFlags.HideAndDontSave) == 0 && IsSupportedType(candidate);

        static bool IsSupportedType(Object candidate) =>
            candidate is GameObject
            or Component
            or ScriptableObject
            or Texture
            or Material
            or Mesh
#if MODULE_ANIMATION
            or AnimationClip
            or RuntimeAnimatorController
#endif
            ;

    }
}

