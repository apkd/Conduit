#nullable enable

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Conduit
{
    static partial class ConduitSearchUtility
    {
        static bool IsEditorWindowQuery(string query)
            => query.StartsWith("window:", StringComparison.OrdinalIgnoreCase);

        static List<ResolvedObjectMatch> FindOpenEditorWindowMatches(string query, int maxResults)
        {
            var matches = new List<ResolvedObjectMatch>();
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window == null || !MatchesEditorWindowQuery(window, query))
                    continue;

                matches.Add(CreateMatch(window, ResolvedObjectMatchSource.EditorWindowQuery));
                if (matches.Count >= maxResults)
                    break;
            }

            return Deduplicate(matches, maxResults);
        }

        static List<ResolvedObjectMatch> FindEditorWindowTypeMatches(string query, int maxResults)
        {
            var matches = new List<ResolvedObjectMatch>();
            foreach (var windowType in TypeCache.GetTypesDerivedFrom<EditorWindow>())
            {
                if (!windowType.IsClass || windowType.IsAbstract || windowType.ContainsGenericParameters)
                    continue;

                if (!ContainsIgnoreCase(windowType.Name, query))
                    continue;

                matches.Add(
                    new(
                        null,
                        windowType.Name,
                        "EditorWindow type",
                        null,
                        0,
                        ResolvedObjectMatchSource.EditorWindowQuery
                    )
                );

                if (matches.Count >= maxResults)
                    break;
            }

            matches.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
            return matches;
        }

        static List<ResolvedObjectMatch> OpenEditorWindowTypeMatch(ResolvedObjectMatch typeMatch)
        {
            Type? windowType = null;
            foreach (var candidate in TypeCache.GetTypesDerivedFrom<EditorWindow>())
            {
                if (!string.Equals(candidate.Name, typeMatch.Name, StringComparison.Ordinal))
                    continue;

                windowType = candidate;
                break;
            }

            if (windowType == null)
                return new();

            try
            {
                var window = ConduitEditorWindowDocking.CreateDockedTab(windowType);
                return new() { CreateMatch(window, ResolvedObjectMatchSource.EditorWindowQuery) };
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Could not open editor window type '{windowType.Name}': {exception.Message}");
            }
        }

        internal static string GetEditorWindowDisplayName(EditorWindow window)
        {
            var title = GetEditorWindowTitle(window);
            return string.IsNullOrWhiteSpace(title) ? window.GetType().Name : title;
        }

        internal static string GetEditorWindowTitle(EditorWindow window)
            => window.titleContent?.text?.Trim() ?? string.Empty;

        static bool MatchesEditorWindowQuery(EditorWindow window, string query)
            => ContainsIgnoreCase(GetEditorWindowTitle(window), query)
               || ContainsIgnoreCase(window.GetType().Name, query);

        static bool ContainsIgnoreCase(string value, string query)
            => !string.IsNullOrEmpty(value)
               && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        static bool AreEditorWindowMatches(IReadOnlyList<ResolvedObjectMatch> matches)
        {
            foreach (var match in matches)
                if (match.Source != ResolvedObjectMatchSource.EditorWindowQuery)
                    return false;

            return true;
        }

        static string FormatEditorWindowMatches(IReadOnlyList<ResolvedObjectMatch> matches, bool includeHint)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            for (var index = 0; index < Math.Min(matches.Count, MaxResults); index++)
            {
                var match = matches[index];
                builder.Append("- ")
                    .Append(match.Name)
                    .Append(" | ")
                    .Append(match.Location);
                if (match.ObjectId != 0)
                    builder.Append(" | ").Append(ConduitObjectId.FormatObjectId(match.ObjectId));
                builder.AppendLine();
            }

            AppendTruncationNotice(builder, matches.Count);

            if (includeHint && matches.Count > 1)
            {
                builder.AppendLine();
                builder.AppendLine("Multiple editor windows match your query.");
                builder.Append("Rerun with a more specific window title or type name.");
            }

            return builder.ToTrimmedString();
        }

    }
}
