#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using ShaderPropertyType = UnityEngine.Rendering.ShaderPropertyType;
using Object = UnityEngine.Object;
using PrefabStage = UnityEditor.SceneManagement.PrefabStage;
using PrefabStageUtility = UnityEditor.SceneManagement.PrefabStageUtility;

namespace Conduit
{
    static class ConduitObjectJsonUtility
    {
        const string UndoName = "Conduit From Json Overwrite";
        const string NoSerializedPropertiesChangedMessage = "No serialized properties changed.";
        const string EmptyObjectValue = "\u0001{}";
        const string EmptyArrayValue = "\u0002[]";
        const string StringValuePrefix = "\u0003";
        const string PrimitiveValuePrefix = "\u0004";
        static readonly HashSet<string> supportedDirectMaterialProperties = new(StringComparer.Ordinal)
        {
            "m_Name",
            "m_LightmapFlags",
            "m_EnableInstancingVariants",
            "m_DoubleSidedGI",
            "m_CustomRenderQueue",
            "m_AllowLocking",
        };

        public static string ToJson(string query)
        {
            var matches = ConduitSearchUtility.Resolve(query);
            if (matches.Count == 0)
                return ConduitSearchUtility.FormatNoMatches(query);

            if (matches.Count > 1)
                return ConduitSearchUtility.FormatMatches(matches, includeHint: true);

            var target = matches[0].Target;
            if (TryGetSceneAssetPath(target, out var sceneAssetPath))
            {
                throw new InvalidOperationException(
                    $"Target scene '{sceneAssetPath}' cannot be safely and sensibly converted to JSON. " +
                    "Use the `show` tool to display a compact representation of the scene. " +
                    "(Note that the scene needs to be opened to show its contents.) " +
                    "After that, you can use `to_json` and `from_json_overwrite` targeting specific scene objects."
                );
            }

            return EditorJsonUtility.ToJson(target, true);
        }

        public static string FromJsonOverwrite(string query, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException("JSON payload was empty.");

            var matches = ConduitSearchUtility.Resolve(query);
            if (matches.Count == 0)
                return ConduitSearchUtility.FormatNoMatches(query);

            if (matches.Count > 1)
                return ConduitSearchUtility.FormatMatches(matches, includeHint: true);

            var target = matches[0].Target;
            var normalizedJson = NormalizeOverwriteJson(target, json);
            var beforeJson = EditorJsonUtility.ToJson(target, true);
            var beforeOwningGameObjectName = GetComparableOwningGameObjectName(target, normalizedJson);
            var updatedTarget = ApplyOverwrite(target, normalizedJson);
            var afterJson = EditorJsonUtility.ToJson(updatedTarget, true);
            using var pooledPaths = ConduitUtility.GetPooledList<string>(out var changedPaths);
            CollectChangedSerializedPaths(beforeJson, afterJson, changedPaths);
            AddOwningGameObjectNameChangeIfNeeded(updatedTarget, beforeOwningGameObjectName, changedPaths);
            return FormatChangedPathList(changedPaths);
        }

        static string NormalizeOverwriteJson(Object target, string json)
        {
            if (!TryUnwrapTypedJsonObject(target, json, out var unwrappedJson, out var wrapperTypeName))
                return json;

            if (wrapperTypeName != null)
                throw new InvalidOperationException(
                    $"JSON wrapper '{wrapperTypeName}' does not match target type '{target.GetType().Name}'."
                );

            return target is Material ? unwrappedJson : json;
        }

        static bool TryGetSceneAssetPath(Object target, out string sceneAssetPath)
        {
            sceneAssetPath = AssetDatabase.GetAssetPath(target);
            return target != null
                   && EditorUtility.IsPersistent(target)
                   && sceneAssetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
        }

        static void CollectChangedSerializedPaths(string beforeJson, string afterJson, List<string> changedPaths)
        {
            if (beforeJson == afterJson)
                return;

            if (!TryFlattenJson(beforeJson, out var beforeValues) || !TryFlattenJson(afterJson, out var afterValues))
                throw new InvalidOperationException("Could not diff serialized JSON after overwrite.");

            foreach (var pair in beforeValues)
            {
                if (!afterValues.TryGetValue(pair.Key, out var afterValue) || pair.Value != afterValue)
                    changedPaths.Add(pair.Key);
            }

            foreach (var pair in afterValues)
            {
                if (!beforeValues.ContainsKey(pair.Key))
                    changedPaths.Add(pair.Key);
            }
        }

        static string FormatChangedPathList(List<string> changedPaths)
        {
            if (changedPaths.Count == 0)
                return NoSerializedPropertiesChangedMessage;

            changedPaths.Sort(StringComparer.Ordinal);
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.AppendLine("Applied changes:");
            foreach (var path in changedPaths)
            {
                builder.Append("- ");
                builder.Append(path);
                builder.Append('\n');
            }

            builder.Length--;
            return builder.ToString();
        }

        static string? GetComparableOwningGameObjectName(Object target, string json)
            => target is Component && TryReadRootNameOverwrite(json, out _) ? GetOwningGameObject(target)?.name : null;

        static void AddOwningGameObjectNameChangeIfNeeded(Object updatedTarget, string? beforeOwningGameObjectName, List<string> changedPaths)
        {
            if (beforeOwningGameObjectName == null)
                return;

            var updatedOwningGameObjectName = GetOwningGameObject(updatedTarget)?.name;
            if (updatedOwningGameObjectName == null
                || beforeOwningGameObjectName == updatedOwningGameObjectName
                || changedPaths.Contains("GameObject.m_Name"))
                return;

            changedPaths.Add("GameObject.m_Name");
        }

        static bool TryFlattenJson(string json, out Dictionary<string, string> values)
        {
            values = new(StringComparer.Ordinal);
            var index = 0;
            SkipWhitespace(json, ref index);
            if (!TryFlattenJsonValue(json, ref index, string.Empty, values))
                return false;

            SkipWhitespace(json, ref index);
            return index == json.Length;
        }

