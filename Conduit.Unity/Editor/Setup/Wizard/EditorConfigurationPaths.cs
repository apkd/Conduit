#nullable enable

using System;
using System.IO;

namespace Conduit
{
    static class EditorConfigurationPaths
    {
        internal static bool SupportsProjectConfiguration(EditorClientSpec spec)
            => spec.ResolveProjectConfigPath is not null;

        internal static SetupConfigurationLocation GetDefaultConfigurationLocation(EditorClientSpec spec)
            => SupportsProjectConfiguration(spec)
                ? SetupConfigurationLocation.Project
                : SetupConfigurationLocation.User;

        internal static SetupConfigurationLocation GetPreferredConfigurationLocation(
            EditorClientSpec spec,
            SetupConfigurationLocation fallback
        )
        {
            bool hasProjectConfiguration = EditorConfiguration.TryGetConfiguredExecutablePath(
                spec,
                SetupConfigurationLocation.Project,
                out _,
                out _
            );
            bool hasUserConfiguration = EditorConfiguration.TryGetConfiguredExecutablePath(
                spec,
                SetupConfigurationLocation.User,
                out _,
                out _
            );
            return (hasProjectConfiguration, hasUserConfiguration) switch
            {
                (true, false) => SetupConfigurationLocation.Project,
                (false, true) => SetupConfigurationLocation.User,
                _ => fallback,
            };
        }

        internal static string? GetConfigPath(EditorClientSpec spec)
            => GetConfigPath(spec, GetDefaultConfigurationLocation(spec));

        internal static string? GetConfigPath(EditorClientSpec spec, SetupConfigurationLocation location)
        {
            var context = CreatePathContext();
            return GetConfigPathResolver(spec, location)?.Invoke(context);
        }

        internal static string? GetDisplayConfigPath(EditorClientSpec spec)
            => GetDisplayConfigPath(spec, GetDefaultConfigurationLocation(spec));

        internal static string? GetDisplayConfigPath(EditorClientSpec spec, SetupConfigurationLocation location)
        {
            if (EditorConfiguration.TryGetConfiguredExecutablePath(
                    spec,
                    location,
                    out _,
                    out var configuredConfigPath
                ))
                return configuredConfigPath;

            var configPaths = GetConfigPaths(spec, location);
            foreach (string configPath in configPaths)
                if (File.Exists(configPath))
                    return configPath;

            return GetConfigPath(spec, location) ?? (configPaths.Length > 0 ? configPaths[0] : null);
        }

        static SetupPathContext CreatePathContext()
            => new(
                ConduitAssetPathUtility.GetProjectRootPath(),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            );

        static Func<SetupPathContext, string?>? GetConfigPathResolver(
            EditorClientSpec spec,
            SetupConfigurationLocation location
        )
            => location switch
            {
                SetupConfigurationLocation.Project => spec.ResolveProjectConfigPath,
                SetupConfigurationLocation.User => spec.ResolveUserConfigPath,
                _ => throw new ArgumentOutOfRangeException(nameof(location), location, null),
            };

        internal static string[] GetConfigPaths(
            EditorClientSpec spec,
            SetupConfigurationLocation location
        )
        {
            var context = CreatePathContext();
            using var pooledList = ConduitPool.GetPooledList<string>(out var paths);
            using var pooledSet = ConduitPool.GetPooledSet<string>(out var uniquePaths);

            var configPathsResolver = location switch
            {
                SetupConfigurationLocation.Project => spec.ResolveProjectConfigPaths,
                SetupConfigurationLocation.User => spec.ResolveUserConfigPaths,
                _ => throw new ArgumentOutOfRangeException(nameof(location), location, null),
            };
            if (configPathsResolver?.Invoke(context) is { } candidatePaths)
                foreach (string? path in candidatePaths)
                    AddPath(path);
            AddPath(GetConfigPathResolver(spec, location)?.Invoke(context));
            return paths.ToArray();

            void AddPath(string? path)
            {
                if (path is not { Length: > 0 } || !uniquePaths.Add(path))
                    return;

                paths.Add(path);
            }
        }

        internal static bool HasUserConfigurationFile(EditorClientSpec spec)
        {
            foreach (string path in GetConfigPaths(spec, SetupConfigurationLocation.User))
                if (File.Exists(path))
                    return true;

            return false;
        }

        internal static string[] GetAllConfigPaths(EditorClientSpec spec)
        {
            using var pooledList = ConduitPool.GetPooledList<string>(out var paths);
            using var pooledSet = ConduitPool.GetPooledSet<string>(out var uniquePaths);

            AddPaths(GetConfigPaths(spec, SetupConfigurationLocation.Project));
            AddPaths(GetConfigPaths(spec, SetupConfigurationLocation.User));
            return paths.ToArray();

            void AddPaths(string[] candidates)
            {
                foreach (string path in candidates)
                    if (uniquePaths.Add(path))
                        paths.Add(path);
            }
        }

        internal static string? GetWriteConfigPath(
            EditorClientSpec spec,
            SetupConfigurationLocation location
        )
        {
            foreach (string configPath in GetConfigPaths(spec, location))
                if (File.Exists(configPath))
                    return configPath;

            return GetConfigPath(spec, location);
        }

        internal static int CountExistingConfigPaths(
            EditorClientSpec spec,
            SetupConfigurationLocation location
        )
        {
            int count = 0;
            foreach (string path in GetConfigPaths(spec, location))
                if (File.Exists(path))
                    count++;

            return count;
        }
    }
}
