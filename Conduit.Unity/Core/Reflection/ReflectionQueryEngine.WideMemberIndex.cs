#nullable enable

using System;
using System.Collections;
using System.Collections.Concurrent;
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
        internal static WideMemberIndex GetWideMemberIndex(
            IReadOnlyList<Type> types,
            ReflectMemberKind kind,
            bool accessorsOnly = false)
        {
            lock (IndexLock)
            {
                while (true)
                {
                    if (TryGetCachedWideMemberIndex(kind, accessorsOnly, out var cached))
                        return cached;

                    var currentTypes = cachedIndex ?? types;
                    var built = new WideMemberIndex(
                        BuildWideMemberIndex(currentTypes, kind, accessorsOnly)
                    );
                    // reflecting metadata can load another assembly and replace the type index reentrantly.
                    if (!ReferenceEquals(currentTypes, cachedIndex))
                        continue;

                    SetCachedWideMemberIndex(kind, accessorsOnly, built);
                    return built;
                }
            }
        }

        static WideMemberIndexSegment[] BuildWideMemberIndex(
            IReadOnlyList<Type> types,
            ReflectMemberKind kind,
            bool accessorsOnly = false)
        {
            var segments = new List<WideMemberIndexSegment>();
            var members = new List<WideMemberIndexEntry>();
            string? currentAssemblyName = null;
            for (var position = 0; position < types.Count; position++)
            {
                var type = types[position];
                var assemblyName = GetTypeSearchInfo(types, position).AssemblyName;
                if (currentAssemblyName != assemblyName)
                {
                    FlushSegment();
                    currentAssemblyName = assemblyName;
                }

                var groupStart = members.Count;
                // the global index retains each member; avoid also retaining one cached array per loaded type.
                switch (kind)
                {
                    case ReflectMemberKind.Field:
                        AddWideMembers(
                            members,
                            type,
                            fieldCache.TryGetValue(type, out var fields)
                                ? fields
                                : type.GetFields(DeclaredMembers)
                        );
                        break;
                    case ReflectMemberKind.Property:
                        AddWideMembers(
                            members,
                            type,
                            propertyCache.TryGetValue(type, out var properties)
                                ? properties
                                : type.GetProperties(DeclaredMembers)
                        );
                        break;
                    case ReflectMemberKind.Method:
                        AddWideMethods(
                            members,
                            type,
                            methodCache.TryGetValue(type, out var methods)
                                ? methods
                                : type.GetMethods(DeclaredMembers),
                            accessorsOnly
                        );
                        break;
                    case ReflectMemberKind.Constructor:
                        AddWideMembers(
                            members,
                            type,
                            constructorCache.TryGetValue(type, out var constructors)
                                ? constructors
                                : type.GetConstructors(DeclaredMembers)
                        );
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(kind));
                }

                members.Sort(
                    groupStart,
                    members.Count - groupStart,
                    WideMemberIndexEntry.WithinTypeComparer
                );
            }

            FlushSegment();
            return segments.ToArray();

            void FlushSegment()
            {
                if (members.Count == 0 || currentAssemblyName == null)
                    return;

                segments.Add(new(currentAssemblyName, members.ToArray()));
                members.Clear();
            }
        }

        static void AddWideMembers(
            List<WideMemberIndexEntry> members,
            Type declaringType,
            MemberInfo[] values)
        {
            foreach (var value in values)
                members.Add(new(declaringType, value));
        }

        static void AddWideMethods(
            List<WideMemberIndexEntry> members,
            Type declaringType,
            MethodInfo[] values,
            bool accessorsOnly)
        {
            foreach (var value in values)
                if (ReflectionMemberFormatter.IsPropertyOrEventAccessor(value) == accessorsOnly)
                    members.Add(new(declaringType, value));
        }

        static bool TryGetCachedWideMemberIndex(
            ReflectMemberKind kind,
            bool accessorsOnly,
            out WideMemberIndex members)
        {
            WideMemberIndex? cached = (kind, accessorsOnly) switch
            {
                (ReflectMemberKind.Field, false) => wideFieldIndex,
                (ReflectMemberKind.Property, false) => widePropertyIndex,
                (ReflectMemberKind.Method, false) => wideMethodIndex,
                (ReflectMemberKind.Method, true) => wideAccessorIndex,
                (ReflectMemberKind.Constructor, false) => wideConstructorIndex,
                _ => null,
            };
            members = cached!;
            return cached != null;
        }

        static void SetCachedWideMemberIndex(
            ReflectMemberKind kind,
            bool accessorsOnly,
            WideMemberIndex members)
        {
            if (accessorsOnly)
            {
                wideAccessorIndex = members;
                return;
            }

            switch (kind)
            {
                case ReflectMemberKind.Field:
                    wideFieldIndex = members;
                    break;
                case ReflectMemberKind.Property:
                    widePropertyIndex = members;
                    break;
                case ReflectMemberKind.Method:
                    wideMethodIndex = members;
                    break;
                case ReflectMemberKind.Constructor:
                    wideConstructorIndex = members;
                    break;
            }
        }

        static void InvalidateWideMemberIndexes()
        {
            wideFieldIndex = null;
            widePropertyIndex = null;
            wideMethodIndex = null;
            wideAccessorIndex = null;
            wideConstructorIndex = null;
        }

        static void ExtendWideMemberIndexes(
            IReadOnlyList<Type> addedTypes,
            IReadOnlyList<Type> expectedTypeIndex)
        {
            if (wideFieldIndex == null
                && widePropertyIndex == null
                && wideMethodIndex == null
                && wideAccessorIndex == null
                && wideConstructorIndex == null)
                return;

            var fields = wideFieldIndex == null
                ? null
                : BuildWideMemberIndex(addedTypes, ReflectMemberKind.Field);
            var properties = widePropertyIndex == null
                ? null
                : BuildWideMemberIndex(addedTypes, ReflectMemberKind.Property);
            var methods = wideMethodIndex == null
                ? null
                : BuildWideMemberIndex(addedTypes, ReflectMemberKind.Method);
            var accessors = wideAccessorIndex == null
                ? null
                : BuildWideMemberIndex(
                    addedTypes,
                    ReflectMemberKind.Method,
                    accessorsOnly: true
                );
            var constructors = wideConstructorIndex == null
                ? null
                : BuildWideMemberIndex(addedTypes, ReflectMemberKind.Constructor);

            // metadata inspection can load another assembly reentrantly; its handler owns the newer indexes.
            if (!ReferenceEquals(expectedTypeIndex, cachedIndex))
            {
                InvalidateWideMemberIndexes();
                return;
            }

            if (fields is { Length: > 0 })
                wideFieldIndex!.AddSegments(fields);
            if (properties is { Length: > 0 })
                widePropertyIndex!.AddSegments(properties);
            if (methods is { Length: > 0 })
                wideMethodIndex!.AddSegments(methods);
            if (accessors is { Length: > 0 })
                wideAccessorIndex!.AddSegments(accessors);
            if (constructors is { Length: > 0 })
                wideConstructorIndex!.AddSegments(constructors);
        }

        internal static int CompareWideMemberEntries(
            WideMemberIndexEntry left,
            WideMemberIndexEntry right)
        {
            var type = CompareTypes(left.DeclaringType, right.DeclaringType);
            if (type != 0)
                return type;

            var name = string.Compare(left.Name, right.Name, StringComparison.Ordinal);
            return name != 0
                ? name
                : WideMemberIndexEntry.GetMetadataToken(left.Member)
                    .CompareTo(WideMemberIndexEntry.GetMetadataToken(right.Member));
        }

        sealed class WideMemberCollector
        {
            readonly List<MemberMatch> matches = new(MaxWideMemberRows);

            internal int TotalCount { get; private set; }

            internal void Add(MemberMatch match)
            {
                TotalCount++;
                AddCandidate(match);
            }

            internal void MergeInto(WideMemberCollector destination)
            {
                destination.TotalCount += TotalCount;
                foreach (var match in matches)
                    destination.AddCandidate(match);
            }

            void AddCandidate(MemberMatch match)
            {
                if (matches.Count < MaxWideMemberRows)
                {
                    matches.Add(match);
                    if (matches.Count == MaxWideMemberRows)
                        BuildHeap();
                    return;
                }

                if (ReflectionMemberFormatter.CompareMemberMatchesWithSignatures(
                        match,
                        matches[0]
                    ) >= 0)
                    return;

                matches[0] = match;
                SiftDown(0);
            }

            internal List<MemberMatch> GetSortedMatches()
            {
                matches.Sort(ReflectionMemberFormatter.CompareMemberMatchesWithSignatures);
                return matches;
            }

            void BuildHeap()
            {
                for (var index = matches.Count / 2 - 1; index >= 0; index--)
                    SiftDown(index);
            }

            void SiftDown(int index)
            {
                while (true)
                {
                    var left = index * 2 + 1;
                    if (left >= matches.Count)
                        return;

                    var right = left + 1;
                    var worse = right < matches.Count
                                && ReflectionMemberFormatter.CompareMemberMatchesWithSignatures(
                                    matches[right],
                                    matches[left]
                                ) > 0
                        ? right
                        : left;
                    if (ReflectionMemberFormatter.CompareMemberMatchesWithSignatures(
                            matches[worse],
                            matches[index]
                        ) <= 0)
                        return;

                    (matches[index], matches[worse]) = (matches[worse], matches[index]);
                    index = worse;
                }
            }
        }

    }
}
