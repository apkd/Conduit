#nullable enable

using System;
using System.Collections;
using System.Globalization;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{
    static partial class ShowTool
    {
        static string FormatValue(object? value, int depth)
        {
            if (value == null)
                return "null";

            if (value is string stringValue)
                return FormatString(stringValue);

            if (value is char charValue)
                return FormatString(charValue.ToString());

            if (value is bool boolValue)
                return boolValue ? "true" : "false";

            if (value is Enum)
                return value.ToString();

            if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
                return Convert.ToString(value, CultureInfo.InvariantCulture);

            if (value is Vector2 vector2)
                return FormatVector(vector2.x, vector2.y);

            if (value is Vector3 vector3)
                return FormatVector(vector3.x, vector3.y, vector3.z);

            if (value is Vector4 vector4)
                return FormatVector(vector4.x, vector4.y, vector4.z, vector4.w);

            if (value is Quaternion quaternion)
                return FormatVector(quaternion.x, quaternion.y, quaternion.z, quaternion.w);

            if (value is Color color)
                return $"rgba({color.r:0.###}, {color.g:0.###}, {color.b:0.###}, {color.a:0.###})";

            if (value is Rect rect)
                return $"Rect(x={rect.x:0.###}, y={rect.y:0.###}, w={rect.width:0.###}, h={rect.height:0.###})";

            if (value is Bounds bounds)
                return $"Bounds(center={FormatVector(bounds.center.x, bounds.center.y, bounds.center.z)}, size={FormatVector(bounds.size.x, bounds.size.y, bounds.size.z)})";

            if (value is Object unityObject)
                return DescribeObject(unityObject);

            if (value is IList list)
                return FormatList(list, depth + 1, GetEnumerableElementType(value.GetType()));

            if (TryFormatIndexable(value, depth + 1, out var indexableText))
                return indexableText;

            if (value is IDictionary dictionary)
                return FormatDictionary(dictionary, depth + 1);

            if (value is IEnumerable enumerable)
                return FormatEnumerable(enumerable, depth + 1, GetEnumerableElementType(value.GetType()));

            if (depth < 1)
            {
                if (SummarizeObject(value, depth + 1) is { Length: > 0 } summary)
                    return summary;
            }

            return TrimCompact(value.ToString());
        }

        static string FormatDictionary(IDictionary dictionary, int depth)
        {
            if (dictionary.Count == 0)
                return "{}";

            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            if (dictionary.Count <= MaxCollectionPreview)
                builder.Append('{');
            else
                builder.Append("{count=").Append(dictionary.Count).Append("; first=");

            var count = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (count == MaxCollectionPreview)
                    break;

                if (count++ > 0)
                    builder.Append(", ");

                builder
                    .Append(FormatValue(entry.Key, depth))
                    .Append("=>")
                    .Append(FormatValue(entry.Value, depth));
            }

            return builder.Append('}').ToString();
        }

    }
}
