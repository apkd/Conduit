#nullable enable

using System;
using System.Collections;
using System.Globalization;
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
        [ThreadStatic] static Formatter? reusableFormatter;

        public static string? Format(object? value)
        {
            if (value == null)
                return null;

            if (value is not (IDictionary or IEnumerable) || value is string)
                return FormatSimple(value);

            var formatter = reusableFormatter ?? new();
            reusableFormatter = null;
            try
            {
                return formatter.Format(value, 0);
            }
            finally
            {
                formatter.Reset();
                reusableFormatter ??= formatter;
            }
        }

        static string FormatSimple(object value)
            => value switch
            {
                string text => text,
                char character => character.ToString(),
                bool boolean => boolean ? "true" : "false",
                Enum enumeration => enumeration.ToString(),
                sbyte or byte or short or ushort or int or uint or long or ulong
                    or float or double or decimal => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                Object unityObject => FormatUnityObject(unityObject),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
                _ => value.ToString() ?? string.Empty,
            };

        sealed class Formatter
        {
            readonly object?[] ancestors = new object?[MaxDepth];
            int remainingItems = MaxCollectionItems;

            public string Format(object? value, int depth)
            {
                using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder, 256);
                AppendValue(builder, value, depth);
                return builder.ToString();
            }

            public void Reset()
                => remainingItems = MaxCollectionItems;

            void AppendValue(StringBuilder builder, object? value, int depth)
            {
                if (value == null)
                {
                    builder.Append("null");
                    return;
                }

                switch (value)
                {
                    case string text:
                        builder.Append(text);
                        break;
                    case IDictionary dictionary:
                        AppendDictionary(builder, dictionary, depth);
                        break;
                    case IEnumerable sequence:
                        AppendSequence(builder, sequence, depth);
                        break;
                    default:
                        builder.Append(FormatSimple(value));
                        break;
                }
            }

            void AppendDictionary(StringBuilder builder, IDictionary dictionary, int depth)
            {
                if (depth >= MaxDepth)
                {
                    builder.Append('…');
                    return;
                }
                if (!TryPushAncestor(dictionary, depth))
                {
                    builder.Append("<cycle>");
                    return;
                }

                try
                {
                    builder.Append('{');
                    var first = true;
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (!TryTakeItem(builder, first))
                            break;

                        if (!first)
                            builder.Append(", ");
                        AppendValue(builder, entry.Key, depth + 1);
                        builder.Append(": ");
                        AppendValue(builder, entry.Value, depth + 1);
                        first = false;
                    }

                    builder.Append('}');
                }
                finally
                {
                    ancestors[depth] = null;
                }
            }

            void AppendSequence(StringBuilder builder, IEnumerable sequence, int depth)
            {
                if (depth >= MaxDepth)
                {
                    builder.Append('…');
                    return;
                }
                if (!TryPushAncestor(sequence, depth))
                {
                    builder.Append("<cycle>");
                    return;
                }

                try
                {
                    builder.Append('[');
                    var first = true;
                    foreach (var item in sequence)
                    {
                        if (!TryTakeItem(builder, first))
                            break;

                        if (!first)
                            builder.Append(", ");
                        AppendValue(builder, item, depth + 1);
                        first = false;
                    }

                    builder.Append(']');
                }
                finally
                {
                    ancestors[depth] = null;
                }
            }

            bool TryPushAncestor(object value, int depth)
            {
                for (var index = 0; index < depth; index++)
                    if (ReferenceEquals(ancestors[index], value))
                        return false;

                ancestors[depth] = value;
                return true;
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

    }
}
