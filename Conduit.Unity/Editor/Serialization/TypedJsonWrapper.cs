#nullable enable

using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{
    static class TypedJsonWrapper
    {
        internal static bool TryUnwrap(Object target, string json, out string unwrappedJson, out string? wrapperTypeName)
        {
            unwrappedJson = string.Empty;
            wrapperTypeName = null;
            var index = 0;
            JsonSyntaxReader.SkipWhitespace(json, ref index);
            if (!JsonSyntaxReader.TryConsume(json, ref index, '{'))
                return false;

            JsonSyntaxReader.SkipWhitespace(json, ref index);
            if (JsonSyntaxReader.TryConsume(json, ref index, '}'))
                return false;

            if (!JsonSyntaxReader.TryReadJsonString(json, ref index, out var propertyName))
                return false;

            JsonSyntaxReader.SkipWhitespace(json, ref index);
            if (!JsonSyntaxReader.TryConsume(json, ref index, ':'))
                return false;

            JsonSyntaxReader.SkipWhitespace(json, ref index);
            var valueStart = index;
            if (index >= json.Length || json[index] != '{' || !JsonSyntaxReader.TrySkipJsonValue(json, ref index))
                return false;

            var valueEnd = index;
            JsonSyntaxReader.SkipWhitespace(json, ref index);
            if (JsonSyntaxReader.TryConsume(json, ref index, ',') || !JsonSyntaxReader.TryConsume(json, ref index, '}'))
                return false;

            JsonSyntaxReader.SkipWhitespace(json, ref index);
            if (index != json.Length)
                return false;

            if (!LooksLikeTypeWrapper(propertyName))
                return false;

            if (!MatchesWrappedTypeName(target, propertyName))
            {
                wrapperTypeName = propertyName;
                return true;
            }

            unwrappedJson = json.Substring(valueStart, valueEnd - valueStart);
            return true;
        }

        static bool LooksLikeTypeWrapper(string propertyName)
            => propertyName.Length > 0 && char.IsUpper(propertyName[0]);

        static bool MatchesWrappedTypeName(Object target, string wrappedTypeName)
        {
            for (var current = target.GetType(); current != null && current != typeof(object); current = current.BaseType)
            {
                if (current.Name == wrappedTypeName)
                    return true;
            }

            return false;
        }
    }
}
