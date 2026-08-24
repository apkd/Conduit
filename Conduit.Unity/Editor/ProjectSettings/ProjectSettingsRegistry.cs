#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{

    /// <summary>Builds the catalog used by the <c>project_settings</c> tool.</summary>
    public sealed class ProjectSettingsRegistry
    {
        static readonly object cacheGate = new();
        static ProjectSettingsRegistry? cachedRegistry;
        static uint cachedDependencyVersion;
        readonly List<ProjectSetting> settings = new(2048);
        List<ProjectSetting>? distinctSettings;
        Dictionary<string, (ProjectSetting Setting, int Count)>? registrations;
        string[]? topLevelGroups;

        static ProjectSettingsRegistry()
            => EditorApplication.projectChanged += Invalidate;

        internal IReadOnlyList<ProjectSetting> Settings => settings;

        internal IReadOnlyList<ProjectSetting> DistinctSettings
        {
            get
            {
                EnsureIndex();
                return distinctSettings!;
            }
        }

        internal IReadOnlyList<string> TopLevelGroups
        {
            get
            {
                EnsureIndex();
                return topLevelGroups!;
            }
        }

        /// <summary>Adds a scalar, enum, Unity object reference, or JSON-serializable project setting.</summary>
        public void Add<T>(string key, Func<T> read, Action<T>? write = null)
        {
            if (read == null)
                throw new ArgumentNullException(nameof(read));

            Add(
                key,
                () => ProjectSettingValueCodec.Format(read(), typeof(T)),
                write == null
                    ? null
                    : value => write((T)ProjectSettingValueCodec.Parse(value, typeof(T))!)
            );
        }

        internal void Add(string key, Func<string> read, Action<string>? write = null)
            => Register(key, read, write, null, null);

        internal void AddCollectionAppend(
            string key,
            Func<string> read,
            Action<string> add)
            => Register(key, read, null, add, null);

        internal void AddCollectionElement(
            string key,
            Func<string> read,
            Action<string> set,
            Action remove)
            => Register(key, read, set, null, remove);

        void Register(
            string key,
            Func<string> read,
            Action<string>? set,
            Action<string>? add,
            Action? remove)
        {
            string canonicalKey = ProjectSettingKey.Canonicalize(key);
            if (canonicalKey.Length == 0)
                throw new ArgumentException(
                    "A project setting key must contain at least one letter or digit.",
                    nameof(key)
                );

            settings.Add(new(canonicalKey, read, set, add, remove));
            distinctSettings = null;
            registrations = null;
            topLevelGroups = null;
        }

        internal int CountRegistrations(string key)
        {
            EnsureIndex();
            return registrations!.TryGetValue(key, out var registration)
                ? registration.Count
                : 0;
        }

        internal bool TryGetDistinct(string key, out ProjectSetting setting)
        {
            EnsureIndex();
            if (registrations!.TryGetValue(key, out var registration))
            {
                setting = registration.Setting;
                return true;
            }

            setting = null!;
            return false;
        }

        void EnsureIndex()
        {
            if (distinctSettings != null)
                return;

            var indexed = new Dictionary<string, (ProjectSetting Setting, int Count)>(settings.Count, StringComparer.Ordinal);
            foreach (var setting in settings)
            {
                if (indexed.TryGetValue(setting.Key, out var registration))
                    indexed[setting.Key] = (registration.Setting, registration.Count + 1);
                else
                    indexed.Add(setting.Key, (setting, 1));
            }

            var distinct = new List<ProjectSetting>(indexed.Count);
            foreach (var registration in indexed.Values)
                distinct.Add(registration.Setting);
            distinct.Sort(static (left, right) => string.Compare(
                left.Key,
                right.Key,
                StringComparison.Ordinal
            ));
            var groups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var setting in distinct)
            {
                var separator = setting.Key.IndexOf('.');
                groups.Add(separator < 0 ? setting.Key : setting.Key[..separator]);
            }

            distinctSettings = distinct;
            registrations = indexed;
            topLevelGroups = new string[groups.Count];
            groups.CopyTo(topLevelGroups);
            Array.Sort(topLevelGroups, StringComparer.Ordinal);
        }

        internal static ProjectSettingsRegistry Build()
        {
            var dependencyVersion = AssetDatabase.GlobalArtifactDependencyVersion;
            lock (cacheGate)
            {
                if (cachedRegistry != null
                    && cachedDependencyVersion == dependencyVersion)
                    return cachedRegistry;

                var registry = new ProjectSettingsRegistry();

                // built-in and package providers share one discovery and failure-isolation path.
                foreach (var method in TypeCache.GetMethodsWithAttribute<ConduitProjectSettingsProviderAttribute>())
                {
                    string provider = $"{method.DeclaringType?.FullName}.{method.Name}";
                    int initialCount = registry.settings.Count;
                    try
                    {
                        var parameters = method.GetParameters();
                        if (!method.IsStatic
                            || method.ReturnType != typeof(void)
                            || parameters.Length != 1
                            || parameters[0].ParameterType != typeof(ProjectSettingsRegistry))
                        {
                            ConduitDiagnostics.Warn(
                                $"Ignoring invalid project settings provider '{provider}'. " +
                                "Providers must be static void methods with one ProjectSettingsRegistry parameter."
                            );
                            continue;
                        }

                        method.Invoke(null, new object[] { registry });
                    }
                    catch (Exception exception)
                    {
                        // provider registration is atomic; a failure must not leave a partial catalog behind.
                        registry.settings.RemoveRange(initialCount, registry.settings.Count - initialCount);
                        ConduitDiagnostics.Warn(
                            $"Project settings provider '{provider}' failed: " +
                            (exception is TargetInvocationException { InnerException: { } inner }
                                ? inner.Message
                                : exception.Message)
                        );
                    }
                }

                cachedDependencyVersion = AssetDatabase.GlobalArtifactDependencyVersion;
                return cachedRegistry = registry;
            }
        }

        internal static void Invalidate()
        {
            lock (cacheGate)
                cachedRegistry = null;
        }
    }
}
