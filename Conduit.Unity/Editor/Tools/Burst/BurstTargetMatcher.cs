#nullable enable

using System;
using System.Collections.Generic;

namespace Conduit
{
    static class BurstTargetMatcher
    {
        const int MaxCandidates = 10;
        const int ClearMatchGap = 25;

        internal static BurstAsmTargetMatch MatchTarget(string? query, BurstTarget[] targets)
        {
            var text = query?.Trim() ?? string.Empty;
            if (text.Length == 0)
                return BurstAsmTargetMatch.None(FirstIndexes(targets), targets.Length);

            var matches = new List<int>();
            int bestRank = NameMatching.None;
            for (int index = 0; index < targets.Length; index++)
                NameMatching.AddBest(matches, index, Rank(targets[index], text), ref bestRank);
            if (matches.Count == 1)
                return BurstAsmTargetMatch.Matched(matches[0]);
            if (matches.Count > 1)
                return BurstAsmTargetMatch.Ambiguous(matches);

            var scored = Score(text, targets);
            if (scored.Count == 0)
                return BurstAsmTargetMatch.None(FirstIndexes(targets), targets.Length);

            scored.Sort((left, right) =>
            {
                var score = right.Score.CompareTo(left.Score);
                return score != 0
                    ? score
                    : string.Compare(targets[left.Index].DisplayName, targets[right.Index].DisplayName, StringComparison.Ordinal);
            });

            if (scored.Count == 1 || scored[0].Score - scored[1].Score >= ClearMatchGap)
                return BurstAsmTargetMatch.Matched(scored[0].Index);

            var candidates = new List<int>();
            var minimumScore = scored[0].Score - ClearMatchGap + 1;
            foreach (var candidate in scored)
            {
                if (candidate.Score < minimumScore)
                    break;

                candidates.Add(candidate.Index);
            }

            return BurstAsmTargetMatch.Ambiguous(candidates);
        }

        static List<ScoredTarget> Score(string query, BurstTarget[] targets)
        {
            var tokens = Tokens(query);
            var matches = new List<ScoredTarget>();
            for (var i = 0; i < targets.Length; i++)
            {
                var score = Score(tokens, query, targets[i]);
                if (score > 0)
                    matches.Add(new(i, score));
            }

            return matches;
        }

        static int Score(string[] tokens, string query, BurstTarget target)
        {
            var score = 0;
            foreach (var token in tokens)
            {
                var part = 0;
                if (Contains(target.DisplayName, token))
                    part = Math.Max(part, 100);
                if (Contains(target.MethodName, token))
                    part = Math.Max(part, 90);
                if (Contains(target.DeclaringTypeName, token) || Contains(target.JobTypeName, token))
                    part = Math.Max(part, 70);
                if (part == 0)
                    return 0;

                score += part;
            }

            if (target.DisplayName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                score += 50;
            if (target.MethodName.Equals(query, StringComparison.OrdinalIgnoreCase))
                score += 50;
            if (target.MethodName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                score += 25;

            return score;
        }

        static string[] Tokens(string text)
        {
            using var pooledTokens = ConduitPool.GetPooledList<string>(out var tokens);
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            foreach (var character in text)
            {
                if (char.IsLetterOrDigit(character) || character == '_')
                {
                    builder.Append(char.ToLowerInvariant(character));
                    continue;
                }

                Flush();
            }

            Flush();
            return tokens.ToArray();

            void Flush()
            {
                if (builder.Length == 0)
                    return;

                tokens.Add(builder.ToString());
                builder.Clear();
            }
        }

        static int Rank(BurstTarget target, string query)
        {
            int rank = NameMatching.Rank(target.DisplayName, query);
            if (rank == NameMatching.Exact)
                return rank;
            rank = Math.Min(rank, NameMatching.Rank(target.MethodName, query));
            if (rank == NameMatching.Exact)
                return rank;
            rank = Math.Min(rank, NameMatching.Rank(target.DeclaringTypeName, query));
            return rank == NameMatching.Exact ? rank : Math.Min(rank, NameMatching.Rank(target.JobTypeName, query));
        }

        static bool Contains(string value, string text) =>
            NameMatching.Rank(value, text) != NameMatching.None;

        static int[] FirstIndexes(BurstTarget[] targets)
        {
            var indexes = new int[Math.Min(targets.Length, MaxCandidates)];
            for (var i = 0; i < indexes.Length; i++)
                indexes[i] = i;

            return indexes;
        }

        internal static BridgeCommandResult Ambiguous(
            string query,
            IReadOnlyList<BurstTarget> targets,
            BurstAsmTargetMatch match) =>
            new()
            {
                outcome = ToolOutcome.AmbiguousTarget,
                diagnostic = Candidates(
                    $"Multiple Burst compile targets match '{query?.Trim() ?? string.Empty}'.",
                    targets,
                    match.CandidateIndexes,
                    match.CandidateCount
                ),
            };

        internal static BridgeCommandResult NoMatch(
            string query,
            IReadOnlyList<BurstTarget> targets,
            BurstAsmTargetMatch match) =>
            BridgeCommandResult.Error(NoMatchDiagnostic(query, targets, match.CandidateIndexes, match.CandidateCount));

        internal static string NoMatchDiagnostic(string query, IReadOnlyList<BurstTarget> targets, int[] indexes)
            => NoMatchDiagnostic(query, targets, indexes, indexes.Length);

        internal static string NoMatchDiagnostic(
            string query,
            IReadOnlyList<BurstTarget> targets,
            int[] indexes,
            int candidateCount)
        {
            var trimmed = query?.Trim() ?? string.Empty;
            return Candidates(
                trimmed.Length == 0 ? string.Empty : $"No Burst compile target matched '{trimmed}'.",
                targets,
                indexes,
                candidateCount
            );
        }

        static string Candidates(
            string header,
            IReadOnlyList<BurstTarget> targets,
            int[] indexes,
            int candidateCount)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            if (!string.IsNullOrWhiteSpace(header))
                builder.AppendLine(header);

            if (indexes.Length == 0)
                return builder.ToTrimmedString();

            builder.AppendLine("Candidates:");
            foreach (var index in indexes)
                builder.AppendLine($"- {targets[index].DisplayName}");

            if (candidateCount > indexes.Length)
            {
                builder.AppendLine();
                builder.AppendLine($"{candidateCount - indexes.Length} additional candidates were omitted.");
                builder.AppendLine("More specific target names return a narrower candidate set.");
            }

            return builder.ToTrimmedString();
        }
    }
}
