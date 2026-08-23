#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Conduit
{
    static class ProjectSettingsTool
    {
        const int MaximumDisplayedMatches = 32;

        internal static string Execute(PendingOperationState operation)
        {
            var requestedOperation = ParseOperation(
                operation.args.Length > 0 ? operation.args[0] : null
            );
            try
            {
                return Execute(
                    operation,
                    ProjectSettingsRegistry.Build(),
                    requestedOperation
                );
            }
            finally
            {
                if (requestedOperation != ProjectSettingsOperation.Get)
                    ProjectSettingsRegistry.Invalidate();
            }
        }

        internal static string Execute(
            PendingOperationState operation,
            ProjectSettingsRegistry registry)
            => Execute(
                operation,
                registry,
                ParseOperation(operation.args.Length > 0 ? operation.args[0] : null)
            );

        static string Execute(
            PendingOperationState operation,
            ProjectSettingsRegistry registry,
            ProjectSettingsOperation requestedOperation)
        {
            string key = operation.target ?? string.Empty;
            if (requestedOperation != ProjectSettingsOperation.Get
                && EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException(
                    "Project settings cannot be changed while Unity is in or entering play mode."
                );

            return requestedOperation switch
            {
                ProjectSettingsOperation.Get => Get(registry, key),
                ProjectSettingsOperation.Set => Set(operation, registry, key),
                ProjectSettingsOperation.AddElement => AddElement(operation, registry, key),
                ProjectSettingsOperation.RemoveElement => RemoveElement(operation, registry, key),
                _ => throw new ArgumentOutOfRangeException(nameof(requestedOperation)),
            };
        }

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

        static ProjectSettingsOperation ParseOperation(string? operation)
            => operation?.Trim().ToLowerInvariant() switch
            {
                "get"            => ProjectSettingsOperation.Get,
                "set"            => ProjectSettingsOperation.Set,
                "add_element"    => ProjectSettingsOperation.AddElement,
                "remove_element" => ProjectSettingsOperation.RemoveElement,
                _ => throw new ArgumentException(
                    "Project settings operation must be get, set, add_element, or remove_element.",
                    nameof(operation)
                ),
            };

        static string Get(ProjectSettingsRegistry registry, string query)
        {
            string normalized = ProjectSettingKey.Canonicalize(query);
            var groups = registry.TopLevelGroups;
            if (normalized.Length == 0)
                return ListGroups(groups);

            string? requestedGroup = groups.FirstOrDefault(group => group == normalized);
            if (requestedGroup == null)
            {
                string compactGroup = Compact(normalized);
                var compactGroups = groups
                    .Where(group => Compact(group) == compactGroup)
                    .ToList();
                if (compactGroups.Count == 1)
                    requestedGroup = compactGroups[0];
            }

            if (requestedGroup != null)
            {
                string groupPrefix = requestedGroup + ".";
                var groupSettings = new List<ProjectSetting>();
                ProjectSetting? duplicate = null;
                foreach (var setting in registry.DistinctSettings)
                {
                    if (!setting.Key.StartsWith(groupPrefix, StringComparison.Ordinal))
                        continue;

                    groupSettings.Add(setting);
                    if (duplicate == null && registry.CountRegistrations(setting.Key) > 1)
                        duplicate = setting;
                }
                if (duplicate != null)
                    throw DuplicateKey(
                        duplicate.Key,
                        registry.CountRegistrations(duplicate.Key)
                    );

                return ReadGroup(requestedGroup, groupSettings);
            }

            var matches = Match(registry, normalized);
            if (matches.Count == 0)
                return $"No project settings match '{query.Trim()}'.";

            EnsureUniqueWhenExact(registry, matches);
            return ReadMatches(query, matches);
        }

        static string Set(
            PendingOperationState operation,
            ProjectSettingsRegistry registry,
            string query)
        {
            var matches = Match(registry, query);
            if (matches.Count == 0)
                return $"No project settings match '{query.Trim()}'.";
            if (matches.Count != 1)
                return AmbiguousWrite("set", query, matches.Select(setting => setting.Key));

            var setting = GetUnique(registry, matches[0]);
            RequireOperation(setting, ProjectSettingOperations.Set, "set");
            string previous = operation.project_setting_previous ??= setting.Read();
            if (operation.is_restored)
            {
                string restoredValue = setting.Read();
                // the setter caused the reload; replay only when the setting still has its original value.
                if (restoredValue != previous)
                    return $"Set {setting.Key}: {previous} -> {restoredValue}";
            }

            operation.target = setting.Key;
            SaveAndApply(operation, () => setting.SetValue!(operation.snippet ?? "null"));
            return $"Set {setting.Key}: {previous} -> {setting.Read()}";
        }

        static string AddElement(
            PendingOperationState operation,
            ProjectSettingsRegistry registry,
            string query)
        {
            if (operation.is_restored)
            {
                string resolvedKey = ProjectSettingKey.Canonicalize(query);
                var applied = registry.Settings
                    .Where(setting => setting.Key == resolvedKey
                                      && Supports(setting, ProjectSettingOperations.RemoveElement))
                    .ToList();
                if (applied.Count == 1)
                    return $"Added {resolvedKey}: <absent> -> {applied[0].Read()}";
                if (applied.Count > 1)
                    throw DuplicateKey(resolvedKey, applied.Count);

                query = CollectionKey(resolvedKey);
            }

            var appendSettings = registry.Settings
                .Where(setting => Supports(setting, ProjectSettingOperations.AddElement))
                .ToLookup(setting => CollectionKey(setting.Key), StringComparer.Ordinal);
            var matches = MatchKeys(appendSettings.Select(group => group.Key), query);
            if (matches.Count == 0)
                return $"No project setting collections match '{query.Trim()}'.";
            if (matches.Count != 1)
                return AmbiguousWrite("add an element to", query, matches, "project setting collections");

            var candidates = appendSettings[matches[0]].ToList();
            if (candidates.Count != 1)
                throw new InvalidOperationException(
                    $"Project setting collection '{matches[0]}' has {candidates.Count} append registrations. "
                    + "Each provider must register globally unique collection keys."
                );

            var setting = candidates[0];
            RequireOperation(setting, ProjectSettingOperations.AddElement, "add_element");
            operation.target = setting.Key;
            SaveAndApply(operation, () => setting.AddValue!(operation.snippet ?? "null"));
            return $"Added {setting.Key}: <absent> -> {setting.Read()}";
        }

        static string RemoveElement(
            PendingOperationState operation,
            ProjectSettingsRegistry registry,
            string query)
        {
            if (operation.is_restored && operation.project_setting_previous is { } restoredPrevious)
                return $"Removed {ProjectSettingKey.Canonicalize(query)}: {restoredPrevious} -> <removed>";

            var removable = registry.Settings
                .Where(setting => Supports(setting, ProjectSettingOperations.RemoveElement))
                .ToList();
            var matches = Match(removable, query);
            if (matches.Count == 0)
                return $"No removable project setting elements match '{query.Trim()}'.";
            if (matches.Count != 1)
                return AmbiguousWrite("remove an element matching", query, matches.Select(setting => setting.Key));

            var setting = GetUnique(registry, matches[0]);
            RequireOperation(setting, ProjectSettingOperations.RemoveElement, "remove_element");
            string previous = operation.project_setting_previous ??= setting.Read();
            operation.target = setting.Key;
            SaveAndApply(operation, setting.RemoveElement!);
            return $"Removed {setting.Key}: {previous} -> <removed>";
        }

        static void SaveAndApply(
            PendingOperationState operation,
            Action apply)
        {
            OperationPersistence.SaveActiveOperation(operation, BridgeCommandKind.ProjectSettings);
            apply();
            AssetDatabase.SaveAssets();
        }

        static void RequireOperation(
            ProjectSetting setting,
            ProjectSettingOperations required,
            string operation)
        {
            if (Supports(setting, required))
                return;
            if (setting.Operations == ProjectSettingOperations.None)
                throw new InvalidOperationException($"Project setting '{setting.Key}' is read-only.");

            throw new InvalidOperationException(
                $"Project setting '{setting.Key}' does not support {operation}."
            );
        }

        static bool Supports(ProjectSetting setting, ProjectSettingOperations operation)
            => (setting.Operations & operation) != 0;

        static ProjectSetting GetUnique(ProjectSettingsRegistry registry, ProjectSetting setting)
        {
            int registrations = registry.CountRegistrations(setting.Key);
            if (registrations != 1)
                throw DuplicateKey(setting.Key, registrations);
            return setting;
        }

        static List<ProjectSetting> Match(ProjectSettingsRegistry registry, string query)
        {
            var normalized = ProjectSettingKey.Canonicalize(query);
            if (normalized.Length > 0 && registry.TryGetDistinct(normalized, out var exact))
                return new() { exact };

            return MatchDistinct(registry.DistinctSettings, normalized);
        }

        static void EnsureUniqueWhenExact(
            ProjectSettingsRegistry registry,
            IReadOnlyList<ProjectSetting> matches)
        {
            if (matches.Count == 1)
                GetUnique(registry, matches[0]);
        }

        static InvalidOperationException DuplicateKey(string key, int registrations)
            => new(
                $"Project setting key '{key}' was registered {registrations} times. "
                + "Each provider must register globally unique keys."
            );

        static List<string> MatchKeys(IEnumerable<string> candidates, string query)
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

        static string CollectionKey(string appendKey)
        {
            int separator = appendKey.LastIndexOf('.');
            return separator < 0 ? appendKey : appendKey[..separator];
        }

        static string ListGroups(IReadOnlyList<string> groups)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
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

        static string ReadGroup(string group, IReadOnlyList<ProjectSetting> settings)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
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

        static string ReadMatches(string query, IReadOnlyList<ProjectSetting> matches)
        {
            if (matches.Count == 1)
                return $"{matches[0].Key} = {ReadSafely(matches[0])}";

            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
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

        static string AmbiguousWrite(
            string verb,
            string query,
            IEnumerable<string> matchingKeys,
            string noun = "project settings")
        {
            var keys = matchingKeys
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
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

        enum ProjectSettingsOperation
        {
            Get,
            Set,
            AddElement,
            RemoveElement,
        }
    }
}