        static bool TryFlattenJsonValue(string json, ref int index, string path, Dictionary<string, string> values)
        {
            if (index >= json.Length)
                return false;

            return json[index] switch
            {
                '{' => TryFlattenJsonObject(json, ref index, path, values),
                '[' => TryFlattenJsonArray(json, ref index, path, values),
                '"' => TryReadJsonLeafString(json, ref index, path, values),
                _   => TryReadJsonPrimitive(json, ref index, path, values),
            };
        }

        static bool TryFlattenJsonObject(string json, ref int index, string path, Dictionary<string, string> values)
        {
            if (!TryConsume(json, ref index, '{'))
                return false;

            SkipWhitespace(json, ref index);
            if (TryConsume(json, ref index, '}'))
            {
                if (path.Length > 0)
                    values[path] = EmptyObjectValue;

                return true;
            }

            while (index < json.Length)
            {
                if (!TryReadJsonString(json, ref index, out var propertyName))
                    return false;

                SkipWhitespace(json, ref index);
                if (!TryConsume(json, ref index, ':'))
                    return false;

                SkipWhitespace(json, ref index);
                var childPath = path.Length == 0 ? propertyName : string.Concat(path, ".", propertyName);
                if (!TryFlattenJsonValue(json, ref index, childPath, values))
                    return false;

                SkipWhitespace(json, ref index);
                if (TryConsume(json, ref index, '}'))
                    return true;

                if (!TryConsume(json, ref index, ','))
                    return false;

                SkipWhitespace(json, ref index);
            }

            return false;
        }

        static bool TryFlattenJsonArray(string json, ref int index, string path, Dictionary<string, string> values)
        {
            if (!TryConsume(json, ref index, '['))
                return false;

            SkipWhitespace(json, ref index);
            if (TryConsume(json, ref index, ']'))
            {
                if (path.Length > 0)
                    values[path] = EmptyArrayValue;

                return true;
            }

            var elementIndex = 0;
            while (index < json.Length)
            {
                var childPath = string.Concat(path, "[", elementIndex.ToString(CultureInfo.InvariantCulture), "]");
                if (!TryFlattenJsonValue(json, ref index, childPath, values))
                    return false;

                elementIndex++;
                SkipWhitespace(json, ref index);
                if (TryConsume(json, ref index, ']'))
                    return true;

                if (!TryConsume(json, ref index, ','))
                    return false;

                SkipWhitespace(json, ref index);
            }

            return false;
        }

        static bool TryReadJsonLeafString(string json, ref int index, string path, Dictionary<string, string> values)
        {
            if (!TryReadJsonString(json, ref index, out var value))
                return false;

            if (path.Length > 0)
                values[path] = StringValuePrefix + value;

            return true;
        }

        static bool TryReadJsonPrimitive(string json, ref int index, string path, Dictionary<string, string> values)
        {
            var start = index;
            while (index < json.Length && !char.IsWhiteSpace(json[index]) && json[index] is not ',' and not ']' and not '}')
                index++;

            if (index <= start)
                return false;

            if (path.Length > 0)
                values[path] = string.Concat(PrimitiveValuePrefix, json.AsSpan(start, index - start).ToString());

            return true;
        }

