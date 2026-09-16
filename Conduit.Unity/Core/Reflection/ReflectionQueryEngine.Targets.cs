#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Conduit
{
    static partial class ReflectionQueryEngine
    {
        static readonly ConcurrentDictionary<Type, MemberInfo[]> inspectionMemberCache = new();

        /// <summary>Resolves a type or member selector without executing the target.</summary>
        internal static MemberInfo[] ResolveTarget(string target)
        {
            target = target.Trim();
            if (target.Length == 0)
                return Array.Empty<MemberInfo>();
            var index = LoadIndexForHelpers();
            int separator = target.IndexOf("::", StringComparison.Ordinal);
            if (separator >= 0)
                return FindTargetMembers(target.Substring(0, separator), target.Substring(separator + 2));

            int signature = target.IndexOf('(');
            var types = signature < 0 ? FindTargetTypes(target) : TypeMatch.None();
            // a loaded type can contain dots itself; prefer its exact name before guessing a member separator.
            if (types.IsExact)
                return TypeTargets(types);

            // try the longest type prefix first; dots also occur in nested and explicit-interface names.
            var separators = new List<int>();
            int depth = 0;
            int nameLength = signature < 0 ? target.Length : signature;
            for (int position = 0; position < nameLength; position++)
            {
                char c = target[position];
                if (c is '<' or '(' or '[')
                    depth++;
                else if (c is '>' or ')' or ']')
                    depth--;
                else if (c == '.' && depth == 0)
                    separators.Add(position);
            }
            for (int position = separators.Count - 1; position >= 0; position--)
            {
                separator = separators[position];
                var members = FindTargetMembers(target.Substring(0, separator), target.Substring(separator + 1));
                if (members.Length > 0)
                    return members;
            }

            if (types.CandidateCount > 0)
                return TypeTargets(types);

            var query = new MemberQuery(target);
            var candidates = FindMembers(index, ReflectMemberKind.None, null, query);
            if (candidates.Length < 2)
                return candidates;
            var best = new List<MemberInfo>();
            int bestRank = NameMatching.None;
            foreach (var member in candidates)
                NameMatching.AddBest(best, member, MemberMatchRank(member, query), ref bestRank);
            return SortTargets(best);

            static MemberInfo[] TypeTargets(TypeMatch match)
                => match.Type is { } type ? new MemberInfo[] { type } : match.Candidates.ToArray();

            TypeMatch FindTargetTypes(string query)
                => string.IsNullOrWhiteSpace(query) ? TypeMatch.None()
                    : MatchSingleType(index, query.Trim(), maxCandidates: int.MaxValue);

            MemberInfo[] FindTargetMembers(string type, string member)
            {
                var query = new MemberQuery(member);
                if (query.Text.Length == 0)
                    return Array.Empty<MemberInfo>();
                var matches = new List<MemberInfo>();
                int rank = NameMatching.None;
                var types = FindTargetTypes(type);
                if (types.Type is { } selected)
                    CollectTargetMembers(selected, query, matches, ref rank);
                else
                    foreach (var candidate in types.Candidates)
                        CollectTargetMembers(candidate, query, matches, ref rank);
                return SortTargets(matches);
            }
        }

        static MemberInfo[] SortTargets(List<MemberInfo> matches)
            => matches.Count < 2 ? matches.ToArray() : matches.Distinct()
                .OrderBy(member => ReflectionMemberFormatter.Selector(member), StringComparer.Ordinal).ToArray();

        static void CollectTargetMembers(Type type, MemberQuery query, List<MemberInfo> matches, ref int bestRank)
        {
            var declarations = new Dictionary<MemberSignature, Type>();
            for (var current = type; current != null; current = current.BaseType)
            {
                foreach (var member in GetInspectionMembers(current))
                {
                    if (current != type && member is ConstructorInfo or Type)
                        continue;
                    int rank = MemberMatchRank(member, query);
                    if (rank == NameMatching.None || rank > bestRank)
                        continue;

                    var signature = new MemberSignature(member);
                    if (declarations.TryGetValue(signature, out var owner) && owner != current)
                        continue;
                    // hide inherited declarations, but never discard distinct members declared together.
                    declarations.TryAdd(signature, current);
                    NameMatching.AddBest(matches, member, rank, ref bestRank);
                }
            }
        }

        internal static MemberInfo[] GetInspectionMembers(Type type)
            => inspectionMemberCache.GetOrAdd(type, static value =>
            {
                var members = value.GetMembers(DeclaredMembers);
                var events = new HashSet<string>(members.OfType<EventInfo>().Select(member => member.Name));
                // field-like events have a generated storage field with the exact same name as the event.
                return members.Where(member => member is not FieldInfo field || !events.Contains(field.Name)
                    || !field.IsDefined(typeof(CompilerGeneratedAttribute), false)).ToArray();
            });
    }
}
