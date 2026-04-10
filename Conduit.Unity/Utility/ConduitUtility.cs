#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Conduit
{
    static class ConduitUtility
    {
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

        const string TargetInvocationDiagnostic = "Exception has been thrown by the target of an invocation.";

        static readonly Regex StackTraceFilePattern = new(@"\s*\[0x[0-9a-fA-F]+\]\s+in\s+(.+?)(?::line\s+|:)(\d+)\s*$", RegexOptions.Compiled);
        static readonly Regex RuntimeLocationPattern = new(@"\s*\(<[^>]+>:\d+\)\s*$", RegexOptions.Compiled);

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
            => !string.IsNullOrWhiteSpace(scene.path)
                ? scene.path
                : string.IsNullOrWhiteSpace(scene.name)
                    ? $"<{unsavedLabel}>"
                    : $"<{unsavedLabel}:{scene.name}>";

        /// <summary>
        /// Rents a pooled list and clears any previously retained contents.
        /// </summary>
        public static PooledObject<List<T>> GetPooledList<T>(out List<T> list)
        {
            var pooled = ListPool<T>.Get(out list);
            list.Clear();
            return pooled;
        }

        /// <summary>
        /// Rents a pooled hash set and clears any previously retained contents.
        /// </summary>
        public static PooledObject<HashSet<T>> GetPooledSet<T>(out HashSet<T> set)
        {
            var pooled = CollectionPool<HashSet<T>, T>.Get(out set);
            set.Clear();
            return pooled;
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
        {
            for (var index = 0; index < normalizedExtensions.Count; index++)
                if (string.Equals(normalizedExtensions[index], extension, StringComparison.OrdinalIgnoreCase))
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
                if (!Uri.IsHexDigit(character))
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
            AppendHierarchySegment(builder, transform);
            return builder.ToString();
        }

#if UNITY_6000_4_OR_NEWER
        /// <summary>
        /// Resolves the stable object identifier for a Unity object using entity IDs on modern Unity.
        /// </summary>
        public static ulong GetObjectId(Object target) => EntityId.ToULong(target.GetEntityId());

        /// <summary>
        /// Formats an object identifier for display in tool output.
        /// </summary>
        public static string FormatObjectId(ulong objectId) => $"eid:{objectId}";
#elif UNITY_6000_2_OR_NEWER
        /// <summary>
        /// Resolves the stable object identifier for a Unity object using entity IDs on modern Unity.
        /// </summary>
        public static ulong GetObjectId(Object target) => (ulong)(int)target.GetEntityId();

        /// <summary>
        /// Formats an object identifier for display in tool output.
        /// </summary>
        public static string FormatObjectId(ulong objectId) => $"eid:{objectId}";
#else
        /// <summary>
        /// Resolves the stable object identifier for a Unity object using instance IDs on older Unity.
        /// </summary>
        public static ulong GetObjectId(Object target) => unchecked((uint)target.GetInstanceID());

        /// <summary>
        /// Formats an object identifier for display in tool output.
        /// </summary>
        public static string FormatObjectId(ulong objectId) => $"id:{objectId.ToString(CultureInfo.InvariantCulture)}";
#endif

        /// <summary>
        /// Formats the identifier of a Unity object for display in tool output.
        /// </summary>
        public static string FormatObjectId(Object target) => FormatObjectId(GetObjectId(target));

        /// <summary>
        /// Appends a transform and its parents to a hierarchy-path builder.
        /// </summary>
        static void AppendHierarchySegment(StringBuilder builder, Transform transform)
        {
            if (transform.parent != null)
            {
                AppendHierarchySegment(builder, transform.parent);
                builder.Append('/');
            }

            builder.Append(transform.name);
        }

        /// <summary>
        /// Removes diagnostics that only repeat the exception message.
        /// </summary>
        public static string? NormalizeDiagnostic(string? diagnostic, string? exceptionMessage)
        {
            if (NormalizeUserFacingText(diagnostic) is not { Length: > 0 } normalizedDiagnostic)
                return null;

            var normalizedExceptionMessage = NormalizeUserFacingText(exceptionMessage);
            if (normalizedDiagnostic == normalizedExceptionMessage)
                return null;

            return normalizedDiagnostic == TargetInvocationDiagnostic
                   && !string.IsNullOrWhiteSpace(normalizedExceptionMessage) ? null : normalizedDiagnostic;
        }

        /// <summary>
        /// Replaces double quotes in user-facing text to keep JSON output compact and readable.
        /// </summary>
        public static string? NormalizeUserFacingText(string? value)
            => value is not { Length: > 0 } ? value : value.Replace('"', '\'');

        /// <summary>
        /// Converts an exception into the compact wire shape used by the tool surface.
        /// </summary>
        public static BridgeExceptionInfo ToExceptionInfo(Exception exception)
        {
            var effectiveException = exception is System.Reflection.TargetInvocationException targetInvocationException && targetInvocationException.InnerException != null
                ? targetInvocationException.InnerException
                : exception;

            return new()
            {
                type = SimplifyTypeName(effectiveException.GetType().FullName ?? effectiveException.GetType().Name),
                message = NormalizeUserFacingText(effectiveException.Message) ?? string.Empty,
                stack_trace = SimplifyStackTrace(effectiveException.StackTrace),
            };
        }

        /// <summary>
        /// Trims namespaces from exception type names.
        /// </summary>
        public static string SimplifyTypeName(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return string.Empty;

            var lastDot = typeName.LastIndexOf('.');
            var lastPlus = typeName.LastIndexOf('+');
            var separatorIndex = Math.Max(lastDot, lastPlus);
            return separatorIndex >= 0 && separatorIndex + 1 < typeName.Length
                ? typeName[(separatorIndex + 1)..]
                : typeName;
        }

        /// <summary>
        /// Removes internal Conduit frames and shortens source locations to file-and-line form.
        /// </summary>
        public static string? SimplifyStackTrace(string? stackTrace)
        {
            if (string.IsNullOrWhiteSpace(stackTrace))
                return null;

            using var pooledBuilder = GetStringBuilder(out var builder);
            try
            {
                using var reader = new StringReader(stackTrace);
                while (reader.ReadLine() is { } line)
                {
                    if (line.Trim() is not { Length: > 0 } trimmed || IsInternalStackTraceFrame(trimmed))
                        continue;

                    if (builder.Length > 0)
                        builder.AppendLine();

                    builder.Append(SimplifyStackTraceLine(trimmed));
                }

                return builder.Length == 0 ? null : builder.ToString();
            }
            catch
            {
                return stackTrace;
            }
        }

        /// <summary>
        /// Ensures extensions always include a leading dot.
        /// </summary>
        static string NormalizeExtension(string extension)
            => extension.StartsWith(".", StringComparison.Ordinal) ? extension : $".{extension}";

        static bool IsInternalStackTraceFrame(string line)
            => line.Contains("Conduit.", StringComparison.Ordinal)
               || line.Contains("ConduitGenerated.", StringComparison.Ordinal);

        static string SimplifyStackTraceLine(string line)
        {
            var match = StackTraceFilePattern.Match(line);
            if (match.Success)
            {
                var filePath = match.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar);
                var fileName = GetSafeFileName(filePath);
                var lineNumber = match.Groups[2].Value;
                return RemoveMethodParameters(line[..match.Index].TrimEnd()) + $" ({fileName}:{lineNumber})";
            }

            var withoutRuntimeLocation = RuntimeLocationPattern.Replace(line, string.Empty).TrimEnd();
            return RemoveMethodParameters(withoutRuntimeLocation);
        }

        static string? GetSafeFileName(string? filePath)
        {
            if (filePath is not { Length: > 0 })
                return filePath;

            var lastSeparator = filePath.LastIndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            return lastSeparator >= 0 && lastSeparator + 1 < filePath.Length
                ? filePath[(lastSeparator + 1)..]
                : filePath;
        }

        static string RemoveMethodParameters(string line)
        {
            var closeParen = line.LastIndexOf(')');
            if (closeParen < 0)
                return line;

            var openParen = line.LastIndexOf('(', closeParen);
            if (openParen < 0)
                return line;

            return line.Remove(openParen, closeParen - openParen + 1);
        }
    }
}
