#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{
    static partial class SerializedProjectSettingsProvider
    {
        static bool IsSupportedLeaf(SerializedProperty property)
            => IsGuid(property)
               || property.propertyType is SerializedPropertyType.Integer
                   or SerializedPropertyType.Boolean
                   or SerializedPropertyType.Float
                   or SerializedPropertyType.String
                   or SerializedPropertyType.Color
                   or SerializedPropertyType.ObjectReference
                   or SerializedPropertyType.LayerMask
                   or SerializedPropertyType.Enum
                   or SerializedPropertyType.Vector2
                   or SerializedPropertyType.Vector3
                   or SerializedPropertyType.Vector4
                   or SerializedPropertyType.Rect
                   or SerializedPropertyType.AnimationCurve
                   or SerializedPropertyType.Bounds
                   or SerializedPropertyType.Quaternion
                   or SerializedPropertyType.Vector2Int
                   or SerializedPropertyType.Vector3Int
                   or SerializedPropertyType.RectInt
                   or SerializedPropertyType.BoundsInt
                   or SerializedPropertyType.ArraySize
                   or SerializedPropertyType.Gradient
                   or SerializedPropertyType.ExposedReference
                   or SerializedPropertyType.FixedBufferSize
                   or SerializedPropertyType.Hash128
                   or SerializedPropertyType.RenderingLayerMask;

        static bool IsSupportedArrayElement(SerializedProperty array)
        {
            if (array.arraySize > 0)
            {
                var element = array.GetArrayElementAtIndex(0);
                return IsSupportedLeaf(element) || element.propertyType == SerializedPropertyType.Generic;
            }

            string elementType = array.arrayElementType;
            return elementType.Length > 0
                   && !elementType.Contains("managedReference", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsCompoundArrayElement(SerializedProperty property)
            => property.propertyType == SerializedPropertyType.Generic
               && IsArrayElementPath(property.propertyPath);

        static bool IsArrayElementPath(string path)
            => TryGetArrayElementParts(path, out _, out _);

        static bool TryParseArrayElementPath(string path, out string arrayPath, out int index)
        {
            if (TryGetArrayElementParts(path, out var marker, out var indexText)
                && int.TryParse(
                    indexText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out index
                ))
            {
                arrayPath = path[..marker];
                return true;
            }

            arrayPath = string.Empty;
            index = 0;
            return false;
        }

        static bool TryGetArrayElementParts(
            string path,
            out int marker,
            out ReadOnlySpan<char> indexText)
        {
            marker = path.LastIndexOf(ArrayElementMarker, StringComparison.Ordinal);
            int indexStart = marker + ArrayElementMarker.Length;
            if (marker <= 0 || indexStart >= path.Length || path[^1] != ']')
            {
                indexText = default;
                return false;
            }

            indexText = path.AsSpan(indexStart, path.Length - indexStart - 1);
            foreach (var character in indexText)
                if (character is < '0' or > '9')
                    return false;

            return indexText.Length > 0;
        }

        static IEnumerable<SerializedProperty> GetDirectChildren(SerializedProperty property)
        {
            var iterator = property.Copy();
            var end = iterator.GetEndProperty();
            if (!iterator.NextVisible(true))
                yield break;

            while (!SerializedProperty.EqualContents(iterator, end) && iterator.depth > property.depth)
            {
                if (iterator.depth == property.depth + 1)
                    yield return iterator.Copy();
                if (!iterator.NextVisible(false))
                    yield break;
            }
        }

        static bool ShouldSkip(string path)
        {
            string key = ProjectSettingKey.Canonicalize(path);
            var keySpan = key.AsSpan();
            // implementation metadata, obsolete systems, telemetry, and credentials are outside this tool's ownership.
            return key is "m_object_hide_flags" or "m_script"
                   || ContainsToken(keySpan, "serialized") && ContainsToken(keySpan, "version")
                   || ContainsToken(keySpan, "obsolete", matchPrefix: true)
                   || ContainsToken(keySpan, "deprecated", matchPrefix: true)
                   || key is "m_asset_version" or "m_last_material_version"
                   || key.Contains("vr_settings", StringComparison.Ordinal)
                   || key.Contains("virtual_reality", StringComparison.Ordinal)
                   || key.Contains("submit_analytics", StringComparison.Ordinal)
                   || key.Contains("password", StringComparison.Ordinal)
                   || key.Contains("keyalias_pass", StringComparison.Ordinal)
                   || key.Contains("keystore_pass", StringComparison.Ordinal)
                   || key.Contains("credential", StringComparison.Ordinal)
                   || key.Contains("access_token", StringComparison.Ordinal)
                   || key.Contains("auth_token", StringComparison.Ordinal);

            static bool ContainsToken(
                ReadOnlySpan<char> value,
                ReadOnlySpan<char> token,
                bool matchPrefix = false)
            {
                int segmentStart = 0;
                for (int index = 0; index <= value.Length; ++index)
                {
                    if (index < value.Length && value[index] is not ('.' or '_'))
                        continue;

                    var segment = value[segmentStart..index];
                    if (matchPrefix
                            ? segment.StartsWith(token, StringComparison.Ordinal)
                            : segment.Equals(token, StringComparison.Ordinal))
                        return true;
                    segmentStart = index + 1;
                }

                return false;
            }
        }

        static bool IsReadOnly(SerializedProperty property)
        {
            string key = ProjectSettingKey.Canonicalize(property.propertyPath);
            return !property.editable
                   || property.propertyType == SerializedPropertyType.FixedBufferSize
                   || IsGuid(property)
                   || key is "product_guid" or "project_guid"
                   || key.StartsWith("product_guid.", StringComparison.Ordinal)
                   || key.StartsWith("project_guid.", StringComparison.Ordinal);
        }

        static bool IsGuid(SerializedProperty property)
            => guidValueProperty != null && (int)property.propertyType == guidPropertyType;

        internal static string ToKey(string propertyPath)
        {
            Span<char> normalized = propertyPath.Length <= 512
                ? stackalloc char[propertyPath.Length]
                : new char[propertyPath.Length];
            int outputLength = 0;
            int inputIndex = 0;
            while (inputIndex < propertyPath.Length)
            {
                if (propertyPath.AsSpan(inputIndex).StartsWith(ArrayElementMarker, StringComparison.Ordinal))
                {
                    normalized[outputLength++] = '.';
                    inputIndex += ArrayElementMarker.Length;
                    continue;
                }

                if (propertyPath[inputIndex] == ']')
                {
                    inputIndex++;
                    continue;
                }

                if ((outputLength == 0 || normalized[outputLength - 1] == '.')
                    && propertyPath.AsSpan(inputIndex).StartsWith("m_", StringComparison.Ordinal))
                {
                    inputIndex += 2;
                    continue;
                }

                normalized[outputLength++] = propertyPath[inputIndex++];
            }

            return ProjectSettingKey.Canonicalize(normalized[..outputLength]);
        }

    }
}
