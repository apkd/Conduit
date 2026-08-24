#nullable enable

using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Conduit
{
    static class ServerExecutableLocator
    {
        internal static bool TryFindServerExecutableOnPath(out string executablePath)
        {
            executablePath = FindOnPath("conduit", "conduit.exe") ?? string.Empty;
            return executablePath.Length > 0;
        }

        internal static string? FindOnPath(params string[] names)
        {
            string? path = Environment.GetEnvironmentVariable("PATH");
            return FindOnPathValue(path, names);
        }

        internal static string? FindOnPathValue(string? path, params string[] names)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            // scan PATH directly instead of depending on platform-specific where/which commands or shell setup
            var directories = path!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (string pathEntry in directories)
            {
                string directory = Environment.ExpandEnvironmentVariables(
                    pathEntry.Trim().Trim('"')
                );
                foreach (string name in names)
                {
                    try
                    {
                        string fullPath = Path.Combine(directory, name);
                        if (File.Exists(fullPath))
                            return Path.GetFullPath(fullPath);
                    }
                    catch { }
                }
            }

            return null;
        }

        internal static bool CommandMatches(
            string? configuredCommand,
            string expectedServerExecutablePath
        )
        {
            if (string.IsNullOrWhiteSpace(configuredCommand))
                return false;

            configuredCommand = ToPlatformPath(configuredCommand);
            if (expectedServerExecutablePath.Length > 0)
                return TryResolveCommand(configuredCommand, out var configuredPath)
                       && TryResolveCommand(expectedServerExecutablePath, out var expectedPath)
                       && SetupPathUtility.PathsEqual(configuredPath, expectedPath);

            return Path.GetFileNameWithoutExtension(configuredCommand)
                       .Contains("conduit", StringComparison.OrdinalIgnoreCase)
                   && TryResolveCommand(configuredCommand, out _);
        }

        internal static bool TryResolveConfiguredExecutable(
            string? command,
            out string executablePath
        )
        {
            executablePath = string.Empty;
            if (!CommandMatches(command, string.Empty))
                return false;

            return TryResolveCommand(command, out executablePath);
        }

        static bool TryResolveCommand(string? command, out string executablePath)
        {
            executablePath = string.Empty;
            command = ToPlatformPath(command);
            if (command is not { Length: > 0 })
                return false;

            if (Path.GetDirectoryName(command) is not { Length: > 0 })
            {
                string windowsName = Path.HasExtension(command) ? command : command + ".exe";
                command = FindOnPath(command, windowsName);
            }

            if (command is null || !File.Exists(command))
                return false;

            executablePath = Path.GetFullPath(command);
            return true;
        }

        internal static string ToPlatformPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            string normalizedPath = path!.Trim().Replace('\\', '/');
            if (Application.platform != RuntimePlatform.WindowsEditor)
                return normalizedPath;

            // windows editors can read WSL client configs, so mount paths share native identity
            if (normalizedPath.Length >= 6
                && normalizedPath[0] == '/'
                && normalizedPath[1] == 'm'
                && normalizedPath[2] == 'n'
                && normalizedPath[3] == 't'
                && normalizedPath[4] == '/'
                && char.IsLetter(normalizedPath[5])
                && (normalizedPath.Length == 6 || normalizedPath[6] == '/'))
            {
                char driveLetter = char.ToUpperInvariant(normalizedPath[5]);
                string remainder = normalizedPath.Length == 6
                    ? string.Empty
                    : normalizedPath[7..].Replace('/', '\\');
                return remainder.Length == 0 ? $"{driveLetter}:\\" : $"{driveLetter}:\\{remainder}";
            }

            return normalizedPath.Replace('/', Path.DirectorySeparatorChar);
        }

        internal static bool HasExtension(string[] extensionPaths, string searchPattern)
        {
            string prefix = searchPattern.TrimEnd('*');
            foreach (string extensionsPath in extensionPaths)
            {
                if (!Directory.Exists(extensionsPath))
                    continue;

                try
                {
                    if (Directory.EnumerateDirectories(extensionsPath)
                        .Any(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
                catch { }
            }

            return false;
        }

        internal static Func<string?>? DiscoverExecutableOverride;

        internal static void ResetForTests()
        {
            DiscoverExecutableOverride = null;
            ServerInstallation.ResetForTests();
        }

        internal static string GetEffectiveExecutablePath(
            string serverExecutablePath,
            string configuredExecutablePath
        )
        {
            if (serverExecutablePath.Length > 0 && File.Exists(serverExecutablePath))
                return serverExecutablePath;

            if (configuredExecutablePath.Length > 0 && File.Exists(configuredExecutablePath))
                return configuredExecutablePath;

            if (DiscoverExecutableOverride is { } discoverExecutable)
            {
                string? discoveredPath = discoverExecutable();
                if (discoveredPath is { Length: > 0 } && File.Exists(discoveredPath))
                    return discoveredPath;
            }
            else
            {
                if (EditorConfiguration.TryGetAnyConfiguredExecutablePath(
                        out var discoveredConfiguredPath,
                        out _
                    ))
                    return discoveredConfiguredPath;

                if (TryFindServerExecutableOnPath(out var pathExecutable))
                    return pathExecutable;
            }

            return ServerInstallation.TryGetInstalledExecutablePath(out var installedPath)
                ? installedPath
                : string.Empty;
        }

        internal static string GetEffectiveExecutablePath(
            SetupConfigurationLocation location,
            string serverExecutablePath,
            string configuredExecutablePath
        )
        {
            if (configuredExecutablePath.Length > 0 && File.Exists(configuredExecutablePath))
                return configuredExecutablePath;

            string projectRoot = ConduitAssetPathUtility.GetProjectRootPath();
            if (serverExecutablePath.Length > 0 && File.Exists(serverExecutablePath))
            {
                bool serverIsInProject = ServerInstallation.IsPathWithin(
                    serverExecutablePath,
                    projectRoot
                );
                bool belongsToLocation = location == SetupConfigurationLocation.Project
                    ? serverIsInProject
                    : !serverIsInProject;
                if (belongsToLocation)
                    return serverExecutablePath;
            }

            if (location == SetupConfigurationLocation.Project)
                return ServerInstallation.TryGetInstalledExecutablePath(out var projectExecutablePath)
                    ? projectExecutablePath
                    : string.Empty;

            if (ServerInstallation.TryGetUserInstalledExecutablePath(out var userExecutablePath)
                && File.Exists(userExecutablePath))
                return userExecutablePath;

            if (DiscoverExecutableOverride is { } discoverExecutable)
            {
                string? discoveredPath = discoverExecutable();
                if (discoveredPath is { Length: > 0 }
                    && File.Exists(discoveredPath)
                    && !ServerInstallation.IsPathWithin(discoveredPath, projectRoot))
                    return discoveredPath;
            }
            else if (TryFindServerExecutableOnPath(out var pathExecutable)
                     && !ServerInstallation.IsPathWithin(pathExecutable, projectRoot))
                return pathExecutable;

            return string.Empty;
        }
    }
}
