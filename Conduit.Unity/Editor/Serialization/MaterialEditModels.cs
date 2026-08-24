#nullable enable

using UnityEditor;

namespace Conduit
{
    readonly struct MaterialDirectEdit
    {
        internal MaterialDirectEdit(
            string path,
            string encodedValue,
            SerializedPropertyType propertyType)
        {
            Path = path;
            EncodedValue = encodedValue;
            PropertyType = propertyType;
        }

        internal string Path { get; }
        internal string EncodedValue { get; }
        internal SerializedPropertyType PropertyType { get; }
    }

    struct MaterialNamedScalarEntry
    {
        internal string? Name { get; set; }
        internal string? EncodedValue { get; set; }
    }

    struct MaterialColorEntry
    {
        internal string? Name { get; set; }
        internal float? R { get; set; }
        internal float? G { get; set; }
        internal float? B { get; set; }
        internal float? A { get; set; }

        internal bool HasAnyChannel => R.HasValue || G.HasValue || B.HasValue || A.HasValue;
    }
}
