#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{
    /// <summary>Formats execute-code values consistently in the Editor and player.</summary>
    static class BridgeValueFormatter
    {
        const int MaxDepth = 8;
        const int MaxCollectionItems = 1024;

        public static string? Format(object? value)
            => value == null ? null : new Formatter().Format(value, 0);

        sealed class Formatter
        {
            readonly HashSet<object> ancestors = new(ReferenceComparer.Instance);
            int remainingItems = MaxCollectionItems;

            public string Format(object? value, int depth)
            {
                if (value == null)
                    return "null";

                return value switch
                {
                    string text => text,
                    char character => character.ToString(),
                    bool boolean => boolean ? "true" : "false",
                    Enum enumeration => enumeration.ToString(),
                    sbyte or byte or short or ushort or int or uint or long or ulong
                        or float or double or decimal => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                    Object unityObject => FormatUnityObject(unityObject),
                    IDictionary dictionary => FormatDictionary(dictionary, depth),
                    IEnumerable sequence => FormatSequence(sequence, depth),
                    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
                    _ => value.ToString() ?? string.Empty,
                };
            }

            string FormatDictionary(IDictionary dictionary, int depth)
            {
                if (depth >= MaxDepth)
                    return "…";
                if (!ancestors.Add(dictionary))
                    return "<cycle>";

                try
                {
                    var builder = new StringBuilder("{");
                    var first = true;
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (!TryTakeItem(builder, first))
                            break;

                        if (!first)
                            builder.Append(", ");
                        builder.Append(Format(entry.Key, depth + 1))
                            .Append(": ")
                            .Append(Format(entry.Value, depth + 1));
                        first = false;
                    }

                    return builder.Append('}').ToString();
                }
                finally
                {
                    ancestors.Remove(dictionary);
                }
            }

            string FormatSequence(IEnumerable sequence, int depth)
            {
                if (depth >= MaxDepth)
                    return "…";
                if (!ancestors.Add(sequence))
                    return "<cycle>";

                try
                {
                    var builder = new StringBuilder("[");
                    var first = true;
                    foreach (var item in sequence)
                    {
                        if (!TryTakeItem(builder, first))
                            break;

                        if (!first)
                            builder.Append(", ");
                        builder.Append(Format(item, depth + 1));
                        first = false;
                    }

                    return builder.Append(']').ToString();
                }
                finally
                {
                    ancestors.Remove(sequence);
                }
            }

            bool TryTakeItem(StringBuilder builder, bool first)
            {
                if (remainingItems > 0)
                {
                    remainingItems--;
                    return true;
                }

                if (!first)
                    builder.Append(", ");
                builder.Append('…');
                return false;
            }
        }

        static string FormatUnityObject(Object target)
        {
            if (!target)
                return "null";

            var name = target.name.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return $"{target.GetType().Name} \"{name}\" [{BridgeObjectId.Format(target)}]";
        }

        sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new();

            public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);

            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
