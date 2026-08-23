#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Conduit
{
    static class ConduitUtility
    {
        const int MaximumPooledStringBuilderCapacity = 256 * 1024;
        const int MaximumPooledCollectionCount = 32 * 1024;

        internal struct PooledListHandle<T> : IDisposable
        {
            List<T>? list;

            internal PooledListHandle(List<T> list) => this.list = list;

            public void Dispose()
            {
                if (list == null)
                    return;

                var rented = list;
                if (rented.Capacity > MaximumPooledCollectionCount)
                {
                    rented.Clear();
                    rented.Capacity = 0;
                }

                list = null;
                ListPool<T>.Release(rented);
            }
        }

        internal struct PooledSetHandle<T> : IDisposable
        {
            HashSet<T>? set;

            internal PooledSetHandle(HashSet<T> set) => this.set = set;

            public void Dispose()
            {
                if (set == null)
                    return;

                var rented = set;
                if (rented.EnsureCapacity(0) > MaximumPooledCollectionCount)
                {
                    rented.Clear();
                    rented.TrimExcess();
                }

                set = null;
                CollectionPool<HashSet<T>, T>.Release(rented);
            }
        }

        internal struct PooledDictionaryHandle<TKey, TValue> : IDisposable
        {
            Dictionary<TKey, TValue>? dictionary;

            internal PooledDictionaryHandle(Dictionary<TKey, TValue> dictionary)
                => this.dictionary = dictionary;

            public void Dispose()
            {
                if (dictionary == null)
                    return;

                var rented = dictionary;
                if (rented.EnsureCapacity(0) > MaximumPooledCollectionCount)
                {
                    rented.Clear();
                    rented.TrimExcess();
                }

                dictionary = null;
                DictionaryPool<TKey, TValue>.Release(rented);
            }
        }

        internal struct StringBuilderHandle : IDisposable
        {
            StringBuilder? builder;

            internal StringBuilderHandle(StringBuilder builder)
                => this.builder = builder;

            public void Dispose()
            {
                if (builder == null)
                    return;

                var rentedBuilder = builder;
                builder = null;
                ReturnStringBuilder(rentedBuilder);
            }
        }

        public static string? Stringify(object? value)
        {
            if (value == null)
                return null;

            switch (value)
            {
                case string text:
                    return text;
                case char charValue:
                    return charValue.ToString();
                case bool boolValue:
                    return boolValue ? "true" : "false";
                case Enum enumValue:
                    return enumValue.ToString();
                case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    return Convert.ToString(value, CultureInfo.InvariantCulture);
                case Object unityObject:
                    return UnityEditor.EditorJsonUtility.ToJson(unityObject, true);
            }

            try
            {
                if (JsonUtility.ToJson(value, true) is { Length: > 0 } json and not "{}")
                    return json;
            }
            catch (ArgumentException) { }

            return value.ToString();
        }

        public static string FormatScenePath(Scene scene, string unsavedLabel)
        {
            var path = scene.path;
            if (!string.IsNullOrWhiteSpace(path))
                return path;

            var name = scene.name;
            return string.IsNullOrWhiteSpace(name)
                ? $"<{unsavedLabel}>"
                : $"<{unsavedLabel}:{name}>";
        }

        /// <summary>
        /// Rents a pooled list and clears any previously retained contents.
        /// </summary>
        public static PooledListHandle<T> GetPooledList<T>(out List<T> list)
        {
            _ = ListPool<T>.Get(out list);
            list.Clear();
            return new(list);
        }

        /// <summary>
        /// Rents a pooled hash set and clears any previously retained contents.
        /// </summary>
        public static PooledSetHandle<T> GetPooledSet<T>(out HashSet<T> set)
        {
            _ = CollectionPool<HashSet<T>, T>.Get(out set);
            set.Clear();
            return new(set);
        }

        /// <summary>Rents a pooled dictionary and clears any previously retained entries.</summary>
        public static PooledDictionaryHandle<TKey, TValue> GetPooledDictionary<TKey, TValue>(
            out Dictionary<TKey, TValue> dictionary)
        {
            _ = DictionaryPool<TKey, TValue>.Get(out dictionary);
            dictionary.Clear();
            return new(dictionary);
        }

        /// <summary>
        /// Rents a pooled <see cref="StringBuilder"/> and clears its contents.
        /// </summary>
        public static StringBuilderHandle GetStringBuilder(out StringBuilder builder)
        {
            builder = RentStringBuilder();
            return new(builder);
        }

        static StringBuilder RentStringBuilder()
        {
            var builder = GenericPool<StringBuilder>.Get();
            builder.Clear();
            return builder;
        }

        static void ReturnStringBuilder(StringBuilder? builder)
        {
            if (builder == null)
                return;

            builder.Clear();
            if (builder.Capacity <= MaximumPooledStringBuilderCapacity)
                GenericPool<StringBuilder>.Release(builder);
        }

        public static StringBuilder Trim(this StringBuilder builder)
        {
            builder.TrimEnd();
            var start = 0;
            while (start < builder.Length && char.IsWhiteSpace(builder[start]))
                start++;

            if (start > 0)
                builder.Remove(0, start);

            return builder;
        }

        public static StringBuilder TrimEnd(this StringBuilder builder)
        {
            while (builder.Length > 0 && char.IsWhiteSpace(builder[^1]))
                builder.Length--;

            return builder;
        }

        /// <summary>Appends a value with invariant formatting without creating an intermediate string.</summary>
        public static StringBuilder AppendInvariant(
            this StringBuilder builder,
            int value,
            ReadOnlySpan<char> format = default)
        {
            Span<char> buffer = stackalloc char[64];
            return value.TryFormat(
                buffer,
                out var written,
                format,
                CultureInfo.InvariantCulture
            )
                ? builder.Append(buffer[..written])
                : builder.Append(value.ToString(format.ToString(), CultureInfo.InvariantCulture));
        }

        /// <summary>Appends a value with invariant formatting without creating an intermediate string.</summary>
        public static StringBuilder AppendInvariant(
            this StringBuilder builder,
            float value,
            ReadOnlySpan<char> format = default)
        {
            Span<char> buffer = stackalloc char[64];
            return value.TryFormat(
                buffer,
                out var written,
                format,
                CultureInfo.InvariantCulture
            )
                ? builder.Append(buffer[..written])
                : builder.Append(value.ToString(format.ToString(), CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Finds the first wildcard character in a path or search pattern.
        /// </summary>
        public static int FindWildcardIndex(ReadOnlySpan<char> value)
        {
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character == '*' || character == '?')
                    return index;
            }

            return -1;
        }

        /// <summary>
        /// Determines whether a path or search pattern contains wildcard characters.
        /// </summary>
        public static bool ContainsWildcard(ReadOnlySpan<char> value)
            => FindWildcardIndex(value) >= 0;

        /// <summary>
        /// Normalizes and de-duplicates a set of file extensions.
        /// </summary>
        public static string[] NormalizeExtensions(string[] allowedExtensions)
        {
            using var pooledList = GetPooledList<string>(out var normalized);
            using var pooledSet = GetPooledSet<string>(out var seen);
            foreach (var extension in allowedExtensions)
            {
                var normalizedExtension = NormalizeExtension(extension);
                if (seen.Add(normalizedExtension))
                    normalized.Add(normalizedExtension);
            }

            return normalized.ToArray();
        }

        /// <summary>
        /// Checks extension membership using ordinal-ignore-case comparison.
        /// </summary>
        public static bool ContainsExtension(IReadOnlyList<string> normalizedExtensions, string extension)
            => ContainsExtension(normalizedExtensions, extension.AsSpan());

        /// <summary>
        /// Checks span-based extension membership without materializing a substring.
        /// </summary>
        public static bool ContainsExtension(
            IReadOnlyList<string> normalizedExtensions,
            ReadOnlySpan<char> extension)
        {
            for (var index = 0; index < normalizedExtensions.Count; index++)
                if (normalizedExtensions[index].AsSpan().Equals(
                        extension,
                        StringComparison.OrdinalIgnoreCase
                    ))
                    return true;

            return false;
        }

        /// <summary>
        /// Throws when an asset path does not use one of the supported extensions.
        /// </summary>
        public static void ValidateExtension(string assetPath, IReadOnlyCollection<string> normalizedExtensions)
        {
            if (normalizedExtensions.Count == 0)
                return;

            var extension = System.IO.Path.GetExtension(assetPath);
            foreach (var candidate in normalizedExtensions)
                if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase))
                    return;

            throw new InvalidOperationException(
                $"Asset '{assetPath}' does not match the supported extensions: {string.Join(", ", normalizedExtensions)}."
            );
        }

        /// <summary>
        /// Detects 32-character hexadecimal GUID strings without allocating.
        /// </summary>
        public static bool IsLikelyGuid(ReadOnlySpan<char> value)
        {
            if (value.Length != 32)
                return false;

            foreach (var character in value)
                if (character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f')
                    and not (>= 'A' and <= 'F'))
                    return false;

            return true;
        }

        /// <summary>
        /// Copies a hash set into a deterministically sorted string array.
        /// </summary>
        public static string[] SortStrings(HashSet<string> values, StringComparer comparer)
        {
            if (values.Count == 0)
                return Array.Empty<string>();

            var sorted = new string[values.Count];
            var index = 0;
            foreach (var value in values)
                sorted[index++] = value;

            Array.Sort(sorted, comparer);
            return sorted;
        }

        /// <summary>
        /// Builds a slash-delimited hierarchy path for a transform.
        /// </summary>
        public static string BuildHierarchyPath(Transform transform)
        {
            using var pooledBuilder = GetStringBuilder(out var builder);
            AppendHierarchySegment(builder, transform, 0);
            return builder.ToString();

            static void AppendHierarchySegment(StringBuilder builder, Transform current, int depth)
            {
                // cap call depth while retaining the cheaper recursive path for normal scene hierarchies.
                if (depth == 256)
                {
                    AppendDeepAncestors(builder, current);
                    return;
                }

                var parent = current.parent;
                if (parent != null)
                {
                    AppendHierarchySegment(builder, parent, depth + 1);
                    builder.Append('/');
                }

                builder.Append(current.name);
            }

            static void AppendDeepAncestors(StringBuilder builder, Transform current)
            {
                using var pooledAncestors = GetPooledList<Transform>(out var ancestors);
                for (Transform? ancestor = current; ancestor != null; ancestor = ancestor.parent)
                    ancestors.Add(ancestor);

                for (var index = ancestors.Count - 1; index >= 0; index--)
                {
                    if (index < ancestors.Count - 1)
                        builder.Append('/');

                    builder.Append(ancestors[index].name);
                }
            }
        }

        /// <summary>
        /// Resolves the stable object identifier for a Unity object using entity IDs on modern Unity.
        /// </summary>
        public static ulong GetObjectId(Object target) => BridgeObjectId.Get(target);

        /// <summary>
        /// Formats an object identifier for display in tool output.
        /// </summary>
        public static string FormatObjectId(ulong objectId)
            => BridgeObjectId.Format(objectId);

        /// <summary>Resolves an object identifier produced by <see cref="GetObjectId(Object)"/>.</summary>
        public static Object? ResolveObjectId(ulong objectId)
        {
#if UNITY_6000_4_OR_NEWER
            var entityId = EntityId.FromULong(objectId);
            return entityId.IsValid()
                ? UnityEditor.EditorUtility.EntityIdToObject(entityId)
                : null;
#elif UNITY_6000_3_OR_NEWER
            var entityId = (EntityId)unchecked((int)objectId);
            return entityId.IsValid()
                ? UnityEditor.EditorUtility.EntityIdToObject(entityId)
                : null;
#elif UNITY_6000_2_OR_NEWER
            var entityId = (EntityId)unchecked((int)objectId);
            return entityId.IsValid()
                ? UnityEditor.EditorUtility.InstanceIDToObject(unchecked((int)objectId))
                : null;
#else
            return UnityEditor.EditorUtility.InstanceIDToObject(unchecked((int)objectId));
#endif
        }

        /// <summary>
        /// Formats the identifier of a Unity object for display in tool output.
        /// </summary>
        public static string FormatObjectId(Object target) => FormatObjectId(GetObjectId(target));

        /// <summary>
        /// Removes diagnostics that only repeat the exception message.
        /// </summary>
        public static string? NormalizeDiagnostic(string? diagnostic, string? exceptionMessage)
            => BridgeExceptionFormatter.NormalizeDiagnostic(diagnostic, exceptionMessage);

        /// <summary>
        /// Replaces double quotes in user-facing text to keep JSON output compact and readable.
        /// </summary>
        public static string? NormalizeUserFacingText(string? value)
            => BridgeExceptionFormatter.NormalizeUserFacingText(value);

        /// <summary>
        /// Converts an exception into the compact wire shape used by the tool surface.
        /// </summary>
        public static BridgeExceptionInfo ToExceptionInfo(Exception exception)
            => BridgeExceptionFormatter.ToInfo(exception);

        /// <summary>
        /// Trims namespaces from exception type names.
        /// </summary>
        public static string SimplifyTypeName(string typeName)
            => BridgeExceptionFormatter.SimplifyTypeName(typeName);

        /// <summary>Produces compact logical frames from runtime and compiler-generated stack traces.</summary>
        public static string? SimplifyStackTrace(string? stackTrace)
            => BridgeExceptionFormatter.SimplifyStackTrace(stackTrace);

        /// <summary>
        /// Ensures extensions always include a leading dot.
        /// </summary>
        static string NormalizeExtension(string extension)
            => extension.StartsWith(".", StringComparison.Ordinal) ? extension : $".{extension}";
    }
}
