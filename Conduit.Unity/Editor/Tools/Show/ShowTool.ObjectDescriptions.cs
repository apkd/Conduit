#nullable enable

using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using static System.StringComparison;
using Object = UnityEngine.Object;

namespace Conduit
{
    static partial class ShowTool
    {
        static string? SummarizeObject(object value, int depth)
        {
            var fields = GetInspectableFields(value.GetType());
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.Append(value.GetType().Name).Append('{');
            var count = 0;
            foreach (var field in fields)
            {
                if (!IsUnitySerializableField(field))
                    continue;

                TryFormatFieldValue(field, value, depth, out var fieldValue);

                if (count++ > 0)
                    builder.Append(", ");

                builder.Append(field.Name).Append('=').Append(fieldValue);
                if (count >= MaxCollectionPreview)
                    break;
            }

            return count == 0 ? null : builder.Append('}').ToString();
        }

        static void AppendObjectIdentifiers(StringBuilder builder, Object target, int indent, bool includeGuid)
        {
            var assetPath = includeGuid && EditorUtility.IsPersistent(target)
                ? AssetDatabase.GetAssetPath(target)
                : string.Empty;
            AppendObjectIdentifiers(builder, target, indent, includeGuid, assetPath);
        }

        static void AppendObjectIdentifiers(
            StringBuilder builder,
            Object target,
            int indent,
            bool includeGuid,
            string assetPath)
        {
            builder.Append(' ', indent);
            builder.Append("ID: ").AppendLine(ConduitObjectId.FormatObjectId(target));

            if (!includeGuid || assetPath.Length == 0)
                return;

            if (AssetDatabase.AssetPathToGUID(assetPath) is not { Length: > 0 } guid)
                return;

            builder.Append(' ', indent);
            builder.Append("GUID: ").AppendLine(guid);
        }

        static string DescribeObject(Object target)
        {
            if (target == null)
                return "null";

            var assetPath = EditorUtility.IsPersistent(target)
                ? AssetDatabase.GetAssetPath(target)
                : string.Empty;
            return DescribeObject(target, assetPath);
        }

        static string DescribeObject(Object target, string assetPath)
        {
            if (target == null)
                return "null";

            return target switch
            {
                GameObject gameObject                        => FormatObjectDescription(
                    nameof(GameObject),
                    ConduitHierarchyPathUtility.BuildHierarchyPath(gameObject.transform),
                    assetPath
                ),
                Component component                          => FormatObjectDescription(
                    component.GetType().Name,
                    ConduitHierarchyPathUtility.BuildHierarchyPath(component.transform),
                    assetPath
                ),
                MonoScript when assetPath is { Length: > 0 } => $"Script({assetPath})",
                _ when assetPath is { Length: > 0 }          => FormatObjectDescription(target.GetType().Name, target.name, assetPath),
                _                                            => $"{target.GetType().Name}(\"{target.name}\")",
            };
        }

        static string FormatObjectDescription(string typeName, string identifier, string assetPath)
            => string.IsNullOrWhiteSpace(assetPath)
                ? $"{typeName}(\"{identifier}\")"
                : IsRedundantAssetIdentifier(identifier, assetPath)
                    ? $"{typeName}({assetPath})"
                    : $"{typeName}(\"{identifier}\", {assetPath})";

        static bool IsRedundantAssetIdentifier(string identifier, string assetPath)
            => string.Equals(identifier, Path.GetFileNameWithoutExtension(assetPath), Ordinal);

        static string FormatSceneName(Scene scene)
            => ConduitHierarchyPathUtility.FormatScenePath(scene, "unsaved scene");

        static string FormatVector(float x, float y)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.Append('(');
            builder.AppendInvariant(x, "0.###");
            builder.Append(", ");
            builder.AppendInvariant(y, "0.###");
            builder.Append(')');
            return builder.ToString();
        }

        static string FormatVector(float x, float y, float z)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.Append('(');
            builder.AppendInvariant(x, "0.###");
            builder.Append(", ");
            builder.AppendInvariant(y, "0.###");
            builder.Append(", ");
            builder.AppendInvariant(z, "0.###");
            builder.Append(')');
            return builder.ToString();
        }

        static string FormatVector(float x, float y, float z, float w)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.Append('(');
            builder.AppendInvariant(x, "0.###");
            builder.Append(", ");
            builder.AppendInvariant(y, "0.###");
            builder.Append(", ");
            builder.AppendInvariant(z, "0.###");
            builder.Append(", ");
            builder.AppendInvariant(w, "0.###");
            builder.Append(')');
            return builder.ToString();
        }

        static string FormatString(string value)
        {
            if (value == null)
                return "null";

            var compact = TrimCompact(value);
            return $"\"{compact}\"";
        }

        static string TrimCompact(string value)
        {
            if (value is not { Length: > 0 })
                return string.Empty;

            var normalized = value
                .Replace("\r\n", "\\n")
                .Replace('\n', ' ')
                .Replace('\r', ' ')
                .Replace('\t', ' ')
                .Trim();

            return normalized.Length <= MaxStringLength
                ? normalized
                : $"{normalized[..MaxStringLength]}...";
        }

        static string FormatUnavailable(Exception exception)
        {
            var type = BridgeExceptionFormatter.SimplifyTypeName(
                exception.GetType().FullName ?? exception.GetType().Name
            );
            var message = TrimCompact(exception.Message);
            return string.IsNullOrWhiteSpace(message)
                ? $"<unavailable: {type}>"
                : $"<unavailable: {type}: {message}>";
        }

    }
}
