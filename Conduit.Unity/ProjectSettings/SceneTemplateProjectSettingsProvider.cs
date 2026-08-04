#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Conduit
{
    static class SceneTemplateProjectSettingsProvider
    {
        const string SettingsTypeName = "UnityEditor.SceneTemplate.SceneTemplateProjectSettings";

        // scene templates use a JSON-backed CLR singleton rather than a UnityEngine.Object,
        // so their persisted public records need a small reflection adapter.
        [ConduitProjectSettingsProvider]
        static void Register(ProjectSettingsRegistry registry)
        {
            if (AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType(SettingsTypeName, throwOnError: false))
                    .FirstOrDefault(type => type != null) is not { } settingsType)
                return;

            var get = settingsType.GetMethod("Get", BindingFlags.Public | BindingFlags.Static);
            var save = settingsType.GetMethod(
                "Save",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), settingsType },
                null
            );
            if (get == null || save == null)
                return;

            object GetSettings()
                => get.Invoke(null, null)
                   ?? throw new InvalidOperationException("Unity did not return its scene template project settings.");

            void Save(object settings) => save.Invoke(null, new object?[] { null, settings });
            static object Root(object settings) => settings;

            RegisterField(settingsType.GetField("newSceneOverride"), "new_scene_override", Root);
            RegisterRecord(
                settingsType.GetField("defaultDependencyTypeInfo"),
                "default_dependency_type_info",
                Root,
                "userAdded",
                "type",
                "defaultInstantiationMode"
            );
            RegisterList(
                settingsType.GetField("dependencyTypeInfos"),
                "dependency_type_infos",
                Root,
                "userAdded",
                "type",
                "defaultInstantiationMode"
            );
            RegisterList(
                settingsType.GetField("templatePinStates"),
                "template_pin_states",
                Root,
                "templateId",
                "isEnabled"
            );

            // owners receive the fetched root so each write mutates the exact instance passed to Save.
            void RegisterField(FieldInfo? field, string key, Func<object, object> owner)
            {
                if (field == null)
                    return;

                registry.Add(
                    $"scene_template_settings.{key}",
                    () =>
                    {
                        var settings = GetSettings();
                        return ProjectSettingValueCodec.Format(
                            field.GetValue(owner(settings)),
                            field.FieldType
                        );
                    },
                    value =>
                    {
                        var settings = GetSettings();
                        field.SetValue(owner(settings), ProjectSettingValueCodec.Parse(value, field.FieldType));
                        Save(settings);
                    }
                );
            }

            void RegisterRecord(
                FieldInfo? recordField,
                string key,
                Func<object, object> owner,
                params string[] serializedFieldNames)
            {
                if (recordField?.GetValue(owner(GetSettings())) is not { } record)
                    return;

                var fields = FindFields(record.GetType(), serializedFieldNames);
                if (fields.Length == 0)
                    return;

                object GetRecord(object settings)
                    => recordField.GetValue(owner(settings))
                       ?? throw new InvalidOperationException(
                           $"Scene template setting '{recordField.Name}' is unavailable."
                       );
                registry.Add(
                    $"scene_template_settings.{key}",
                    () => FormatRecord(GetRecord(GetSettings()), fields),
                    value =>
                    {
                        var settings = GetSettings();
                        ApplyRecord(GetRecord(settings), fields, ParseRecord(value));
                        Save(settings);
                    }
                );
                foreach (var field in fields)
                    RegisterField(field, $"{key}.{ProjectSettingKey.Canonicalize(field.Name)}", GetRecord);
            }

            void RegisterList(
                FieldInfo? listField,
                string key,
                Func<object, object> owner,
                params string[] serializedFieldNames)
            {
                if (listField?.GetValue(owner(GetSettings())) is not IList initial)
                    return;

                var itemType = listField.FieldType.IsGenericType
                    ? listField.FieldType.GetGenericArguments()[0]
                    : initial.Count > 0
                        ? initial[0]?.GetType()
                        : null;
                if (itemType == null)
                    return;

                var fields = FindFields(itemType, serializedFieldNames);
                if (fields.Length == 0)
                    return;

                IList GetList(object settings)
                    => (IList)(listField.GetValue(owner(settings))
                               ?? throw new InvalidOperationException(
                                   $"Scene template setting '{listField.Name}' is unavailable."
                               ));

                object GetItem(IList list, int index)
                    => list[index]
                       ?? throw new InvalidOperationException(
                           $"Scene template {key} index {index} is null."
                       );

                registry.Add($"scene_template_settings.{key}.count", () => GetList(GetSettings()).Count);
                for (int index = 0, count = initial.Count; index <= count; ++index)
                {
                    int capturedIndex = index;
                    string settingKey = $"scene_template_settings.{key}.{index}";
                    string ReadRecord()
                    {
                        var list = GetList(GetSettings());
                        return capturedIndex < list.Count
                            ? FormatRecord(GetItem(list, capturedIndex), fields)
                            : "<append>";
                    }

                    void AddRecord(string value)
                    {
                        var settings = GetSettings();
                        var list = GetList(settings);
                        if (capturedIndex != list.Count)
                            throw new InvalidOperationException(
                                $"Append at index {capturedIndex} is invalid; the next index is {list.Count}."
                            );
                        var record = ParseRecord(value);
                        list.Add(CreateRecord(itemType, fields, record));
                        Save(settings);
                    }

                    void SetRecord(string value)
                    {
                        var settings = GetSettings();
                        var list = GetList(settings);
                        if (capturedIndex >= list.Count)
                            throw new InvalidOperationException(
                                $"Scene template {key} index {capturedIndex} does not exist."
                            );
                        ApplyRecord(
                            GetItem(list, capturedIndex),
                            fields,
                            ParseRecord(value)
                        );
                        Save(settings);
                    }

                    void RemoveRecord()
                    {
                        var settings = GetSettings();
                        var list = GetList(settings);
                        if (capturedIndex >= list.Count)
                            throw new InvalidOperationException(
                                $"Scene template {key} index {capturedIndex} does not exist."
                            );
                        list.RemoveAt(capturedIndex);
                        Save(settings);
                    }

                    if (index == count)
                        registry.AddCollectionAppend(settingKey, ReadRecord, AddRecord);
                    else
                        registry.AddCollectionElement(
                            settingKey,
                            ReadRecord,
                            SetRecord,
                            RemoveRecord
                        );

                    if (index == count)
                        continue;

                    foreach (var field in fields)
                        RegisterField(
                            field,
                            $"{key}.{index}.{ProjectSettingKey.Canonicalize(field.Name)}",
                            settings =>
                            {
                                var list = GetList(settings);
                                if (capturedIndex >= list.Count)
                                    throw new InvalidOperationException(
                                        $"Scene template {key} index {capturedIndex} no longer exists."
                                    );
                                return GetItem(list, capturedIndex);
                            }
                        );
                }
            }

            static FieldInfo[] FindFields(Type type, IEnumerable<string> names)
                => names
                    .Select(name => type.GetField(name, BindingFlags.Public | BindingFlags.Instance))
                    .OfType<FieldInfo>()
                    .ToArray();

            static string FormatRecord(object record, IReadOnlyList<FieldInfo> fields)
                => "{" + string.Join(",", fields.Select(field =>
                    ConduitSimpleJson.Quote(ProjectSettingKey.Canonicalize(field.Name))
                    + ":"
                    + FormatJsonValue(field.GetValue(record), field.FieldType)
                )) + "}";

            static string FormatJsonValue(object? value, Type type)
            {
                if (value == null)
                    return "null";
                if (type == typeof(string) || type == typeof(char) || type.IsEnum)
                    return ConduitSimpleJson.Quote(value.ToString());
                return ProjectSettingValueCodec.Format(value, type);
            }

            static ConduitSimpleJson.JsonObjectValue ParseRecord(string json)
                => ConduitSimpleJson.ParseValue(json) as ConduitSimpleJson.JsonObjectValue
                   ?? throw new FormatException("A scene template collection element requires a JSON object.");

            static void ApplyRecord(
                object record,
                IReadOnlyList<FieldInfo> fields,
                ConduitSimpleJson.JsonObjectValue value)
            {
                var available = fields.ToDictionary(
                    field => ProjectSettingKey.Canonicalize(field.Name),
                    StringComparer.Ordinal
                );
                foreach (var pair in value.Properties)
                {
                    string key = ProjectSettingKey.Canonicalize(pair.Key);
                    if (!available.TryGetValue(key, out var field))
                        throw new FormatException($"Unknown scene template field '{pair.Key}'.");
                    field.SetValue(record, ParseJsonValue(pair.Value, field.FieldType));
                }
            }

            static object? ParseJsonValue(ConduitSimpleJson.JsonValue? value, Type type)
            {
                if (value is ConduitSimpleJson.JsonStringValue text)
                    return ProjectSettingValueCodec.Parse(
                        type == typeof(string) || type == typeof(char)
                            ? ConduitSimpleJson.Quote(text.Value)
                            : text.Value,
                        type
                    );
                return ProjectSettingValueCodec.Parse(ConduitSimpleJson.SerializeValue(value), type);
            }

            static object CreateRecord(
                Type itemType,
                IReadOnlyList<FieldInfo> fields,
                ConduitSimpleJson.JsonObjectValue value)
            {
                if (itemType.GetConstructor(Type.EmptyTypes) is { } defaultConstructor)
                    return defaultConstructor.Invoke(null);

                var typeField = fields.FirstOrDefault(field => field.Name == "type")
                                ?? throw new InvalidOperationException(
                                    $"Scene template type '{itemType.Name}' has no supported constructor."
                                );
                var pair = value.Properties.FirstOrDefault(
                    candidate => ProjectSettingKey.Canonicalize(candidate.Key)
                                 == ProjectSettingKey.Canonicalize(typeField.Name)
                );
                if (pair.Key == null || ParseJsonValue(pair.Value, typeof(string)) is not string typeName)
                    throw new FormatException("An appended dependency type requires a 'type' string field.");

                var constructor = itemType.GetConstructor(new[] { typeof(string), typeof(string) })
                                  ?? throw new InvalidOperationException(
                                      $"Scene template type '{itemType.Name}' has no supported constructor."
                                  );
                return constructor.Invoke(new object?[] { typeName, null });
            }
        }
    }
}