        static bool TryUnwrapTypedJsonObject(Object target, string json, out string unwrappedJson, out string? wrapperTypeName)
        {
            unwrappedJson = string.Empty;
            wrapperTypeName = null;
            var index = 0;
            SkipWhitespace(json, ref index);
            if (!TryConsume(json, ref index, '{'))
                return false;

            SkipWhitespace(json, ref index);
            if (TryConsume(json, ref index, '}'))
                return false;

            if (!TryReadJsonString(json, ref index, out var propertyName))
                return false;

            SkipWhitespace(json, ref index);
            if (!TryConsume(json, ref index, ':'))
                return false;

            SkipWhitespace(json, ref index);
            var valueStart = index;
            if (index >= json.Length || json[index] != '{' || !TrySkipJsonValue(json, ref index))
                return false;

            var valueEnd = index;
            SkipWhitespace(json, ref index);
            if (TryConsume(json, ref index, ',') || !TryConsume(json, ref index, '}'))
                return false;

            SkipWhitespace(json, ref index);
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

        static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index]))
                index++;
        }

        static bool TryConsume(string json, ref int index, char expected)
        {
            if (index >= json.Length || json[index] != expected)
                return false;

            index++;
            return true;
        }

        static bool TryReadJsonString(string json, ref int index, out string value)
        {
            value = string.Empty;
            if (!TryConsume(json, ref index, '"'))
                return false;

            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            while (index < json.Length)
            {
                var character = json[index++];
                if (character == '"')
                {
                    value = builder.ToString();
                    return true;
                }

                if (character != '\\')
                {
                    builder.Append(character);
                    continue;
                }

                if (index >= json.Length)
                    return false;

                var escape = json[index++];
                switch (escape)
                {
                    case '"':
                    case '\\':
                    case '/':
                        builder.Append(escape);
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'u':
                        if (index + 4 > json.Length)
                            return false;

                        if (!ushort.TryParse(json.AsSpan(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codeUnit))
                            return false;

                        builder.Append((char)codeUnit);
                        index += 4;
                        break;
                    default:
                        return false;
                }
            }

            return false;
        }

        static bool TrySkipJsonValue(string json, ref int index)
        {
            if (index >= json.Length)
                return false;

            return json[index] switch
            {
                '{' => TrySkipJsonObject(json, ref index),
                '[' => TrySkipJsonArray(json, ref index),
                '"' => TrySkipJsonString(json, ref index),
                _   => TrySkipJsonPrimitive(json, ref index),
            };
        }

        static bool TrySkipJsonObject(string json, ref int index)
        {
            if (!TryConsume(json, ref index, '{'))
                return false;

            SkipWhitespace(json, ref index);
            if (TryConsume(json, ref index, '}'))
                return true;

            while (index < json.Length)
            {
                if (!TryReadJsonString(json, ref index, out _))
                    return false;

                SkipWhitespace(json, ref index);
                if (!TryConsume(json, ref index, ':'))
                    return false;

                SkipWhitespace(json, ref index);
                if (!TrySkipJsonValue(json, ref index))
                    return false;

                SkipWhitespace(json, ref index);
                if (TryConsume(json, ref index, '}'))
                    return true;

                if (!TryConsume(json, ref index, ','))
                    return false;

                SkipWhitespace(json, ref index);
            }

            return false;
        }

        static bool TrySkipJsonArray(string json, ref int index)
        {
            if (!TryConsume(json, ref index, '['))
                return false;

            SkipWhitespace(json, ref index);
            if (TryConsume(json, ref index, ']'))
                return true;

            while (index < json.Length)
            {
                if (!TrySkipJsonValue(json, ref index))
                    return false;

                SkipWhitespace(json, ref index);
                if (TryConsume(json, ref index, ']'))
                    return true;

                if (!TryConsume(json, ref index, ','))
                    return false;

                SkipWhitespace(json, ref index);
            }

            return false;
        }

        static bool TrySkipJsonString(string json, ref int index)
            => TryReadJsonString(json, ref index, out _);

        static bool TrySkipJsonPrimitive(string json, ref int index)
        {
            var start = index;
            while (index < json.Length && !char.IsWhiteSpace(json[index]) && json[index] is not ',' and not ']' and not '}')
                index++;

            return index > start;
        }

        static Material OverwriteMaterial(Material target, string json)
        {
            ValidateEditablePersistentAsset(target);
            if (!TryFlattenJson(json, out var values))
                throw new InvalidOperationException("Material JSON payload was invalid.");

            var serializedObject = new SerializedObject(target);
            var shaderPropertyTypes = GetMaterialShaderPropertyTypes(target);
            var directEdits = new List<MaterialDirectEdit>();
            var tagEdits = new Dictionary<string, string>(StringComparer.Ordinal);
            var floatEdits = new Dictionary<string, float>(StringComparer.Ordinal);
            var intEdits = new Dictionary<string, int>(StringComparer.Ordinal);
            var colorEdits = new Dictionary<string, Color>(StringComparer.Ordinal);
            var consumedPaths = new HashSet<string>(StringComparer.Ordinal);

            CollectDirectMaterialEdits(serializedObject, values, directEdits, consumedPaths);
            CollectMaterialTagEdits(values, tagEdits, consumedPaths);
            var disabledShaderPasses = CollectMaterialDisabledShaderPasses(serializedObject, target, values, consumedPaths);
            CollectMaterialFloatEdits(shaderPropertyTypes, values, floatEdits, consumedPaths);
            CollectMaterialIntEdits(shaderPropertyTypes, values, intEdits, consumedPaths);
            CollectMaterialColorEdits(serializedObject, target, shaderPropertyTypes, values, colorEdits, consumedPaths);
            EnsureAllMaterialPathsWereConsumed(values, consumedPaths);

            if (directEdits.Count == 0
                && tagEdits.Count == 0
                && disabledShaderPasses == null
                && floatEdits.Count == 0
                && intEdits.Count == 0
                && colorEdits.Count == 0)
                return target;

            Undo.RecordObject(target, UndoName);
            ApplyMaterialDirectEdits(serializedObject, directEdits);
            ApplyMaterialFloatEdits(serializedObject, floatEdits);
            ApplyMaterialIntEdits(serializedObject, intEdits);
            ApplyMaterialColorEdits(serializedObject, colorEdits);
            if (disabledShaderPasses != null)
                ApplyMaterialDisabledShaderPasses(serializedObject, disabledShaderPasses);
            serializedObject.ApplyModifiedPropertiesWithoutUndo();

            foreach (var pair in tagEdits)
                target.SetOverrideTag(pair.Key, pair.Value);

            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssets();
            var assetPath = AssetDatabase.GetAssetPath(target);
            if (string.IsNullOrWhiteSpace(assetPath))
                return target;

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<Material>(assetPath) ?? target;
        }

        static void CollectDirectMaterialEdits(
            SerializedObject serializedObject,
            Dictionary<string, string> values,
            List<MaterialDirectEdit> directEdits,
            HashSet<string> consumedPaths)
        {
            foreach (var pair in values)
            {
                if (!supportedDirectMaterialProperties.Contains(pair.Key))
                    continue;

                if (pair.Key.IndexOf('.') >= 0 || pair.Key.IndexOf('[') >= 0)
                    continue;

                var property = serializedObject.FindProperty(pair.Key)
                               ?? throw new InvalidOperationException($"Material overwrite could not resolve '{pair.Key}'.");
                ValidateDirectMaterialProperty(pair.Key, pair.Value, property.propertyType);
                directEdits.Add(
                    new()
                    {
                        Path = pair.Key,
                        EncodedValue = pair.Value,
                        PropertyType = property.propertyType,
                    }
                );
                consumedPaths.Add(pair.Key);
            }
        }

        static void CollectMaterialTagEdits(
            Dictionary<string, string> values,
            Dictionary<string, string> tagEdits,
            HashSet<string> consumedPaths)
        {
            if (values.TryGetValue("stringTagMap", out var rootValue))
            {
                if (rootValue != EmptyObjectValue)
                    throw new InvalidOperationException("Material overwrite expected 'stringTagMap' to be an object.");

                consumedPaths.Add("stringTagMap");
            }

            const string prefix = "stringTagMap.";
            foreach (var pair in values)
            {
                if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                var tagName = pair.Key[prefix.Length..];
                if (tagName.Length == 0 || tagName.IndexOf('.') >= 0 || tagName.IndexOf('[') >= 0)
                    throw new InvalidOperationException($"Material overwrite does not support path '{pair.Key}'.");

                tagEdits[tagName] = DecodeStringValue(pair.Key, pair.Value);
                consumedPaths.Add(pair.Key);
            }
        }

        static string[]? CollectMaterialDisabledShaderPasses(
            SerializedObject serializedObject,
            Material target,
            Dictionary<string, string> values,
            HashSet<string> consumedPaths)
        {
            var canonicalPassNames = GetMaterialShaderPassNames(serializedObject, target);
            if (values.TryGetValue("disabledShaderPasses", out var rootValue))
            {
                if (rootValue != EmptyArrayValue)
                    throw new InvalidOperationException("Material overwrite expected 'disabledShaderPasses' to be an array of strings.");

                consumedPaths.Add("disabledShaderPasses");
                return Array.Empty<string>();
            }

            var disabledPassesByIndex = new Dictionary<int, string>();
            foreach (var pair in values)
            {
                if (!TryParseIndexedChildPath(pair.Key, "disabledShaderPasses", out var index, out var childPath))
                    continue;

                if (childPath.Length != 0)
                    throw new InvalidOperationException($"Material overwrite does not support path '{pair.Key}'.");

                var passName = DecodeStringValue(pair.Key, pair.Value);
                if (!disabledPassesByIndex.TryAdd(index, passName))
                    throw new InvalidOperationException($"Material overwrite received duplicate array index for '{pair.Key}'.");

                consumedPaths.Add(pair.Key);
            }

            if (disabledPassesByIndex.Count == 0)
                return null;

            var indexes = new int[disabledPassesByIndex.Count];
            disabledPassesByIndex.Keys.CopyTo(indexes, 0);
            Array.Sort(indexes);
            var desiredPasses = new string[indexes.Length];
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            for (var arrayIndex = 0; arrayIndex < indexes.Length; arrayIndex++)
            {
                var requestedPassName = disabledPassesByIndex[indexes[arrayIndex]];
                var normalizedPassName = NormalizeMaterialShaderPassName(requestedPassName);
                if (!canonicalPassNames.TryGetValue(normalizedPassName, out var passName))
                    throw new InvalidOperationException($"Material overwrite does not support shader pass '{requestedPassName}'.");

                if (!seenNames.Add(normalizedPassName))
                    throw new InvalidOperationException($"Material overwrite received duplicate disabled shader pass '{requestedPassName}'.");

                desiredPasses[arrayIndex] = passName;
            }

            return desiredPasses;
        }

        static void CollectMaterialFloatEdits(
            Dictionary<string, ShaderPropertyType> shaderPropertyTypes,
            Dictionary<string, string> values,
            Dictionary<string, float> floatEdits,
            HashSet<string> consumedPaths)
        {
            CollectMaterialNamedScalarEdits(
                values,
                "m_SavedProperties.m_Floats",
                consumedPaths,
                decode: DecodeFloatValue,
                validate: propertyName => ValidateMaterialShaderPropertyType(shaderPropertyTypes, propertyName, "float", ShaderPropertyType.Float, ShaderPropertyType.Range),
                assign: (propertyName, value) => floatEdits[propertyName] = value);
        }

        static void CollectMaterialIntEdits(
            Dictionary<string, ShaderPropertyType> shaderPropertyTypes,
            Dictionary<string, string> values,
            Dictionary<string, int> intEdits,
            HashSet<string> consumedPaths)
        {
            CollectMaterialNamedScalarEdits(
                values,
                "m_SavedProperties.m_Ints",
                consumedPaths,
                decode: DecodeIntValue,
                validate: propertyName => ValidateMaterialShaderPropertyType(shaderPropertyTypes, propertyName, "integer", ShaderPropertyType.Int),
                assign: (propertyName, value) => intEdits[propertyName] = value);
        }

        static void CollectMaterialNamedScalarEdits<TValue>(
            Dictionary<string, string> values,
            string collectionPath,
            HashSet<string> consumedPaths,
            Func<string, string, TValue> decode,
            Action<string> validate,
            Action<string, TValue> assign)
        {
            if (values.TryGetValue(collectionPath, out var rootValue))
            {
                if (rootValue != EmptyArrayValue)
                    throw new InvalidOperationException($"Material overwrite expected '{collectionPath}' to be an array.");

                consumedPaths.Add(collectionPath);
            }

            var entries = new Dictionary<int, MaterialNamedScalarEntry>();
            foreach (var pair in values)
            {
                if (!TryParseIndexedChildPath(pair.Key, collectionPath, out var index, out var childPath))
                    continue;

                consumedPaths.Add(pair.Key);
                if (!entries.TryGetValue(index, out var entry))
                {
                    entry = new();
                    entries[index] = entry;
                }

                switch (childPath)
                {
                    case "first":
                        entry.Name = DecodeStringValue(pair.Key, pair.Value);
                        break;
                    case "second":
                        entry.EncodedValue = pair.Value;
                        break;
                    default:
                        throw new InvalidOperationException($"Material overwrite does not support path '{pair.Key}'.");
                }
            }

            if (entries.Count == 0)
                return;

            var indexes = new int[entries.Count];
            entries.Keys.CopyTo(indexes, 0);
            Array.Sort(indexes);
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var index in indexes)
            {
                var entry = entries[index];
                if (entry.Name is not { Length: > 0 } propertyName || entry.EncodedValue is not { Length: > 0 } encodedValue)
                    throw new InvalidOperationException($"Material overwrite requires both 'first' and 'second' in '{collectionPath}[{index}]'.");

                if (!seenNames.Add(propertyName))
                    throw new InvalidOperationException($"Material overwrite received duplicate key '{propertyName}' in '{collectionPath}'.");

                validate(propertyName);
                assign(propertyName, decode($"{collectionPath}[{index}].second", encodedValue));
            }
        }

        static void CollectMaterialColorEdits(
            SerializedObject serializedObject,
            Material target,
            Dictionary<string, ShaderPropertyType> shaderPropertyTypes,
            Dictionary<string, string> values,
            Dictionary<string, Color> colorEdits,
            HashSet<string> consumedPaths)
        {
            const string collectionPath = "m_SavedProperties.m_Colors";
            if (values.TryGetValue(collectionPath, out var rootValue))
            {
                if (rootValue != EmptyArrayValue)
                    throw new InvalidOperationException($"Material overwrite expected '{collectionPath}' to be an array.");

                consumedPaths.Add(collectionPath);
            }

            var entries = new Dictionary<int, MaterialColorEntry>();
            foreach (var pair in values)
            {
                if (!TryParseIndexedChildPath(pair.Key, collectionPath, out var index, out var childPath))
                    continue;

                consumedPaths.Add(pair.Key);
                if (!entries.TryGetValue(index, out var entry))
                {
                    entry = new();
                    entries[index] = entry;
                }

                switch (childPath)
                {
                    case "first":
                        entry.Name = DecodeStringValue(pair.Key, pair.Value);
                        break;
                    case "second":
                        if (pair.Value != EmptyObjectValue)
                            throw new InvalidOperationException($"Material overwrite expected '{pair.Key}' to be a color object.");
                        break;
                    case "second.r":
                        entry.R = DecodeFloatValue(pair.Key, pair.Value);
                        break;
                    case "second.g":
                        entry.G = DecodeFloatValue(pair.Key, pair.Value);
                        break;
                    case "second.b":
                        entry.B = DecodeFloatValue(pair.Key, pair.Value);
                        break;
                    case "second.a":
                        entry.A = DecodeFloatValue(pair.Key, pair.Value);
                        break;
                    default:
                        throw new InvalidOperationException($"Material overwrite does not support path '{pair.Key}'.");
                }
            }

            if (entries.Count == 0)
                return;

            var indexes = new int[entries.Count];
            entries.Keys.CopyTo(indexes, 0);
            Array.Sort(indexes);
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var index in indexes)
            {
                var entry = entries[index];
                if (entry.Name is not { Length: > 0 } propertyName)
                    throw new InvalidOperationException($"Material overwrite requires a 'first' key in '{collectionPath}[{index}]'.");

                if (!entry.HasAnyChannel)
                    throw new InvalidOperationException($"Material overwrite requires at least one color channel in '{collectionPath}[{index}].second'.");

                if (!seenNames.Add(propertyName))
                    throw new InvalidOperationException($"Material overwrite received duplicate key '{propertyName}' in '{collectionPath}'.");

                ValidateMaterialShaderPropertyType(shaderPropertyTypes, propertyName, "color", ShaderPropertyType.Color);

                var color = TryReadMaterialSavedColor(serializedObject, propertyName, out var serializedColor)
                    ? serializedColor
                    : target.GetColor(propertyName);
                if (entry.R.HasValue)
                    color.r = entry.R.Value;
                if (entry.G.HasValue)
                    color.g = entry.G.Value;
                if (entry.B.HasValue)
                    color.b = entry.B.Value;
                if (entry.A.HasValue)
                    color.a = entry.A.Value;

                colorEdits[propertyName] = color;
            }
        }

        static Dictionary<string, ShaderPropertyType> GetMaterialShaderPropertyTypes(Material target)
        {
            if (target.shader == null)
                throw new InvalidOperationException("Material overwrite could not resolve a shader for the target material.");

            var shader = target.shader;
            var propertyCount = shader.GetPropertyCount();
            var propertyTypes = new Dictionary<string, ShaderPropertyType>(propertyCount, StringComparer.Ordinal);
            for (var index = 0; index < propertyCount; index++)
                propertyTypes[shader.GetPropertyName(index)] = shader.GetPropertyType(index);

            return propertyTypes;
        }

        static void ValidateMaterialShaderPropertyType(
            Dictionary<string, ShaderPropertyType> shaderPropertyTypes,
            string propertyName,
            string label,
            params ShaderPropertyType[] supportedTypes)
        {
            if (!shaderPropertyTypes.TryGetValue(propertyName, out var propertyType))
                throw new InvalidOperationException($"Material overwrite does not support {label} property '{propertyName}'.");

            foreach (var supportedType in supportedTypes)
                if (propertyType == supportedType)
                    return;

            throw new InvalidOperationException($"Material overwrite does not support {label} property '{propertyName}'.");
        }

        static Dictionary<string, string> GetMaterialShaderPassNames(SerializedObject serializedObject, Material target)
        {
            var passNames = new Dictionary<string, string>(StringComparer.Ordinal);
            AddRuntimeMaterialShaderPassNames(target, passNames);
            AddSerializedMaterialShaderPassNames(serializedObject, passNames);
            return passNames;
        }

        static void AddRuntimeMaterialShaderPassNames(Material target, Dictionary<string, string> passNames)
        {
            for (var index = 0; index < target.passCount; index++)
            {
                if (target.GetPassName(index) is not { Length: > 0 } passName)
                    continue;

                passNames.TryAdd(NormalizeMaterialShaderPassName(passName), passName);
            }
        }

        static string NormalizeMaterialShaderPassName(string passName) => passName.ToUpperInvariant();

        static void AddSerializedMaterialShaderPassNames(SerializedObject serializedObject, Dictionary<string, string> passNames)
        {
            var disabledPasses = serializedObject.FindProperty("disabledShaderPasses");
            if (disabledPasses is not { isArray: true })
                return;

            for (var index = 0; index < disabledPasses.arraySize; index++)
            {
                var passName = disabledPasses.GetArrayElementAtIndex(index).stringValue;
                if (string.IsNullOrWhiteSpace(passName))
                    continue;

                passNames[NormalizeMaterialShaderPassName(passName)] = passName;
            }
        }

        static void EnsureAllMaterialPathsWereConsumed(Dictionary<string, string> values, HashSet<string> consumedPaths)
        {
            foreach (var path in values.Keys)
                if (!consumedPaths.Contains(path))
                    throw new InvalidOperationException($"Material overwrite does not support path '{path}'.");
        }

        static void ApplyMaterialDirectEdits(SerializedObject serializedObject, List<MaterialDirectEdit> directEdits)
        {
            foreach (var edit in directEdits)
            {
                var property = serializedObject.FindProperty(edit.Path)
                               ?? throw new InvalidOperationException($"Material overwrite could not resolve '{edit.Path}'.");

                switch (edit.PropertyType)
                {
                    case SerializedPropertyType.String:
                        property.stringValue = DecodeStringValue(edit.Path, edit.EncodedValue);
                        break;
                    case SerializedPropertyType.Boolean:
                        property.boolValue = DecodeBoolValue(edit.Path, edit.EncodedValue);
                        break;
                    case SerializedPropertyType.Integer:
                    case SerializedPropertyType.Enum:
                        property.intValue = DecodeIntValue(edit.Path, edit.EncodedValue);
                        break;
                    default:
                        throw new InvalidOperationException($"Material overwrite does not support direct property '{edit.Path}'.");
                }
            }
        }

        static void ApplyMaterialFloatEdits(SerializedObject serializedObject, Dictionary<string, float> floatEdits)
            => ApplyMaterialNamedScalarEdits(
                serializedObject,
                "m_SavedProperties.m_Floats",
                floatEdits,
                static (secondProperty, value) => secondProperty.floatValue = value
            );

        static void ApplyMaterialIntEdits(SerializedObject serializedObject, Dictionary<string, int> intEdits)
            => ApplyMaterialNamedScalarEdits(
                serializedObject,
                "m_SavedProperties.m_Ints",
                intEdits,
                static (secondProperty, value) => secondProperty.intValue = value
            );

        static void ApplyMaterialColorEdits(SerializedObject serializedObject, Dictionary<string, Color> colorEdits)
        {
            if (colorEdits.Count == 0)
                return;

            var colorArray = FindMaterialSavedPropertyArray(serializedObject, "m_SavedProperties.m_Colors");
            foreach (var pair in colorEdits)
            {
                var entry = FindOrAddMaterialSavedPropertyEntry(colorArray, pair.Key);
                WriteSerializedColor(
                    entry.FindPropertyRelative("second")
                    ?? throw new InvalidOperationException($"Material overwrite could not resolve color value for '{pair.Key}'."),
                    pair.Value
                );
            }
        }

        static void ApplyMaterialNamedScalarEdits<TValue>(
            SerializedObject serializedObject,
            string collectionPath,
            Dictionary<string, TValue> edits,
            Action<SerializedProperty, TValue> assign)
        {
            if (edits.Count == 0)
                return;

            var arrayProperty = FindMaterialSavedPropertyArray(serializedObject, collectionPath);
            foreach (var pair in edits)
            {
                var entry = FindOrAddMaterialSavedPropertyEntry(arrayProperty, pair.Key);
                assign(
                    entry.FindPropertyRelative("second")
                    ?? throw new InvalidOperationException($"Material overwrite could not resolve '{collectionPath}' value for '{pair.Key}'."),
                    pair.Value
                );
            }
        }

        static SerializedProperty FindMaterialSavedPropertyArray(SerializedObject serializedObject, string collectionPath)
            => serializedObject.FindProperty(collectionPath)
               ?? throw new InvalidOperationException($"Material overwrite could not resolve '{collectionPath}'.");

        static SerializedProperty FindOrAddMaterialSavedPropertyEntry(SerializedProperty arrayProperty, string propertyName)
            => FindMaterialSavedPropertyEntry(arrayProperty, propertyName) ?? AddMaterialSavedPropertyEntry(arrayProperty, propertyName);

        static SerializedProperty AddMaterialSavedPropertyEntry(SerializedProperty arrayProperty, string propertyName)
        {
            var index = arrayProperty.arraySize;
            arrayProperty.InsertArrayElementAtIndex(index);
            var entry = arrayProperty.GetArrayElementAtIndex(index);
            (
                entry.FindPropertyRelative("first")
                ?? throw new InvalidOperationException($"Material overwrite could not resolve key for '{propertyName}'.")
            ).stringValue = propertyName;
            return entry;
        }

        static SerializedProperty? FindMaterialSavedPropertyEntry(SerializedProperty arrayProperty, string propertyName)
        {
            for (var index = 0; index < arrayProperty.arraySize; index++)
            {
                var entry = arrayProperty.GetArrayElementAtIndex(index);
                if (entry.FindPropertyRelative("first") is not { stringValue: var currentName })
                    continue;

                if (currentName == propertyName)
                    return entry;
            }

            return null;
        }

        static bool TryReadMaterialSavedColor(SerializedObject serializedObject, string propertyName, out Color color)
        {
            var entry = FindMaterialSavedPropertyEntry(
                FindMaterialSavedPropertyArray(serializedObject, "m_SavedProperties.m_Colors"),
                propertyName
            );
            if (entry == null)
            {
                color = default;
                return false;
            }

            color = ReadSerializedColor(
                entry.FindPropertyRelative("second")
                ?? throw new InvalidOperationException($"Material overwrite could not resolve color value for '{propertyName}'.")
            );
            return true;
        }

        static Color ReadSerializedColor(SerializedProperty colorProperty)
        {
            var color = new Color();
            color.r = (
                colorProperty.FindPropertyRelative("r")
                ?? throw new InvalidOperationException("Material overwrite could not resolve a serialized color channel.")
            ).floatValue;
            color.g = (
                colorProperty.FindPropertyRelative("g")
                ?? throw new InvalidOperationException("Material overwrite could not resolve a serialized color channel.")
            ).floatValue;
            color.b = (
                colorProperty.FindPropertyRelative("b")
                ?? throw new InvalidOperationException("Material overwrite could not resolve a serialized color channel.")
            ).floatValue;
            color.a = (
                colorProperty.FindPropertyRelative("a")
                ?? throw new InvalidOperationException("Material overwrite could not resolve a serialized color channel.")
            ).floatValue;
            return color;
        }

        static void WriteSerializedColor(SerializedProperty colorProperty, Color color)
        {
            (
                colorProperty.FindPropertyRelative("r")
                ?? throw new InvalidOperationException("Material overwrite could not resolve a serialized color channel.")
            ).floatValue = color.r;
            (
                colorProperty.FindPropertyRelative("g")
                ?? throw new InvalidOperationException("Material overwrite could not resolve a serialized color channel.")
            ).floatValue = color.g;
            (
                colorProperty.FindPropertyRelative("b")
                ?? throw new InvalidOperationException("Material overwrite could not resolve a serialized color channel.")
            ).floatValue = color.b;
            (
                colorProperty.FindPropertyRelative("a")
                ?? throw new InvalidOperationException("Material overwrite could not resolve a serialized color channel.")
            ).floatValue = color.a;
        }

        static void ApplyMaterialDisabledShaderPasses(SerializedObject serializedObject, string[] disabledShaderPasses)
        {
            var disabledPasses = serializedObject.FindProperty("disabledShaderPasses")
                               ?? throw new InvalidOperationException("Material overwrite could not resolve 'disabledShaderPasses'.");
            if (!disabledPasses.isArray)
                throw new InvalidOperationException("Material overwrite expected 'disabledShaderPasses' to be an array of strings.");

            disabledPasses.arraySize = disabledShaderPasses.Length;
            for (var index = 0; index < disabledShaderPasses.Length; index++)
                disabledPasses.GetArrayElementAtIndex(index).stringValue = disabledShaderPasses[index];
        }

        static void ValidateEditablePersistentAsset(Object target)
        {
            if (!EditorUtility.IsPersistent(target))
                throw new InvalidOperationException("Resolved object is not persistent and could not be treated as a scene or prefab target.");

            if (!AssetDatabase.IsNativeAsset(target))
                throw new InvalidOperationException(
                    $"Target '{AssetDatabase.GetAssetPath(target)}' is not a native editable asset and cannot be overwritten safely."
                );
        }

        static void ValidateDirectMaterialProperty(string path, string encodedValue, SerializedPropertyType propertyType)
        {
            switch (propertyType)
            {
                case SerializedPropertyType.String:
                    DecodeStringValue(path, encodedValue);
                    return;
                case SerializedPropertyType.Boolean:
                    DecodeBoolValue(path, encodedValue);
                    return;
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.Enum:
                    DecodeIntValue(path, encodedValue);
                    return;
                default:
                    throw new InvalidOperationException($"Material overwrite does not support direct property '{path}'.");
            }
        }

        static bool TryParseIndexedChildPath(string path, string collectionPath, out int index, out string childPath)
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

        static string DecodeStringValue(string path, string encodedValue)
        {
            if (!encodedValue.StartsWith(StringValuePrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Material overwrite expected a string at '{path}'.");

            return encodedValue[StringValuePrefix.Length..];
        }

        static bool DecodeBoolValue(string path, string encodedValue)
        {
            if (!encodedValue.StartsWith(PrimitiveValuePrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Material overwrite expected a boolean at '{path}'.");

            return encodedValue[PrimitiveValuePrefix.Length..] switch
            {
                "true"  => true,
                "false" => false,
                _       => throw new InvalidOperationException($"Material overwrite expected a boolean at '{path}'."),
            };
        }

        static int DecodeIntValue(string path, string encodedValue)
        {
            if (!encodedValue.StartsWith(PrimitiveValuePrefix, StringComparison.Ordinal)
                || !int.TryParse(
                    encodedValue.AsSpan(PrimitiveValuePrefix.Length),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var value))
                throw new InvalidOperationException($"Material overwrite expected an integer at '{path}'.");

            return value;
        }

        static float DecodeFloatValue(string path, string encodedValue)
        {
            if (!encodedValue.StartsWith(PrimitiveValuePrefix, StringComparison.Ordinal)
                || !float.TryParse(
                    encodedValue.AsSpan(PrimitiveValuePrefix.Length),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value))
                throw new InvalidOperationException($"Material overwrite expected a number at '{path}'.");

            return value;
        }

        static Object ApplyOverwrite(Object target, string json)
        {
            if (target == null)
                throw new InvalidOperationException("Resolved object was null.");

            if (target is Material material)
                return OverwriteMaterial(material, json);

            if (TryOverwriteOpenPrefabStageObject(target, json))
                return target;

            if (PrefabUtility.IsPartOfPrefabAsset(target))
                return OverwritePrefabAssetObject(target, json);

            if (target is GameObject or Component)
            {
                OverwriteSceneObject(target, json);
                return target;
            }

            OverwritePersistentObject(target, json);
            return target;
        }

        static bool TryOverwriteOpenPrefabStageObject(Object target, string json)
        {
            var gameObject = GetOwningGameObject(target);
            if (gameObject == null)
                return false;

            var prefabStage = PrefabStageUtility.GetPrefabStage(gameObject);
            if (prefabStage == null)
                return false;

            Undo.RecordObject(target, UndoName);
            EditorJsonUtility.FromJsonOverwrite(json, target);
            ApplyOwningGameObjectNameOverwrite(target, json);
            MarkPrefabOverrideIfNeeded(target);
            EditorSceneManager.MarkSceneDirty(prefabStage.scene);
            SavePrefabStage(prefabStage);
            AssetDatabase.SaveAssets();
            return true;
        }

        static Object OverwritePrefabAssetObject(Object target, string json)
        {
            if (AssetDatabase.GetAssetPath(target) is not { Length: > 0 } assetPath)
                throw new InvalidOperationException("Could not resolve prefab asset path for overwrite target.");

            var currentPrefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (currentPrefabStage != null && string.Equals(currentPrefabStage.assetPath, assetPath, StringComparison.OrdinalIgnoreCase))
            {
                var stageTarget = RemapToPrefabContents(target, currentPrefabStage.prefabContentsRoot);
                Undo.RecordObject(stageTarget, UndoName);
                EditorJsonUtility.FromJsonOverwrite(json, stageTarget);
                ApplyOwningGameObjectNameOverwrite(stageTarget, json);
                MarkPrefabOverrideIfNeeded(stageTarget);
                EditorSceneManager.MarkSceneDirty(currentPrefabStage.scene);
                SavePrefabStage(currentPrefabStage);
                AssetDatabase.SaveAssets();
                return stageTarget;
            }

            var prefabRoot = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                var editableTarget = RemapToPrefabContents(target, prefabRoot);
                Undo.RecordObject(editableTarget, UndoName);
                EditorJsonUtility.FromJsonOverwrite(json, editableTarget);
                ApplyOwningGameObjectNameOverwrite(editableTarget, json);
                MarkPrefabOverrideIfNeeded(editableTarget);
                PrefabUtility.SaveAsPrefabAsset(prefabRoot, assetPath);
                AssetDatabase.SaveAssets();
                return ReloadPrefabAssetTarget(target, assetPath);
            }
            finally
            {
                if (prefabRoot != null)
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        static Object ReloadPrefabAssetTarget(Object originalTarget, string assetPath)
        {
            var prefabRoot = AssetDatabase.LoadMainAssetAtPath(assetPath) as GameObject
                             ?? throw new InvalidOperationException($"Could not reload prefab asset '{assetPath}'.");

            return RemapToPrefabContents(originalTarget, prefabRoot);
        }

        static void OverwriteSceneObject(Object target, string json)
        {
            var gameObject = GetOwningGameObject(target)
                             ?? throw new InvalidOperationException("Could not resolve the owning scene object.");

            var scene = gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded)
                throw new InvalidOperationException("Resolved scene object does not belong to a loaded scene.");

            var beforeJson = EditorJsonUtility.ToJson(target, true);
            var beforeOwningGameObjectName = gameObject.name;
            Undo.RecordObject(target, UndoName);
            EditorJsonUtility.FromJsonOverwrite(json, target);
            ApplyOwningGameObjectNameOverwrite(target, json);
            MarkPrefabOverrideIfNeeded(target);
            if (beforeJson != EditorJsonUtility.ToJson(target, true)
                || beforeOwningGameObjectName != gameObject.name)
                EditorSceneManager.MarkSceneDirty(scene);
        }

        static void OverwritePersistentObject(Object target, string json)
        {
            ValidateEditablePersistentAsset(target);

            Undo.RecordObject(target, UndoName);
            EditorJsonUtility.FromJsonOverwrite(json, target);
            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssets();
        }

        static void ApplyOwningGameObjectNameOverwrite(Object target, string json)
        {
            var gameObject = GetOwningGameObject(target);
            if (gameObject == null || !TryReadRootNameOverwrite(json, out var name))
                return;

            gameObject.name = name;
        }

        static bool TryReadRootNameOverwrite(string json, out string name)
        {
            name = string.Empty;
            if (!TryFlattenJson(json, out var values))
                return false;

            if (values.TryGetValue("m_Name", out var directValue))
            {
                name = DecodeStringValue("m_Name", directValue);
                return true;
            }

            foreach (var pair in values)
            {
                if (!pair.Key.EndsWith(".m_Name", StringComparison.Ordinal))
                    continue;

                var separatorIndex = pair.Key.IndexOf('.');
                if (separatorIndex < 0
                    || separatorIndex != pair.Key.LastIndexOf('.')
                    || separatorIndex != pair.Key.Length - "m_Name".Length - 1)
                    continue;

                name = DecodeStringValue(pair.Key, pair.Value);
                return true;
            }

            return false;
        }

        static Object RemapToPrefabContents(Object originalTarget, GameObject prefabContentsRoot)
        {
            switch (originalTarget)
            {
                case GameObject gameObject:
                    return FindGameObjectByPath(prefabContentsRoot.transform, BuildRelativeTransformPath(gameObject.transform));
                case Component component:
                    var mappedGameObject = FindGameObjectByPath(prefabContentsRoot.transform, BuildRelativeTransformPath(component.transform));
                    var originalComponents = component.gameObject.GetComponents(component.GetType());
                    var componentIndex = Array.IndexOf(originalComponents, component);
                    if (componentIndex < 0)
                        throw new InvalidOperationException($"Could not determine component index for '{component.GetType().FullName}'.");

                    var mappedComponents = mappedGameObject.GetComponents(component.GetType());
                    if (componentIndex >= mappedComponents.Length)
                        throw new InvalidOperationException($"Could not remap component '{component.GetType().FullName}' inside prefab contents.");

                    return mappedComponents[componentIndex];
                default:
                    return originalTarget;
            }
        }

        static GameObject FindGameObjectByPath(Transform prefabRoot, string relativePath)
        {
            if (relativePath is not { Length: > 0 })
                return prefabRoot.gameObject;

            var current = prefabRoot;
            foreach (var segment in relativePath.Split('/'))
            {
                current = current.Find(segment);
                if (current == null)
                    throw new InvalidOperationException($"Could not find prefab object path '{relativePath}'.");
            }

            return current.gameObject;
        }

        static string BuildRelativeTransformPath(Transform transform)
        {
            var segments = new Stack<string>();
            for (var current = transform; current != null && current.parent != null; current = current.parent)
                segments.Push(current.name);

            return string.Join("/", segments);
        }

        static GameObject? GetOwningGameObject(Object target)
            => target switch
            {
                GameObject gameObject => gameObject,
                Component component   => component.gameObject,
                _                     => null,
            };

        static void MarkPrefabOverrideIfNeeded(Object target)
        {
            if (PrefabUtility.IsPartOfPrefabInstance(target))
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
        }

        static void SavePrefabStage(PrefabStage prefabStage)
        {
            if (prefabStage.prefabContentsRoot == null || string.IsNullOrWhiteSpace(prefabStage.assetPath))
                throw new InvalidOperationException("Current prefab stage does not expose a saveable prefab root.");

            PrefabUtility.SaveAsPrefabAsset(prefabStage.prefabContentsRoot, prefabStage.assetPath);
        }

        struct MaterialDirectEdit
        {
            public string Path;
            public string EncodedValue;
            public SerializedPropertyType PropertyType;
        }

        sealed class MaterialNamedScalarEntry
        {
            public string? Name { get; set; }

            public string? EncodedValue { get; set; }
        }

        sealed class MaterialColorEntry
        {
            public string? Name { get; set; }

            public float? R { get; set; }

            public float? G { get; set; }

            public float? B { get; set; }

            public float? A { get; set; }

            public bool HasAnyChannel => R.HasValue || G.HasValue || B.HasValue || A.HasValue;
        }
    }
}
