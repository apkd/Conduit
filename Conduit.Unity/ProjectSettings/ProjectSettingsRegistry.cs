#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{
    /// <summary>Marks a static <c>void(ProjectSettingsRegistry)</c> provider method.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ConduitProjectSettingsProviderAttribute : Attribute { }

    /// <summary>Builds the catalog used by the <c>project_settings</c> tool.</summary>
    public sealed class ProjectSettingsRegistry
    {
        readonly List<ProjectSetting> settings = new();

        internal IReadOnlyList<ProjectSetting> Settings => settings;

        /// <summary>Adds a scalar, enum, Unity object reference, or JSON-serializable project setting.</summary>
        public void Add<T>(string key, Func<T> read, Action<T>? write = null)
        {
            if (read == null)
                throw new ArgumentNullException(nameof(read));

            Add(
                key,
                () => ProjectSettingValueCodec.Format(read(), typeof(T)),
                write == null
                    ? null
                    : value => write((T)ProjectSettingValueCodec.Parse(value, typeof(T))!)
            );
        }

        internal void Add(string key, Func<string> read, Action<string>? write = null)
            => Register(key, read, write, null, null);

        internal void AddCollectionAppend(
            string key,
            Func<string> read,
            Action<string> add)
            => Register(key, read, null, add, null);

        internal void AddCollectionElement(
            string key,
            Func<string> read,
            Action<string> set,
            Action remove)
            => Register(key, read, set, null, remove);

        void Register(
            string key,
            Func<string> read,
            Action<string>? set,
            Action<string>? add,
            Action? remove)
        {
            string canonicalKey = ProjectSettingKey.Canonicalize(key);
            if (canonicalKey.Length == 0)
                throw new ArgumentException(
                    "A project setting key must contain at least one letter or digit.",
                    nameof(key)
                );

            settings.Add(new(canonicalKey, read, set, add, remove));
        }

        internal static ProjectSettingsRegistry Build()
        {
            var registry = new ProjectSettingsRegistry();

            // built-in and package providers share one discovery and failure-isolation path.
            foreach (var method in TypeCache.GetMethodsWithAttribute<ConduitProjectSettingsProviderAttribute>())
            {
                string provider = $"{method.DeclaringType?.FullName}.{method.Name}";
                int initialCount = registry.settings.Count;
                try
                {
                    var parameters = method.GetParameters();
                    if (!method.IsStatic
                        || method.ReturnType != typeof(void)
                        || parameters.Length != 1
                        || parameters[0].ParameterType != typeof(ProjectSettingsRegistry))
                    {
                        ConduitDiagnostics.Warn(
                            $"Ignoring invalid project settings provider '{provider}'. " +
                            "Providers must be static void methods with one ProjectSettingsRegistry parameter."
                        );
                        continue;
                    }

                    method.Invoke(null, new object[] { registry });
                }
                catch (Exception exception)
                {
                    // provider registration is atomic; a failure must not leave a partial catalog behind.
                    registry.settings.RemoveRange(initialCount, registry.settings.Count - initialCount);
                    ConduitDiagnostics.Warn(
                        $"Project settings provider '{provider}' failed: " +
                        (exception is TargetInvocationException { InnerException: { } inner }
                            ? inner.Message
                            : exception.Message)
                    );
                }
            }

            return registry;
        }
    }

    sealed class ProjectSetting
    {
        internal ProjectSetting(
            string key,
            Func<string> read,
            Action<string>? set,
            Action<string>? add,
            Action? remove)
        {
            Key = key;
            Read = read;
            SetValue = set;
            AddValue = add;
            RemoveElement = remove;
        }

        internal string Key { get; }
        internal Func<string> Read { get; }
        internal Action<string>? SetValue { get; }
        internal Action<string>? AddValue { get; }
        internal Action? RemoveElement { get; }
        internal ProjectSettingOperations Operations
            => (SetValue == null ? ProjectSettingOperations.None : ProjectSettingOperations.Set)
               | (AddValue == null ? ProjectSettingOperations.None : ProjectSettingOperations.AddElement)
               | (RemoveElement == null ? ProjectSettingOperations.None : ProjectSettingOperations.RemoveElement);
    }

    [Flags]
    enum ProjectSettingOperations
    {
        None = 0,
        Set = 1 << 0,
        AddElement = 1 << 1,
        RemoveElement = 1 << 2,
    }

    static class ProjectSettingKey
    {
        internal static string Canonicalize(string value)
        {
            var builder = new StringBuilder(value.Length + 8);
            var previous = CharacterKind.Separator;
            string trimmed = value.Trim();

            for (int index = 0, count = trimmed.Length; index < count; ++index)
            {
                char character = trimmed[index];
                if (character is '.' or '/')
                {
                    AppendHierarchySeparator(builder);
                    previous = CharacterKind.Separator;
                    continue;
                }
                if (character == '_' || character == '-' || char.IsWhiteSpace(character))
                {
                    AppendWordSeparator(builder);
                    previous = CharacterKind.Separator;
                    continue;
                }

                var kind = GetKind(character);
                var next = index + 1 < count
                    ? GetKind(trimmed[index + 1])
                    : CharacterKind.Separator;
                if (kind == CharacterKind.Separator)
                {
                    AppendWordSeparator(builder);
                    previous = kind;
                    continue;
                }

                if (kind == CharacterKind.Upper
                    && (previous is CharacterKind.Lower or CharacterKind.Digit
                        || previous == CharacterKind.Upper && next == CharacterKind.Lower))
                    AppendWordSeparator(builder);

                builder.Append(char.ToLowerInvariant(character));
                previous = kind;
            }

            return builder.ToString().Trim('.', '_');

            static CharacterKind GetKind(char value)
                => char.IsUpper(value)
                    ? CharacterKind.Upper
                    : char.IsLower(value)
                        ? CharacterKind.Lower
                        : char.IsDigit(value)
                            ? CharacterKind.Digit
                            : CharacterKind.Separator;

            static void AppendHierarchySeparator(StringBuilder builder)
            {
                while (builder.Length > 0 && builder[^1] == '_')
                    builder.Length--;
                if (builder.Length > 0 && builder[^1] != '.')
                    builder.Append('.');
            }

            static void AppendWordSeparator(StringBuilder builder)
            {
                if (builder.Length > 0 && builder[^1] is not ('.' or '_'))
                    builder.Append('_');
            }
        }

        internal static string[] Tokens(string key)
            => key.Split(new[] { '.', '_' }, StringSplitOptions.RemoveEmptyEntries);

        enum CharacterKind
        {
            Separator,
            Lower,
            Upper,
            Digit,
        }
    }

    static class ProjectSettingValueCodec
    {
        internal static string Format(object? value, Type declaredType)
        {
            if (value == null)
                return "null";

            var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
            if (type == typeof(string))
                return FormatString((string)value);
            if (type == typeof(char))
                return FormatString(value.ToString());
            if (type == typeof(bool))
                return (bool)value ? "true" : "false";
            if (type.IsEnum)
                return value.ToString();
            if (value is Object unityObject)
                return FormatObjectReference(unityObject);
            if (value is IFormattable formattable
                && (type.IsPrimitive || type == typeof(decimal) || type == typeof(Guid)))
                return formattable.ToString(null, CultureInfo.InvariantCulture);

            return JsonUtility.ToJson(value);
        }

        internal static object? Parse(string value, Type declaredType)
        {
            var nullableType = Nullable.GetUnderlyingType(declaredType);
            var type = nullableType ?? declaredType;
            if (value == "null")
            {
                if (!type.IsValueType || nullableType != null)
                    return null;

                throw new FormatException($"'{declaredType.Name}' cannot be cleared with null.");
            }

            if (type == typeof(string))
                return ParseString(value);
            if (type == typeof(char))
            {
                string parsed = ParseString(value);
                if (parsed.Length != 1)
                    throw new FormatException("A character setting requires exactly one character.");
                return parsed[0];
            }
            if (type == typeof(bool))
                return ParseBoolean(value);
            if (type.IsEnum)
                return Enum.Parse(type, value, ignoreCase: true);
            if (typeof(Object).IsAssignableFrom(type))
                return ParseObjectReference(value, type);

            try
            {
                if (type == typeof(Guid))
                    return Guid.Parse(value);
                if (type.IsPrimitive || type == typeof(decimal))
                    return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);

                return JsonUtility.FromJson(value, type);
            }
            catch (Exception exception) when (exception is FormatException
                                              or OverflowException
                                              or ArgumentException
                                              or InvalidCastException)
            {
                throw new FormatException($"Could not parse '{value}' as {declaredType.Name}.", exception);
            }
        }

        // simple strings stay shell-friendly; JSON quoting preserves empty, whitespace, and literal "null" values.
        static string FormatString(string? value)
        {
            if (value == null)
                return "null";
            if (value.Length > 0
                && value != "null"
                && value == value.Trim()
                && value[0] != '"'
                && value[^1] != '"'
                && value.IndexOfAny(new[] { '\r', '\n', '\t' }) < 0)
                return value;

            return ConduitSimpleJson.Quote(value);
        }

        static string ParseString(string value)
        {
            if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
                return value;

            return ConduitSimpleJson.ParseValue(value) is ConduitSimpleJson.JsonStringValue text
                ? text.Value
                : throw new FormatException($"Could not parse '{value}' as String.");
        }

        static bool ParseBoolean(string value)
            => value.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "on"  => true,
                "false" or "0" or "no" or "off" => false,
                _ => throw new FormatException($"Could not parse '{value}' as Boolean."),
            };

        internal static string FormatObjectReference(Object value)
        {
            string path = AssetDatabase.GetAssetPath(value);
            if (path.Length > 0
                && AssetDatabase.LoadMainAssetAtPath(path) == value)
                return path;

            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out var guid, out var fileId))
                return $"{{\"guid\":\"{guid}\",\"file_id\":{fileId.ToString(CultureInfo.InvariantCulture)}}}";

            return $"{{\"entity_id\":\"{BridgeObjectId.Get(value).ToString(CultureInfo.InvariantCulture)}\"}}";
        }

        internal static Object? ParseObjectReference(string value, Type expectedType)
        {
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = (string)Parse(value, typeof(string))!;

            if (value.StartsWith("{", StringComparison.Ordinal))
            {
                if (ConduitSimpleJson.ParseValue(value) is not ConduitSimpleJson.JsonObjectValue reference)
                    throw new FormatException("An object reference requires a JSON object.");

                if (reference.Properties.TryGetValue("guid", out var guidValue))
                {
                    if (guidValue is not ConduitSimpleJson.JsonStringValue { Value: { Length: > 0 } } guid)
                        throw new FormatException("An object reference guid must be a non-empty string.");

                    string referencePath = AssetDatabase.GUIDToAssetPath(guid.Value);
                    if (!reference.Properties.TryGetValue("file_id", out var fileIdValue))
                    {
                        if (AssetDatabase.LoadAssetAtPath(referencePath, expectedType) is { } asset)
                            return asset;
                    }
                    else
                    {
                        if (fileIdValue is not ConduitSimpleJson.JsonNumberValue fileId)
                            throw new FormatException("An object reference file_id must be an integer.");

                        long expectedFileId = long.Parse(
                            fileId.Value,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture
                        );
                        foreach (var candidate in AssetDatabase.LoadAllAssetsAtPath(referencePath))
                            if (expectedType.IsInstanceOfType(candidate)
                                && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                                    candidate,
                                    out _,
                                    out var candidateFileId
                                )
                                && candidateFileId == expectedFileId)
                                return candidate;
                    }

                    throw new FormatException(
                        $"Object reference '{value}' does not resolve to an asset assignable to {expectedType.Name}."
                    );
                }

                if (reference.Properties.TryGetValue("entity_id", out var entityIdValue))
                {
                    string? entityId = entityIdValue switch
                    {
                        ConduitSimpleJson.JsonStringValue text => text.Value,
                        ConduitSimpleJson.JsonNumberValue number => number.Value,
                        _ => null,
                    };
                    if (entityId != null
                        && BridgeObjectId.TryParse(entityId, out var objectId)
                        && ConduitUtility.ResolveObjectId(objectId) is { } target
                        && expectedType.IsInstanceOfType(target))
                        return target;

                    throw new FormatException(
                        $"Object reference '{value}' does not resolve to a live {expectedType.Name}."
                    );
                }

                throw new FormatException("An object reference JSON object requires guid or entity_id.");
            }

            var result = AssetDatabase.LoadAssetAtPath(value, expectedType);
            if (result != null)
                return result;

            string path = AssetDatabase.GUIDToAssetPath(value);
            result = path.Length == 0 ? null : AssetDatabase.LoadAssetAtPath(path, expectedType);
            if (result == null)
                throw new FormatException(
                    $"'{value}' does not resolve to a project asset assignable to {expectedType.Name}."
                );
            return result;
        }
    }
}
