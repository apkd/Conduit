#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Conduit
{
    static class ProjectSettingsOperations
    {
        internal static string Execute(PendingOperationState operation)
        {
            var requestedOperation = ParseOperation(
                operation.Args.Length > 0 ? operation.Args[0] : null
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
                ParseOperation(operation.Args.Length > 0 ? operation.Args[0] : null)
            );

        static string Execute(
            PendingOperationState operation,
            ProjectSettingsRegistry registry,
            ProjectSettingsOperation requestedOperation)
        {
            var key = operation.Target ?? string.Empty;
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
                return ProjectSettingsFormatter.ListGroups(groups);

            string? requestedGroup = groups.FirstOrDefault(group => group == normalized);
            if (requestedGroup == null)
            {
                string compactGroup = ProjectSettingKey.Compact(normalized);
                var compactGroups = groups
                    .Where(group => ProjectSettingKey.Compact(group) == compactGroup)
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

                return ProjectSettingsFormatter.ReadGroup(requestedGroup, groupSettings);
            }

            var matches = ProjectSettingMatcher.Match(registry, normalized);
            if (matches.Count == 0)
                return $"No project settings match '{query.Trim()}'.";

            EnsureUniqueWhenExact(registry, matches);
            return ProjectSettingsFormatter.ReadMatches(query, matches);
        }

        static string Set(
            PendingOperationState operation,
            ProjectSettingsRegistry registry,
            string query)
        {
            var matches = ProjectSettingMatcher.Match(registry, query);
            if (matches.Count == 0)
                return $"No project settings match '{query.Trim()}'.";
            if (matches.Count != 1)
                return ProjectSettingsFormatter.AmbiguousWrite(
                    "set",
                    query,
                    matches.Select(setting => setting.Key)
                );

            var setting = GetUnique(registry, matches[0]);
            RequireOperation(setting, ProjectSettingOperations.Set, "set");
            string previous = operation.ProjectSettingPrevious ??= setting.Read();
            if (operation.IsRestored)
            {
                string restoredValue = setting.Read();
                // the setter caused the reload; replay only when the setting still has its original value.
                if (restoredValue != previous)
                    return $"Set {setting.Key}: {previous} -> {restoredValue}";
            }

            operation.Target = setting.Key;
            SaveAndApply(operation, () => setting.SetValue!(operation.Snippet ?? "null"));
            return $"Set {setting.Key}: {previous} -> {setting.Read()}";
        }

        static string AddElement(
            PendingOperationState operation,
            ProjectSettingsRegistry registry,
            string query)
        {
            if (operation.IsRestored)
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
            var matches = ProjectSettingMatcher.MatchKeys(
                appendSettings.Select(group => group.Key),
                query
            );
            if (matches.Count == 0)
                return $"No project setting collections match '{query.Trim()}'.";
            if (matches.Count != 1)
                return ProjectSettingsFormatter.AmbiguousWrite(
                    "add an element to",
                    query,
                    matches,
                    "project setting collections"
                );

            var candidates = appendSettings[matches[0]].ToList();
            if (candidates.Count != 1)
                throw new InvalidOperationException(
                    $"Project setting collection '{matches[0]}' has {candidates.Count} append registrations. "
                    + "Each provider must register globally unique collection keys."
                );

            var setting = candidates[0];
            RequireOperation(setting, ProjectSettingOperations.AddElement, "add_element");
            operation.Target = setting.Key;
            SaveAndApply(operation, () => setting.AddValue!(operation.Snippet ?? "null"));
            return $"Added {setting.Key}: <absent> -> {setting.Read()}";
        }

        static string RemoveElement(
            PendingOperationState operation,
            ProjectSettingsRegistry registry,
            string query)
        {
            if (operation.IsRestored && operation.ProjectSettingPrevious is { } restoredPrevious)
                return $"Removed {ProjectSettingKey.Canonicalize(query)}: {restoredPrevious} -> <removed>";

            var removable = registry.Settings
                .Where(setting => Supports(setting, ProjectSettingOperations.RemoveElement))
                .ToList();
            var matches = ProjectSettingMatcher.Match(removable, query);
            if (matches.Count == 0)
                return $"No removable project setting elements match '{query.Trim()}'.";
            if (matches.Count != 1)
                return ProjectSettingsFormatter.AmbiguousWrite(
                    "remove an element matching",
                    query,
                    matches.Select(setting => setting.Key)
                );

            var setting = GetUnique(registry, matches[0]);
            RequireOperation(setting, ProjectSettingOperations.RemoveElement, "remove_element");
            string previous = operation.ProjectSettingPrevious ??= setting.Read();
            operation.Target = setting.Key;
            SaveAndApply(operation, setting.RemoveElement!);
            return $"Removed {setting.Key}: {previous} -> <removed>";
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

        static string CollectionKey(string appendKey)
        {
            int separator = appendKey.LastIndexOf('.');
            return separator < 0 ? appendKey : appendKey[..separator];
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

        enum ProjectSettingsOperation
        {
            Get,
            Set,
            AddElement,
            RemoveElement,
        }

    }
}
