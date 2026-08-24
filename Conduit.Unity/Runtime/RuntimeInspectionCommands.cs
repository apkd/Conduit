#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Pool;
using Object = UnityEngine.Object;

namespace Conduit.Runtime
{
    static class RuntimeInspectionCommands
    {
        const int MaximumDisplayedFieldCount = 100;
        static readonly ConcurrentDictionary<Type, FieldInfo[]> fieldCache = new();

        internal static string Search(string? query)
        {
            var matches = ConduitRuntimeSearch.ResolveManyForDisplay(query);
            if (matches.Count == 0)
                return $"No matches for '{query?.Trim() ?? string.Empty}'.";

            return ConduitRuntimeSearch.FormatMatches(matches, includeHint: false);
        }

        internal static string Show(string? query)
        {
            var target = ConduitRuntimeSearch.ResolveOne(query ?? string.Empty);
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            builder.Append(target.name)
                .Append(" [")
                .Append(target.GetType().FullName)
                .Append("] ")
                .AppendLine(BridgeObjectId.Format(target));

            if (target.AsGameObject() is { } gameObject)
            {
                builder.Append("Path: ")
                    .AppendLine(ConduitRuntimeSearch.GetHierarchyPath(gameObject));
                AppendHierarchy(builder, gameObject.transform);
            }

            if (target is GameObject targetGameObject)
            {
                builder.AppendLine("Components:");
                using var pooledComponents = ListPool<Component>.Get(out var components);
                components.Clear();
                targetGameObject.GetComponents(components);
                foreach (var component in components)
                {
                    builder.Append("- ");
                    if (component == null)
                        builder.AppendLine("Missing Component");
                    else
                        builder.Append(component.GetType().FullName)
                            .Append(" [")
                            .Append(BridgeObjectId.Format(component))
                            .AppendLine("]");
                }
            }

            builder.AppendLine("Properties:")
                .AppendLine(RuntimeObjectJsonUtility.ToJson(target));

            AppendFields(builder, target);
            while (builder.Length > 0 && char.IsWhiteSpace(builder[builder.Length - 1]))
                builder.Length--;

            return builder.ToString();
        }

        internal static string ToJson(string? query)
        {
            var target = ConduitRuntimeSearch.ResolveOne(query ?? string.Empty);
            return RuntimeObjectJsonUtility.ToJson(target);
        }

        internal static string FromJsonOverwrite(string? query, string? json)
        {
            var target = ConduitRuntimeSearch.ResolveOne(query ?? string.Empty);
            return RuntimeObjectJsonUtility.FromJsonOverwrite(target, json ?? string.Empty);
        }

        static void AppendHierarchy(StringBuilder builder, Transform root)
        {
            using var pooledPending = ListPool<(Transform Transform, int Depth)>.Get(out var pending);
            pending.Clear();
            pending.Add((root, 0));
            while (pending.Count > 0)
            {
                var lastIndex = pending.Count - 1;
                var (transform, depth) = pending[lastIndex];
                pending.RemoveAt(lastIndex);
                builder.Append(' ', depth * 2)
                .Append("- ")
                .Append(transform.name)
                .Append(" [")
                .Append(BridgeObjectId.Format(transform.gameObject))
                .AppendLine("]");

                // reverse insertion preserves Unity's sibling order when the stack is consumed.
                for (var index = transform.childCount - 1; index >= 0; --index)
                    pending.Add((transform.GetChild(index), depth + 1));
            }
        }

        static void AppendFields(StringBuilder builder, Object target)
        {
            var fields = fieldCache.GetOrAdd(target.GetType(), static targetType =>
            {
                var fields = new List<FieldInfo>();
                for (var type = targetType;
                     type != null && type != typeof(Object);
                     type = type.BaseType)
                {
                    foreach (var field in type.GetFields(
                                 BindingFlags.Instance
                                 | BindingFlags.Public
                                 | BindingFlags.NonPublic
                                 | BindingFlags.DeclaredOnly
                             ))
                    {
                        fields.Add(field);
                        if (fields.Count == MaximumDisplayedFieldCount)
                            return fields.ToArray();
                    }
                }

                return fields.ToArray();
            });
            if (fields.Length == 0)
                return;

            builder.AppendLine("Fields:");
            foreach (var field in fields)
            {
                object? value;
                try
                {
                    value = field.GetValue(target);
                }
                catch (Exception exception)
                {
                    value = $"<{exception.GetType().Name}>";
                }

                builder.Append("- ")
                    .Append(field.Name)
                    .Append(": ")
                    .AppendLine(BridgeValueFormatter.Format(value) ?? "null");
            }
        }

    }
}
