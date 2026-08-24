#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Conduit
{
    static class ProjectSettingsFormatter
    {
        const int MaximumDisplayedMatches = 32;

        internal static string ListGroups(IReadOnlyList<string> groups)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder
                .Append("Found ")
                .Append(groups.Count)
                .Append(groups.Count == 1
                    ? " project settings group:"
                    : " project settings groups:");
            foreach (string group in groups)
                builder.Append('\n').Append(group);
            return builder.ToString();
        }

        internal static string ReadGroup(string group, IReadOnlyList<ProjectSetting> settings)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder
                .Append("Found ")
                .Append(settings.Count)
                .Append(settings.Count == 1
                    ? " project setting in '"
                    : " project settings in '")
                .Append(group)
                .Append("':");
            foreach (var setting in settings)
                builder
                    .Append('\n')
                    .Append(setting.Key)
                    .Append(" = ")
                    .Append(ReadSafely(setting));
            return builder.ToString();
        }

        internal static string ReadMatches(string query, IReadOnlyList<ProjectSetting> matches)
        {
            if (matches.Count == 1)
                return $"{matches[0].Key} = {ReadSafely(matches[0])}";

            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder
                .Append("Found ")
                .Append(matches.Count)
                .Append(" project settings matching '")
                .Append(query.Trim())
                .Append("' (showing ")
                .Append(Math.Min(matches.Count, MaximumDisplayedMatches))
                .Append("):");
            var displayed = 0;
            foreach (var match in matches)
            {
                if (displayed++ == MaximumDisplayedMatches)
                    break;

                builder
                    .Append('\n')
                    .Append(match.Key)
                    .Append(" = ")
                    .Append(ReadSafely(match));
            }
            return builder.ToString();
        }

        internal static string AmbiguousWrite(
            string verb,
            string query,
            IEnumerable<string> matchingKeys,
            string noun = "project settings")
        {
            var keys = matchingKeys
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder
                .Append("Cannot ")
                .Append(verb)
                .Append(" '")
                .Append(query.Trim())
                .Append("' because it matches ")
                .Append(keys.Count)
                .Append(' ')
                .Append(noun)
                .Append(". Use a more specific key (showing ")
                .Append(Math.Min(keys.Count, MaximumDisplayedMatches))
                .Append("):");
            var displayed = 0;
            foreach (string key in keys)
            {
                if (displayed++ == MaximumDisplayedMatches)
                    break;

                builder.Append('\n').Append(key);
            }
            return builder.ToString();
        }

        static string ReadSafely(ProjectSetting setting)
        {
            try
            {
                return setting.Read();
            }
            catch (Exception exception)
            {
                return $"<unavailable: {exception.Message}>";
            }
        }
    }
}
