#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;

namespace Conduit
{
    static class ProjectSettingsTool
    {
        const int MaximumDisplayedMatches = 32;

        internal static string Execute(PendingOperationState operation)
            => Execute(operation, ProjectSettingsRegistry.Build());

        internal static string Execute(
            PendingOperationState operation,
            ProjectSettingsRegistry registry)
        {
            var requestedOperation = ParseOperation(operation.args.FirstOrDefault());
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
            var matchingKeys = MatchKeys(distinct.Select(setting => setting.Key), query)
                .ToHashSet(StringComparer.Ordinal);
            return distinct.Where(setting => matchingKeys.Contains(setting.Key)).ToList();
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
            var groups = registry.Settings
                .Select(setting => TopLevelGroup(setting.Key))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(group => group, StringComparer.Ordinal)
                .ToList();
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
                var registrations = registry.Settings
                    .Where(setting => setting.Key.StartsWith(requestedGroup + ".", StringComparison.Ordinal))
                    .GroupBy(setting => setting.Key, StringComparer.Ordinal)
                    .ToList();
                var duplicate = registrations.FirstOrDefault(group => group.Count() > 1);
                if (duplicate != null)
                    throw DuplicateKey(duplicate.Key, duplicate.Count());

                var groupSettings = registrations
                    .Select(group => group.Single())
                    .OrderBy(setting => setting.Key, StringComparer.Ordinal)
                    .ToList();
                return ReadGroup(requestedGroup, groupSettings);
            }

            var matches = Match(registry.Settings, query);
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
            var matches = Match(registry.Settings, query);
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
            int registrations = registry.Settings.Count(candidate => candidate.Key == setting.Key);
            if (registrations != 1)
                throw DuplicateKey(setting.Key, registrations);
            return setting;
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
            => key.Replace(".", string.Empty).Replace("_", string.Empty);

        static string CollectionKey(string appendKey)
        {
            int separator = appendKey.LastIndexOf('.');
            return separator < 0 ? appendKey : appendKey[..separator];
        }

        static string TopLevelGroup(string key)
        {
            int separator = key.IndexOf('.');
            return separator < 0 ? key : key[..separator];
        }

        static string ListGroups(IReadOnlyList<string> groups)
        {
            var builder = new StringBuilder()
                .Append("Found ")
                .Append(groups.Count)
                .AppendLine(groups.Count == 1
                    ? " project settings group:"
                    : " project settings groups:");
            foreach (string group in groups)
                builder.AppendLine(group);
            return builder.ToString().TrimEnd();
        }

        static string ReadGroup(string group, IReadOnlyList<ProjectSetting> settings)
        {
            var builder = new StringBuilder()
                .Append("Found ")
                .Append(settings.Count)
                .Append(settings.Count == 1
                    ? " project setting in '"
                    : " project settings in '")
                .Append(group)
                .AppendLine("':");
            foreach (var setting in settings)
                builder.Append(setting.Key).Append(" = ").AppendLine(ReadSafely(setting));
            return builder.ToString().TrimEnd();
        }

        static string ReadMatches(string query, IReadOnlyList<ProjectSetting> matches)
        {
            if (matches.Count == 1)
                return $"{matches[0].Key} = {ReadSafely(matches[0])}";

            var builder = new StringBuilder()
                .Append("Found ")
                .Append(matches.Count)
                .Append(" project settings matching '")
                .Append(query.Trim())
                .Append("' (showing ")
                .Append(Math.Min(matches.Count, MaximumDisplayedMatches))
                .AppendLine("):");
            foreach (var match in matches.Take(MaximumDisplayedMatches))
                builder.Append(match.Key).Append(" = ").AppendLine(ReadSafely(match));
            return builder.ToString().TrimEnd();
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
            var builder = new StringBuilder()
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
                .AppendLine("):");
            foreach (string key in keys.Take(MaximumDisplayedMatches))
                builder.AppendLine(key);
            return builder.ToString().TrimEnd();
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
