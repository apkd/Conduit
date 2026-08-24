#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Conduit
{
    static class ProjectSettingMatcher
    {
        internal static List<ProjectSetting> Match(
            IReadOnlyList<ProjectSetting> settings,
            string query)
        {
            var distinct = settings
                .GroupBy(setting => setting.Key, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(setting => setting.Key, StringComparer.Ordinal)
                .ToList();
            return MatchDistinct(distinct, query);
        }

        static List<ProjectSetting> MatchDistinct(
            IReadOnlyList<ProjectSetting> settings,
            string query)
        {
            string normalized = ProjectSettingKey.Canonicalize(query);
            if (normalized.Length == 0)
                return new(settings);

            var matches = new List<ProjectSetting>();
            foreach (var setting in settings)
                if (setting.Key == normalized)
                    matches.Add(setting);
            if (matches.Count > 0)
                return matches;

            string compactQuery = Compact(normalized);
            foreach (var setting in settings)
                if (setting.CompactKey == compactQuery)
                    matches.Add(setting);
            if (matches.Count > 0)
                return matches;

            var queryTokens = ProjectSettingKey.Tokens(normalized);
            foreach (var setting in settings)
                if (IsOrderedTokenPrefix(queryTokens, setting.Tokens)
                    || setting.CompactKey.IndexOf(compactQuery, StringComparison.Ordinal) >= 0)
                    matches.Add(setting);

            return matches;
        }

        internal static List<ProjectSetting> Match(
            ProjectSettingsRegistry registry,
            string query)
        {
            var normalized = ProjectSettingKey.Canonicalize(query);
            if (normalized.Length > 0 && registry.TryGetDistinct(normalized, out var exact))
                return new() { exact };

            return MatchDistinct(registry.DistinctSettings, normalized);
        }

        internal static List<string> MatchKeys(
            IEnumerable<string> candidates,
            string query)
        {
            string normalized = ProjectSettingKey.Canonicalize(query);
            var keys = candidates
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();
            if (normalized.Length == 0)
                return keys;

            var exact = keys.Where(key => key == normalized).ToList();
            if (exact.Count > 0)
                return exact;

            string compactQuery = Compact(normalized);
            var compactExact = keys.Where(key => Compact(key) == compactQuery).ToList();
            if (compactExact.Count > 0)
                return compactExact;

            var queryTokens = ProjectSettingKey.Tokens(normalized);
            return keys
                .Where(key => IsOrderedTokenPrefix(queryTokens, ProjectSettingKey.Tokens(key))
                              || Compact(key).IndexOf(compactQuery, StringComparison.Ordinal) >= 0)
                .ToList();
        }

        static bool IsOrderedTokenPrefix(
            IReadOnlyList<string> needle,
            IReadOnlyList<string> candidate)
        {
            int candidateIndex = 0;
            foreach (string token in needle)
            {
                while (candidateIndex < candidate.Count
                       && !candidate[candidateIndex].StartsWith(token, StringComparison.Ordinal))
                    candidateIndex++;
                if (candidateIndex == candidate.Count)
                    return false;
                candidateIndex++;
            }

            return true;
        }

        static string Compact(string key)
            => ProjectSettingKey.Compact(key);

    }
}
