#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Conduit
{
    static class ServerInstallation
    {
        internal static Func<string>? GetUserInstalledExecutablePathOverride;
        static readonly Dictionary<string, bool> executableWriteabilityCache = new(StringComparer.OrdinalIgnoreCase);

        internal static void ResetForTests()
        {
            GetUserInstalledExecutablePathOverride = null;
            lock (executableWriteabilityCache)
                executableWriteabilityCache.Clear();
        }

        internal static bool CanDownloadServer(out string reason)
        {
#if !MODULE_UNITYWEBREQUEST
            reason = "The Unity Web Request module is unavailable.";
            return false;
#else
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.LinuxEditor:
                    reason = string.Empty;
                    return true;
                default:
                    reason = "The setup wizard can currently download server binaries only on Windows and Linux.";
                    return false;
            }
#endif
        }

        internal static bool CanAutomaticallyUpdateServer(string executablePath, out string reason)
        {
            string fullPath = Path.GetFullPath(executablePath);
            string homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string projectPath = ConduitAssetPathUtility.GetProjectRootPath();
            // a PATH lookup can find system-managed binaries whose lifecycle Conduit must not take over
            if (!IsAutomaticUpdateLocation(fullPath, homePath, projectPath))
            {
                reason = $"Conduit cannot automatically update MCP server executables in this path: `{fullPath}`.";
                return false;
            }

            if (!CanWriteExecutable(fullPath))
            {
                reason =
                    $"Your user account cannot replace the MCP server at `{fullPath}`. Grant it write permission, " +
                    "move it into a writable directory, or update it manually from " +
                    $"{ConduitPackageUpdater.ReleasesUrl}.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        internal static bool IsAutomaticUpdateLocation(string path, string homePath, string projectPath)
            => IsPathWithin(path, homePath) || IsPathWithin(path, projectPath);

        internal static bool IsPathWithin(string path, string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                return false;

            string relativePath = Path.GetRelativePath(
                Path.GetFullPath(rootPath),
                Path.GetFullPath(path)
            );
            return relativePath != ".."
                   && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                   && !Path.IsPathRooted(relativePath);
        }

        static bool CanWriteExecutable(string path)
        {
            string fullPath = Path.GetFullPath(path);
            lock (executableWriteabilityCache)
                if (executableWriteabilityCache.TryGetValue(fullPath, out var cached))
                    return cached;

            bool canWrite = ProbeWriteability(fullPath);
            lock (executableWriteabilityCache)
                executableWriteabilityCache[fullPath] = canWrite;
            return canWrite;

            static bool ProbeWriteability(string fullPath)
            {
                try
                {
                    // replacing a binary requires write access to both the file and its containing directory
                    if (!File.Exists(fullPath)
                        || (File.GetAttributes(fullPath) & FileAttributes.ReadOnly) != 0)
                        return false;

                    if (Application.platform is RuntimePlatform.LinuxEditor or RuntimePlatform.OSXEditor
                        && access(fullPath, 2) != 0)
                        return false;

                    if (Path.GetDirectoryName(fullPath) is not { Length: > 0 } directoryPath)
                        return false;

                    string probePath = Path.Combine(
                        directoryPath,
                        $".conduit-write-{Guid.NewGuid():N}"
                    );
                    try
                    {
                        using (File.Create(probePath)) { }
                    }
                    finally
                    {
                        ConduitFileUtility.TryDelete(probePath);
                    }

                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        internal static string GetInstallDirectoryPath()
            => SetupPathUtility.Combine(ConduitAssetPathUtility.GetProjectRootPath(), "Conduit");

        internal static string GetInstalledWindowsExecutablePath()
            => SetupPathUtility.Combine(GetInstallDirectoryPath(), "conduit.exe");

        internal static string GetInstalledLinuxExecutablePath()
            => SetupPathUtility.Combine(GetInstallDirectoryPath(), "conduit");

        internal static string GetUserInstalledExecutablePath()
            => GetUserInstalledExecutablePathOverride?.Invoke()
               ?? GetUserInstalledExecutablePath(
                   Application.platform,
                   Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                   Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
               );

        internal static string GetUserInstalledExecutablePath(
            RuntimePlatform platform,
            string userHome,
            string localAppData
        )
            => platform switch
            {
                RuntimePlatform.WindowsEditor => SetupPathUtility.Combine(localAppData, "Conduit", "conduit.exe"),
                RuntimePlatform.LinuxEditor => SetupPathUtility.Combine(userHome, ".local", "bin", "conduit"),
                _ => throw new InvalidOperationException(
                    "Conduit does not publish an MCP server binary for this editor platform."
                ),
            };

        internal static bool TryGetUserInstalledExecutablePath(out string executablePath)
        {
            if (Application.platform is not (RuntimePlatform.WindowsEditor or RuntimePlatform.LinuxEditor))
            {
                executablePath = string.Empty;
                return false;
            }

            executablePath = GetUserInstalledExecutablePath();
            return true;
        }

        // the generic glibc build cannot run on NixOS without an FHS compatibility environment
        internal static string GetLinuxDownloadAssetName() =>
            IsNixOsLinux(
                Application.platform == RuntimePlatform.LinuxEditor,
                File.Exists,
                File.ReadLines
            )
                ? "conduit-linux-musl-x64"
                : "conduit-linux-x64";

        internal static bool IsNixOsLinux(
            bool isLinux,
            Func<string, bool> fileExists,
            Func<string, IEnumerable<string>> readLines)
        {
            if (!isLinux)
                return false;

            return OsReleaseIdentifiesNixOs("/etc/os-release")
                   || OsReleaseIdentifiesNixOs("/usr/lib/os-release");

            bool OsReleaseIdentifiesNixOs(string path)
            {
                if (!fileExists(path))
                    return false;

                try
                {
                    foreach (string line in readLines(path))
                    {
                        string trimmed = line.Trim();
                        if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                            continue;

                        int separatorIndex = trimmed.IndexOf('=');
                        if (separatorIndex <= 0)
                            continue;

                        string key = trimmed[..separatorIndex].Trim();
                        if (key is not ("ID" or "ID_LIKE"))
                            continue;

                        string value = trimmed[(separatorIndex + 1)..].Trim().Trim('"', '\'');
                        if (key == "ID" && string.Equals(value, "nixos", StringComparison.OrdinalIgnoreCase))
                            return true;

                        if (key == "ID_LIKE")
                            foreach (string token in value.Split(
                                         new[] { ' ', '\t' },
                                         StringSplitOptions.RemoveEmptyEntries
                                     ))
                                if (string.Equals(token, "nixos", StringComparison.OrdinalIgnoreCase))
                                    return true;
                    }
                }
                catch (IOException)
                {
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }

                return false;
            }
        }

        internal static bool IsNixOsManagedExecutablePath(string path)
        {
            try
            {
                string normalizedPath = path.Replace('\\', '/');
                if (IsManagedPath(normalizedPath))
                    return true;

                string fullPath = Path.GetFullPath(path).Replace('\\', '/');
                return IsManagedPath(fullPath);

                static bool IsManagedPath(string candidate)
                    => candidate.StartsWith("/nix/store/", StringComparison.Ordinal)
                       || candidate.StartsWith("/run/current-system/", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryGetInstalledExecutablePath(out string executablePath)
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor:
                    executablePath = GetInstalledWindowsExecutablePath();
                    return File.Exists(executablePath);
                case RuntimePlatform.LinuxEditor:
                    executablePath = GetInstalledLinuxExecutablePath();
                    return File.Exists(executablePath);
                default:
                    executablePath = string.Empty;
                    return false;
            }
        }

        internal static void SetExecutableBit(string path)
        {
            if (Application.platform is not (RuntimePlatform.LinuxEditor or RuntimePlatform.OSXEditor))
                return;

            const int mode755 = 493;
            if (chmod(path, mode755) != 0)
                throw new IOException($"chmod failed for '{path}' (errno {Marshal.GetLastWin32Error()}).");
        }

        [DllImport("libc", SetLastError = true)]
        static extern int chmod(string path, int mode);

        [DllImport("libc", SetLastError = true)]
        static extern int access(string path, int mode);

        const string LatestDownloadUrl = "https://github.com/apkd/Conduit/releases/latest/download";

        internal static ServerDownloadTarget[] CreateDownloadTargets(
            SetupConfigurationLocation location,
            string existingExecutablePath
        )
        {
            // project installs remain portable; user installs need only the current host's executable
            if (existingExecutablePath.Length > 0)
                return new[] { CreateCurrentPlatformTarget(existingExecutablePath) };

            if (location == SetupConfigurationLocation.User)
                return new[] { CreateCurrentPlatformTarget(GetUserInstalledExecutablePath()) };

            return new ServerDownloadTarget[]
            {
                new(
                    $"{LatestDownloadUrl}/{GetLinuxDownloadAssetName()}",
                    GetInstalledLinuxExecutablePath(),
                    needsExecutableBit: true
                ),
                new(
                    LatestDownloadUrl + "/conduit-win-x64.exe",
                    GetInstalledWindowsExecutablePath(),
                    needsExecutableBit: false
                ),
            };

            ServerDownloadTarget CreateCurrentPlatformTarget(string destinationPath)
                => Application.platform switch
                {
                    RuntimePlatform.LinuxEditor => new(
                        $"{LatestDownloadUrl}/{GetLinuxDownloadAssetName()}",
                        destinationPath,
                        needsExecutableBit: true
                    ),
                    RuntimePlatform.WindowsEditor => new(
                        LatestDownloadUrl + "/conduit-win-x64.exe",
                        destinationPath,
                        needsExecutableBit: false
                    ),
                    _ => throw new InvalidOperationException(
                        "Conduit does not publish an MCP server binary for this editor platform."
                    ),
                };
        }
    }
}
