#nullable enable

using System;
using System.Globalization;
using UnityEditor;
using Object = UnityEngine.Object;

namespace Conduit
{
    static class SerializedJsonValueDecoder
    {
        internal static void ValidateEditablePersistentAsset(Object target)
        {
            if (!EditorUtility.IsPersistent(target))
                throw new InvalidOperationException("Resolved object is not persistent and could not be treated as a scene or prefab target.");

            if (!AssetDatabase.IsNativeAsset(target))
                throw new InvalidOperationException(
                    $"Target '{AssetDatabase.GetAssetPath(target)}' is not a native editable asset and cannot be overwritten safely."
                );
        }

        internal static void ValidateDirectMaterialProperty(string path, string encodedValue, SerializedPropertyType propertyType)
        {
            switch (propertyType)
            {
                case SerializedPropertyType.String:
                    DecodeString(path, encodedValue);
                    return;
                case SerializedPropertyType.Boolean:
                    DecodeBool(path, encodedValue);
                    return;
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.Enum:
                    DecodeInt(path, encodedValue);
                    return;
                default:
                    throw new InvalidOperationException($"Material overwrite does not support direct property '{path}'.");
            }
        }

        internal static bool TryParseIndexedChildPath(string path, string collectionPath, out int index, out string childPath)
        {
            index = -1;
            childPath = string.Empty;
            if (!path.StartsWith(collectionPath, StringComparison.Ordinal))
                return false;

            var cursor = collectionPath.Length;
            if (cursor >= path.Length || path[cursor] != '[')
                return false;

            cursor++;
            var indexStart = cursor;
            while (cursor < path.Length && char.IsDigit(path[cursor]))
                cursor++;

            if (cursor == indexStart
                || cursor >= path.Length
                || path[cursor] != ']'
                || !int.TryParse(path.AsSpan(indexStart, cursor - indexStart), NumberStyles.None, CultureInfo.InvariantCulture, out index))
                return false;

            cursor++;
            if (cursor == path.Length)
                return true;

            if (path[cursor] != '.')
                return false;

            childPath = path[(cursor + 1)..];
            return true;
        }

        internal static string DecodeString(string path, string encodedValue)
        {
            if (!encodedValue.StartsWith(SerializedJsonDiff.StringValuePrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Material overwrite expected a string at '{path}'.");

            return encodedValue[SerializedJsonDiff.StringValuePrefix.Length..];
        }

        internal static bool DecodeBool(string path, string encodedValue)
        {
            if (!encodedValue.StartsWith(SerializedJsonDiff.PrimitiveValuePrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Material overwrite expected a boolean at '{path}'.");

            return encodedValue[SerializedJsonDiff.PrimitiveValuePrefix.Length..] switch
            {
                "true"  => true,
                "false" => false,
                _       => throw new InvalidOperationException($"Material overwrite expected a boolean at '{path}'."),
            };
        }

        internal static int DecodeInt(string path, string encodedValue)
        {
            if (!encodedValue.StartsWith(SerializedJsonDiff.PrimitiveValuePrefix, StringComparison.Ordinal)
                || !int.TryParse(
                    encodedValue.AsSpan(SerializedJsonDiff.PrimitiveValuePrefix.Length),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var value))
                throw new InvalidOperationException($"Material overwrite expected an integer at '{path}'.");

            return value;
        }

        internal static float DecodeFloat(string path, string encodedValue)
        {
            if (!encodedValue.StartsWith(SerializedJsonDiff.PrimitiveValuePrefix, StringComparison.Ordinal)
                || !float.TryParse(
                    encodedValue.AsSpan(SerializedJsonDiff.PrimitiveValuePrefix.Length),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value))
                throw new InvalidOperationException($"Material overwrite expected a number at '{path}'.");

            return value;
        }
    }
}
