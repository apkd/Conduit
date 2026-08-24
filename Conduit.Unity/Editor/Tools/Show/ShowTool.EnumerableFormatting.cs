#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using static System.StringComparison;

namespace Conduit
{
    static partial class ShowTool
    {
        static string FormatList(IList list, int depth, Type elementType)
        {
            var count = list.Count;
            if (count == 0)
                return elementType == typeof(bool) ? string.Empty : "[]";

            var previewCount = GetPreviewCount(elementType);
            var visibleCount = count <= previewCount ? count : previewCount - 1;
            if (elementType == typeof(bool))
            {
                using var pooledBits = ConduitPool.GetStringBuilder(out var bits);
                for (var index = 0; index < visibleCount; ++index)
                    bits.Append(list[index] is true ? '1' : '0');

                if (count <= previewCount)
                    return bits.ToString();

                bits.Append("...");
                bits.Append(list[count - 1] is true ? '1' : '0');
                bits.Append(" (n=").Append(count).Append(')');
                return bits.ToString();
            }

            using var pooledPreview = ConduitPool.GetStringBuilder(out var preview);
            preview.Append('[');
            var appendedCount = 0;
            for (var index = 0; index < visibleCount; ++index)
                AppendPreviewItem(preview, ref appendedCount, FormatValue(list[index], depth));

            if (count <= previewCount)
                return preview.Append(']').ToString();

            AppendPreviewItem(preview, ref appendedCount, "...");
            AppendPreviewItem(preview, ref appendedCount, FormatValue(list[count - 1], depth));
            return preview.Append("] (n=").Append(count).Append(')').ToString();
        }

        static string FormatEnumerable(IEnumerable enumerable, int depth, Type elementType)
        {
            if (elementType == typeof(bool))
            {
                using var pooledBits = ConduitPool.GetStringBuilder(out var bits);
                var count = 0;
                var previewCount = GetPreviewCount(elementType);
                var lastBit = '0';
                var bitScanLimitReached = false;
                foreach (var item in enumerable)
                {
                    if (count == MaxEnumerableScan)
                    {
                        bitScanLimitReached = true;
                        break;
                    }

                    lastBit = item is true ? '1' : '0';
                    if (count < previewCount)
                        bits.Append(lastBit);

                    count++;
                }

                if (count == 0)
                    return string.Empty;

                if (count <= previewCount)
                    return bits.ToString();

                bits.Length = previewCount - 1;
                if (bitScanLimitReached)
                {
                    bits.Append("... (n>").Append(MaxEnumerableScan).Append(')');
                    return bits.ToString();
                }

                bits.Append("...").Append(lastBit).Append(" (n=").Append(count).Append(')');
                return bits.ToString();
            }

            using var pooledPreview = ConduitPool.GetStringBuilder(out var preview);
            preview.Append('[');
            var appendedCount = 0;
            object? lastItem = null;
            var itemCount = 0;
            var maxPreviewCount = GetPreviewCount(elementType);
            var itemScanLimitReached = false;
            foreach (var item in enumerable)
            {
                if (itemCount == MaxEnumerableScan)
                {
                    itemScanLimitReached = true;
                    break;
                }

                if (itemCount < maxPreviewCount - 1)
                    AppendPreviewItem(preview, ref appendedCount, FormatValue(item, depth));

                lastItem = item;
                ++itemCount;
            }

            if (itemCount == 0)
                return "[]";

            if (itemCount <= maxPreviewCount)
            {
                if (appendedCount < itemCount)
                    AppendPreviewItem(preview, ref appendedCount, FormatValue(lastItem, depth));

                return preview.Append(']').ToString();
            }

            AppendPreviewItem(preview, ref appendedCount, "...");
            if (itemScanLimitReached)
                return preview
                    .Append("] (n>")
                    .Append(MaxEnumerableScan)
                    .Append(')')
                    .ToString();

            AppendPreviewItem(preview, ref appendedCount, FormatValue(lastItem, depth));
            return preview.Append("] (n=").Append(itemCount).Append(')').ToString();
        }

        static void AppendPreviewItem(
            StringBuilder builder,
            ref int itemCount,
            string item)
        {
            if (itemCount++ > 0)
                builder.Append(", ");

            builder.Append(item);
        }

        static Type ResolveDeclaredType(Type rootType, string propertyPath)
        {
            var currentType = rootType;
            var path = propertyPath.AsSpan();
            var segmentStart = 0;
            while (segmentStart <= path.Length)
            {
                var separatorIndex = path[segmentStart..].IndexOf('.');
                var segment = separatorIndex < 0
                    ? path[segmentStart..]
                    : path.Slice(segmentStart, separatorIndex);

                if (segment.SequenceEqual("Array"))
                {
                    currentType = GetEnumerableElementType(currentType) ?? typeof(object);
                }
                else if (segment.StartsWith("data[", Ordinal))
                {
                    currentType = GetEnumerableElementType(currentType) ?? typeof(object);
                }
                else
                {
                    FieldInfo? field = null;
                    foreach (var candidate in GetInspectableFields(currentType))
                    {
                        if (!segment.Equals(candidate.Name, Ordinal))
                            continue;

                        field = candidate;
                        break;
                    }

                    if (field == null)
                        return typeof(object);

                    currentType = field.FieldType;
                }

                if (separatorIndex < 0)
                    break;

                segmentStart += separatorIndex + 1;
            }

            return currentType;
        }

        static Type GetEnumerableElementType(Type type)
            => enumerableElementTypeCache.GetOrAdd(type, static value =>
            {
                if (value.IsArray)
                    return value.GetElementType() ?? typeof(object);

                if (value.IsGenericType)
                {
                    var arguments = value.GetGenericArguments();
                    if (arguments.Length == 1)
                        return arguments[0];
                }

                foreach (var candidate in value.GetInterfaces())
                    if (candidate.IsGenericType
                        && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                        return candidate.GetGenericArguments()[0];

                return typeof(object);
            });

        static int GetPreviewCount(Type elementType)
        {
            if (elementType == typeof(bool))
                return 512;

            if (elementType == typeof(byte) || elementType == typeof(sbyte))
                return 128;

            if (elementType == typeof(short) || elementType == typeof(ushort))
                return 64;

            if (elementType == typeof(int) || elementType == typeof(uint))
                return 32;

            if (elementType == typeof(long) || elementType == typeof(ulong))
                return 16;

            return 8;
        }

    }
}
