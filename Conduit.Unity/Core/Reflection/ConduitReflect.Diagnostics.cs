#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Conduit
{
    public static partial class ConduitReflect
    {
        static string FormatResultCandidates<T>(string header, IReadOnlyList<T> candidates) where T : class
            => FormatResultCandidates(header, candidates, candidates.Count);

        static string FormatResultCandidates<T>(
            string header,
            IReadOnlyList<T> candidates,
            int candidateCount) where T : class
        {
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            builder.AppendLine(header);
            builder.AppendLine("Candidates:");
            for (var index = 0; index < candidates.Count && index < MaxCandidates; index++)
                builder.AppendLine("- " + FormatCandidate(candidates[index]));

            AppendTruncation(builder, candidateCount, MaxCandidates, "candidates");
            return builder.ToTrimmedString();
        }

        static string FormatCandidate(object candidate)
        {
            return candidate switch
            {
                Type type =>
                    $"{ReflectionTypeFormatter.DisplayTypeName(type, includeNamespace: true)}, "
                    + type.Assembly.GetName().Name,
                MemberInfo member => FormatMemberCandidate(member),
                _                 => candidate.ToString() ?? string.Empty,
            };

            static string FormatMemberCandidate(MemberInfo member)
            {
                var declaringType = ReflectionTypeFormatter.DisplayTypeName(
                    member.DeclaringType ?? typeof(object),
                    includeNamespace: true
                );
                return $"{declaringType}.{member.Name} | {member.MemberType} | "
                       + member.Module.Assembly.GetName().Name;
            }
        }

        static string TypeCandidates(
            string header,
            IReadOnlyList<Type> candidates,
            int candidateCount)
            => FormatResultCandidates(header, candidates, candidateCount);

        static void AppendTruncation(System.Text.StringBuilder builder, int count, int maxRows, string label)
        {
            if (count <= maxRows)
                return;

            builder.AppendLine();
            builder.Append("Truncated: ");
            builder.Append((count - maxRows).ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(' ');
            builder.Append(label);
            builder.AppendLine(" omitted. Narrow the query.");
        }

        static string FormatQuery(string mode, string? type, string? member)
        {
            var normalizedType = NormalizeQuery(type);
            var normalizedMember = NormalizeQuery(member);
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            builder.Append("reflect query mode='");
            builder.Append(NormalizeQuery(mode));
            builder.Append('\'');
            if (normalizedType.Length > 0)
            {
                builder.Append(", type='");
                builder.Append(normalizedType);
                builder.Append('\'');
            }

            if (normalizedMember.Length > 0)
            {
                builder.Append(", member='");
                builder.Append(normalizedMember);
                builder.Append('\'');
            }

            return builder.ToString();
        }

        static string FormatMode(ReflectMode mode)
            => mode.Category == ReflectCategory.Types
                ? mode.TypeKind switch
                {
                    ReflectTypeKind.Class     => "classes",
                    ReflectTypeKind.Struct    => "structs",
                    ReflectTypeKind.Enum      => "enums",
                    ReflectTypeKind.Interface => "interfaces",
                    ReflectTypeKind.Delegate  => "delegates",
                    _                         => "types",
                }
                : mode.MemberKind switch
                {
                    ReflectMemberKind.Field       => "fields",
                    ReflectMemberKind.Property    => "properties",
                    ReflectMemberKind.Method      => "methods",
                    ReflectMemberKind.Constructor => "constructors",
                    _                             => "members",
                };

        static string InvalidModeDiagnostic(string mode)
            => $"Unsupported reflect mode '{mode}'. Valid modes: {ValidModes}.";

        static string NormalizeQuery(string? value)
            => value?.Trim() ?? string.Empty;

        static bool Contains(string value, string query)
            => value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        static string ShortTypeName(Type type) => ReflectionQueryEngine.GetShortTypeName(type);

    }
}
