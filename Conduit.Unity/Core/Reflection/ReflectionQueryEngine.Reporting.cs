#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit
{
    static partial class ReflectionQueryEngine
    {
        static bool AppendTypeScopedMembers(
            StringBuilder builder,
            Type target,
            ReflectMemberKind kind,
            string memberQuery)
        {
            var appendedAny = false;
            AppendContainer(
                target,
                $"Declared on {ReflectionTypeFormatter.FormatType(target, includeNamespace: true)}"
            );

            for (var baseType = target.BaseType;
                 baseType != null && baseType != typeof(object);
                 baseType = baseType.BaseType)
                AppendContainer(
                    baseType,
                    $"Inherited from {ReflectionTypeFormatter.FormatType(baseType, includeNamespace: true)}"
                );

            var interfaces = target.GetInterfaces();
            Array.Sort(interfaces, CompareTypes);
            foreach (var interfaceType in interfaces)
                AppendContainer(
                    interfaceType,
                    $"Interface {ReflectionTypeFormatter.FormatType(interfaceType, includeNamespace: true)}"
                );

            return appendedAny;

            void AppendContainer(Type containingType, string label)
            {
                var members = GetDisplayMembers(containingType, kind, memberQuery);
                if (members.Count == 0)
                    return;

                if (appendedAny)
                    builder.AppendLine();

                builder.AppendLine($"{label} ({GetAssemblyName(containingType.Assembly)})");
                AppendMembersByKind(builder, members, null);
                appendedAny = true;
            }
        }

        static void AppendHeader(StringBuilder builder, string title, int total, int shown)
            => builder.AppendLine($"{title}: {total} {(total == 1 ? "match" : "matches")}; showing {shown}.");

        static void AppendLoadWarning(StringBuilder builder, string loadWarning)
        {
            if (string.IsNullOrWhiteSpace(loadWarning))
                return;

            builder.AppendLine(loadWarning);
            builder.AppendLine();
        }

        static void AppendType(StringBuilder builder, Type type)
        {
            var kind = ReflectionTypeFormatter.TypeKindLabel(type);
            var baseType = type.BaseType == null || type.IsInterface
                ? "none"
                : ReflectionTypeFormatter.FormatType(type.BaseType, includeNamespace: true);
            var interfaces = type.GetInterfaces();
            Array.Sort(interfaces, CompareTypes);

            builder.Append("- ");
            builder.Append(kind);
            builder.Append(' ');
            builder.Append(ReflectionTypeFormatter.FormatType(type, includeNamespace: true));
            builder.Append(" | Assembly: ");
            builder.Append(GetAssemblyName(type.Assembly));
            builder.Append(" | Base: ");
            builder.Append(baseType);
            builder.Append(" | Interfaces: ");
            builder.Append(
                interfaces.Length == 0
                    ? "none"
                    : ReflectionTypeFormatter.JoinTypes(interfaces, 8)
            );
            builder.Append(" | Members: ");
            AppendMemberCounts(builder, type);
            builder.AppendLine();
        }

        static void AppendTypeHierarchy(StringBuilder builder, Type type)
        {
            builder.Append("Base: ");
            builder.AppendLine(
                type.BaseType == null || type.IsInterface
                    ? "none"
                    : ReflectionTypeFormatter.FormatType(type.BaseType, includeNamespace: true)
            );

            var interfaces = type.GetInterfaces();
            Array.Sort(interfaces, CompareTypes);
            builder.Append("Interfaces: ");
            builder.AppendLine(
                interfaces.Length == 0
                    ? "none"
                    : ReflectionTypeFormatter.JoinTypes(interfaces, 12)
            );
        }

        static void AppendMemberCounts(StringBuilder builder, Type type)
        {
            builder.Append("fields=");
            AppendInvariant(builder, GetFields(type).Length);
            builder.Append(", properties=");
            AppendInvariant(builder, GetProperties(type).Length);
            builder.Append(", methods=");
            AppendInvariant(builder, GetMethods(type).Length);
            builder.Append(", constructors=");
            AppendInvariant(builder, GetConstructors(type).Length);
        }

        static void AppendInvariant(StringBuilder builder, int value)
        {
            Span<char> buffer = stackalloc char[11];
            value.TryFormat(buffer, out var written, provider: CultureInfo.InvariantCulture);
            builder.Append(buffer[..written]);
        }

        static void AppendMembersByKind(StringBuilder builder, List<MemberDisplay> members, int? maxRows)
        {
            members.Sort(ReflectionMemberFormatter.CompareMembers);
            var currentKind = ReflectMemberKind.None;
            var appended = 0;
            foreach (var member in members)
            {
                if (maxRows is { } limit && appended == limit)
                    break;

                if (currentKind != member.Kind)
                {
                    currentKind = member.Kind;
                    builder.Append("  ");
                    builder.Append(ReflectionMemberFormatter.MemberKindHeader(currentKind));
                    builder.AppendLine(":");
                }

                builder.Append("  - ");
                builder.AppendLine(member.Signature);
                appended++;
            }
        }

        static void AppendTruncation(StringBuilder builder, int count, int maxRows, string label)
        {
            if (count <= maxRows)
                return;

            builder.AppendLine();
            builder.Append("Truncated: ");
            AppendInvariant(builder, count - maxRows);
            builder.Append(' ');
            builder.Append(label);
            builder.AppendLine(" omitted. Narrow the query.");
        }

        static List<MemberDisplay> GetDisplayMembers(Type type, ReflectMemberKind kind, string memberQuery)
        {
            var members = new List<MemberDisplay>();
            if (kind is ReflectMemberKind.None or ReflectMemberKind.Field)
                foreach (var field in GetFields(type))
                    if (TryGetMemberMatchRank(field, memberQuery, out var rank))
                        members.Add(new(
                            type,
                            ReflectMemberKind.Field,
                            field.Name,
                            ReflectionMemberFormatter.FormatMemberSignature(field),
                            rank
                        ));

            if (kind is ReflectMemberKind.None or ReflectMemberKind.Property)
                foreach (var property in GetProperties(type))
                    if (TryGetMemberMatchRank(property, memberQuery, out var rank))
                        members.Add(new(
                            type,
                            ReflectMemberKind.Property,
                            property.Name,
                            ReflectionMemberFormatter.FormatMemberSignature(property),
                            rank
                        ));

            if (kind is ReflectMemberKind.None or ReflectMemberKind.Method)
                foreach (var method in GetMethods(type, memberQuery))
                    if (TryGetMemberMatchRank(method, memberQuery, out var rank))
                        members.Add(new(
                            type,
                            ReflectMemberKind.Method,
                            method.Name,
                            ReflectionMemberFormatter.FormatMemberSignature(method),
                            rank
                        ));

            if (kind is ReflectMemberKind.None or ReflectMemberKind.Constructor)
                foreach (var constructor in GetConstructors(type))
                    if (TryGetMemberMatchRank(constructor, memberQuery, out var rank))
                        members.Add(new(
                            type,
                            ReflectMemberKind.Constructor,
                            constructor.Name,
                            ReflectionMemberFormatter.FormatMemberSignature(constructor),
                            rank
                        ));

            return members;
        }

        static void AppendWideMemberMatches(
            WideMemberCollector matches,
            IReadOnlyList<Type> types,
            ReflectMemberKind kind,
            string memberQuery)
        {
            var includeAccessors = IsAccessorQuery(memberQuery);
            if (kind is ReflectMemberKind.None or ReflectMemberKind.Field)
                Append(GetWideMemberIndex(types, ReflectMemberKind.Field), ReflectMemberKind.Field);
            if (kind is ReflectMemberKind.None or ReflectMemberKind.Property)
                Append(GetWideMemberIndex(types, ReflectMemberKind.Property), ReflectMemberKind.Property);
            if (kind is ReflectMemberKind.None or ReflectMemberKind.Method)
            {
                Append(GetWideMemberIndex(types, ReflectMemberKind.Method), ReflectMemberKind.Method);
                if (includeAccessors)
                    Append(
                        GetWideMemberIndex(types, ReflectMemberKind.Method, accessorsOnly: true),
                        ReflectMemberKind.Method
                    );
            }
            if (kind is ReflectMemberKind.None or ReflectMemberKind.Constructor)
                Append(GetWideMemberIndex(types, ReflectMemberKind.Constructor), ReflectMemberKind.Constructor);

            void Append(WideMemberIndex members, ReflectMemberKind memberKind)
            {
                var segments = members.Segments;
                var entryCount = 0;
                foreach (var segment in segments)
                    entryCount += segment.Entries.Length;

                // metadata and search strings are immutable; partition large scans to reduce the editor stall.
                var workerCount = GetParallelScanWorkerCount(entryCount);
                if (workerCount == 1)
                {
                    foreach (var segment in segments)
                        AppendSegment(matches, segment);
                    return;
                }

                var workerResults = new WideMemberCollector[workerCount];
                var nextSegment = -1;
                Parallel.For(0, workerCount, workerIndex =>
                {
                    var localMatches = new WideMemberCollector();
                    int segmentIndex;
                    while ((segmentIndex = Interlocked.Increment(ref nextSegment)) < segments.Length)
                        AppendSegment(localMatches, segments[segmentIndex]);

                    workerResults[workerIndex] = localMatches;
                });

                foreach (var workerResult in workerResults)
                    workerResult.MergeInto(matches);

                void AppendSegment(WideMemberCollector destination, WideMemberIndexSegment segment)
                {
                    foreach (var entry in segment.Entries)
                    {
                        if (TryGetMemberMatchRank(
                                entry.Name,
                                entry.DeclaringType,
                                memberKind == ReflectMemberKind.Constructor,
                                memberQuery,
                                out var rank
                            ))
                            destination.Add(new(
                                entry.DeclaringType,
                                memberKind,
                                entry.Name,
                                entry.Member,
                                rank
                            ));
                    }
                }
            }
        }

        static string TypeCandidates(
            string header,
            IReadOnlyList<Type> candidates,
            int candidateCount
        )
        {
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            builder.AppendLine(header);
            builder.AppendLine("Candidates:");
            for (var index = 0; index < candidates.Count && index < MaxCandidates; index++)
            {
                var type = candidates[index];
                builder.Append("- ")
                    .Append(ReflectionTypeFormatter.FormatType(type, includeNamespace: true))
                    .Append(", ")
                    .AppendLine(GetAssemblyName(type.Assembly));
            }

            AppendTruncation(builder, candidateCount, MaxCandidates, "candidates");
            return builder.ToTrimmedString();
        }
    }
}
