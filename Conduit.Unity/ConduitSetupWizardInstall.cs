#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;
using UnityEngine.Networking;

namespace Conduit
{
    static partial class ConduitSetupWizardUtility
    {
        const string LatestDownloadUrl = "https://github.com/apkd/Conduit/releases/latest/download";

        internal static Func<string>? GetCurrentPackageVersionOverride;
        internal static Func<string, string?>? ProbeExecutableVersionOverride;
        internal static Action<string>? StopRunningExecutableOverride;
        internal static Func<string?>? DiscoverExecutableOverride;
        internal static Func<string>? GetUserInstalledExecutablePathOverride;

        // frequent Preferences repaints would otherwise spawn a process for every frame
        static readonly Dictionary<string, CachedExecutableVersion> executableVersionCache = new(
            StringComparer.OrdinalIgnoreCase
        );
        static readonly Dictionary<string, bool> executableWriteabilityCache = new(StringComparer.OrdinalIgnoreCase);
        static CachedPackageVersion cachedPackageVersion;
        static bool hasCachedPackageVersion;

        public static ButtonModel EvaluateDownloadButton(
            string serverExecutablePath,
            string configuredExecutablePath,
            bool isRunning,
            bool hasError
        )
            => EvaluateDownloadButtonCore(
                ConfigurationLocation.Project,
                GetEffectiveExecutablePath(serverExecutablePath, configuredExecutablePath),
                isRunning,
                hasError
            );

        public static ButtonModel EvaluateDownloadButton(
            ConfigurationLocation location,
            string serverExecutablePath,
            string configuredExecutablePath,
            bool isRunning,
            bool hasError
        )
            => EvaluateDownloadButtonCore(
                location,
                GetEffectiveExecutablePath(
                    location,
                    serverExecutablePath,
                    configuredExecutablePath
                ),
                isRunning,
                hasError
            );

        internal static ButtonModel EvaluateDownloadButtonCore(
            ConfigurationLocation location,
            string executablePath,
            bool isRunning,
            bool hasError
        )
        {
            if (IsNixOsSystemProfilePath(executablePath))
                return new()
                {
                    State = ActionState.Disabled,
                    Label = "MCP server managed by NixOS",
                    Hint =
                        $"Conduit is installed through the NixOS system profile at `{executablePath}`. " +
                        "Update it through your NixOS configuration instead of this wizard.",
                    IsOutdated = false,
                };

            bool isOutdated = ShouldOfferServerUpdate(
                executablePath,
                out var installedVersion,
                out var packageVersion
            );

            if (isRunning)
                return new()
                {
                    State = ActionState.Running,
                    Label = executablePath.Length > 0 ? "Updating the MCP server..." : "Downloading the MCP server...",
                    Hint = executablePath.Length > 0
                        ? $"Downloading the latest server release and replacing `{executablePath}`."
                        : location == ConfigurationLocation.User
                            ? $"Downloading the MCP server for this operating system to " +
                              $"`{GetUserInstalledExecutablePath()}`."
                            : "Downloading the Windows and Linux server binaries.",
                    IsOutdated = isOutdated,
                };

            if (hasError)
                return new()
                {
                    State = ActionState.Error,
                    Label = executablePath.Length > 0 ? "Update the MCP server" : "Download the MCP server",
                    Hint =
                        "The previous server download failed. " +
                        "The Console contains the full error and the destination path.",
                    IsOutdated = isOutdated,
                };

            if (executablePath.Length > 0 && !TryGetExecutableVersion(executablePath, out _))
            {
                if (!CanDownloadServer(out var unsupportedReason))
                    return new()
                    {
                        State = ActionState.Error,
                        Label = "MCP server reinstall is unavailable on this platform",
                        Hint = unsupportedReason,
                        IsOutdated = isOutdated,
                    };

                if (!CanAutomaticallyUpdateServer(executablePath, out var updateReason))
                    return new()
                    {
                        State = ActionState.Error,
                        Label = "MCP server binary cannot be reinstalled automatically",
                        Hint = updateReason,
                        IsOutdated = isOutdated,
                    };

                return new()
                {
                    State = ActionState.Enabled,
                    Label = "Reinstall the MCP server",
                    Hint =
                        $"The server at `{executablePath}` could not report its version. " +
                        "Download a fresh copy and replace it in place.",
                    IsOutdated = isOutdated,
                };
            }

            if (isOutdated)
            {
                if (!CanAutomaticallyUpdateServer(executablePath, out var updateReason))
                    return new()
                    {
                        State = ActionState.Error,
                        Label = "MCP server binary is outdated but not writeable",
                        Hint = updateReason,
                        IsOutdated = true,
                    };

                if (!CanDownloadServer(out var unsupportedReason))
                    return new()
                    {
                        State = ActionState.Error,
                        Label = "MCP server update is unavailable on this platform",
                        Hint = unsupportedReason,
                        IsOutdated = true,
                    };

                return new()
                {
                    State = ActionState.Enabled,
                    Label = "Update the MCP server",
                    Hint =
                        $"The installed server version {installedVersion} is older than " +
                        $"the Unity package version {packageVersion}. Replace `{executablePath}` in place.",
                    IsOutdated = true,
                };
            }

            if (executablePath.Length > 0)
            {
                string hint = $"The MCP server is installed in: `{executablePath}`.";

                return new()
                {
                    State = ActionState.Success,
                    Label = "MCP server installed",
                    Hint = hint,
                    IsOutdated = isOutdated,
                };
            }

            if (!CanDownloadServer(out var reason))
                return new()
                {
                    State = ActionState.Disabled,
                    Label = "Download the MCP server",
                    Hint = reason,
                    IsOutdated = isOutdated,
                };

            return new()
            {
                State = ActionState.Enabled,
                Label = "Download the MCP server",
                Hint = location == ConfigurationLocation.User
                    ? $"Download only the MCP server binary for this operating system to `{GetUserInstalledExecutablePath()}`."
                    : $"Download the Windows and Linux binaries to the project directory: `{GetInstallDirectoryPath()}`. ",
                IsOutdated = isOutdated,
            };
        }

        public static string GetEffectiveExecutablePath(string serverExecutablePath, string configuredExecutablePath)
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
                if (TryGetAnyConfiguredExecutablePath(out var discoveredConfiguredPath, out _))
                    return discoveredConfiguredPath;

                if (TryFindServerExecutableOnPath(out var pathExecutable))
                    return pathExecutable;
            }

            return TryGetInstalledExecutablePath(out var installedPath) ? installedPath : string.Empty;
        }

        public static string GetEffectiveExecutablePath(
            ConfigurationLocation location,
            string serverExecutablePath,
            string configuredExecutablePath
        )
        {
            if (configuredExecutablePath.Length > 0 && File.Exists(configuredExecutablePath))
                return configuredExecutablePath;

            string projectRoot = ConduitAssetPathUtility.GetProjectRootPath();
            if (serverExecutablePath.Length > 0 && File.Exists(serverExecutablePath))
            {
                bool serverIsInProject = IsPathWithin(serverExecutablePath, projectRoot);
                bool belongsToLocation = location == ConfigurationLocation.Project
                    ? serverIsInProject
                    : !serverIsInProject;
                if (belongsToLocation)
                    return serverExecutablePath;
            }

            if (location == ConfigurationLocation.Project)
                return TryGetInstalledExecutablePath(out var projectExecutablePath)
                    ? projectExecutablePath
                    : string.Empty;

            if (TryGetUserInstalledExecutablePath(out var userExecutablePath)
                && File.Exists(userExecutablePath))
                return userExecutablePath;

            if (DiscoverExecutableOverride is { } discoverExecutable)
            {
                string? discoveredPath = discoverExecutable();
                if (discoveredPath is { Length: > 0 }
                    && File.Exists(discoveredPath)
                    && !IsPathWithin(discoveredPath, projectRoot))
                    return discoveredPath;
            }
            else if (TryFindServerExecutableOnPath(out var pathExecutable)
                     && !IsPathWithin(pathExecutable, projectRoot))
                return pathExecutable;

            return string.Empty;
        }

        public static async Task<string> DownloadServerAsync(string existingExecutablePath = "")
            => await DownloadServerAsync(ConfigurationLocation.Project, existingExecutablePath);

        public static async Task<string> DownloadServerAsync(
            ConfigurationLocation location,
            string existingExecutablePath = ""
        )
        {
            if (!CanDownloadServer(out var reason))
                throw new InvalidOperationException(reason);

            if (existingExecutablePath.Length > 0
                && !CanAutomaticallyUpdateServer(existingExecutablePath, out var updateReason))
                throw new InvalidOperationException(updateReason);

            int progressId = Progress.Start(
                "Conduit Setup",
                "Downloading MCP server",
                Progress.Options.Managed
            );
            bool wasCancelled = false;
            Progress.RegisterCancelCallback(progressId, () =>
            {
                wasCancelled = true;
                return true;
            });

            try
            {
                var downloads = CreateDownloadTargets(location, existingExecutablePath);

                for (int index = 0, count = downloads.Length; index < count; ++index)
                    await DownloadTargetAsync(
                        downloads[index],
                        progressId,
                        () => wasCancelled,
                        index,
                        count
                    );

                if (existingExecutablePath.Length > 0)
                    return Path.GetFullPath(existingExecutablePath);

                if (location == ConfigurationLocation.User)
                    return GetUserInstalledExecutablePath();

                if (!TryGetInstalledExecutablePath(out var executablePath))
                    throw new InvalidOperationException(
                        "The server binaries were downloaded, but no executable matches the current OS."
                    );

                return executablePath;
            }
            finally
            {
                Progress.Remove(progressId);
            }
        }

        internal static DownloadTarget[] CreateDownloadTargets(
            ConfigurationLocation location,
            string existingExecutablePath
        )
        {
            // project installs remain portable; user installs need only the current host's executable
            if (existingExecutablePath.Length > 0)
                return new[] { CreateCurrentPlatformTarget(existingExecutablePath) };

            if (location == ConfigurationLocation.User)
                return new[] { CreateCurrentPlatformTarget(GetUserInstalledExecutablePath()) };

            return new[]
            {
                new DownloadTarget
                {
                    Url = $"{LatestDownloadUrl}/{GetLinuxDownloadAssetName()}",
                    DestinationPath = GetInstalledLinuxExecutablePath(),
                    NeedsExecutableBit = true,
                },
                new DownloadTarget
                {
                    Url = LatestDownloadUrl + "/conduit-win-x64.exe",
                    DestinationPath = GetInstalledWindowsExecutablePath(),
                },
            };

            DownloadTarget CreateCurrentPlatformTarget(string destinationPath)
                => Application.platform switch
                {
                    RuntimePlatform.LinuxEditor => new()
                    {
                        Url = $"{LatestDownloadUrl}/{GetLinuxDownloadAssetName()}",
                        DestinationPath = destinationPath,
                        NeedsExecutableBit = true,
                    },
                    RuntimePlatform.WindowsEditor => new()
                    {
                        Url = LatestDownloadUrl + "/conduit-win-x64.exe",
                        DestinationPath = destinationPath,
                    },
                    _ => throw new InvalidOperationException(
                        "Conduit does not publish an MCP server binary for this editor platform."
                    ),
                };
        }

        static bool CanDownloadServer(out string reason)
        {
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
        }

        static bool CanAutomaticallyUpdateServer(string executablePath, out string reason)
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
                        TryDeleteFile(probePath);
                    }

                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        static string GetInstallDirectoryPath() => Combine(ConduitAssetPathUtility.GetProjectRootPath(), "Conduit");

        static string GetInstalledWindowsExecutablePath() => Combine(GetInstallDirectoryPath(), "conduit.exe");

        static string GetInstalledLinuxExecutablePath() => Combine(GetInstallDirectoryPath(), "conduit");

        static string GetUserInstalledExecutablePath()
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
                RuntimePlatform.WindowsEditor => Combine(localAppData, "Conduit", "conduit.exe"),
                RuntimePlatform.LinuxEditor => Combine(userHome, ".local", "bin", "conduit"),
                _ => throw new InvalidOperationException(
                    "Conduit does not publish an MCP server binary for this editor platform."
                ),
            };

        static bool TryGetUserInstalledExecutablePath(out string executablePath)
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

        internal static bool IsNixOsSystemProfilePath(string path)
        {
            try
            {
                // /run/current-system is the immutable system profile selected by a NixOS rebuild
                string fullPath = Path.GetFullPath(path).Replace('\\', '/');
                return fullPath.StartsWith("/run/current-system/", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        static bool TryGetInstalledExecutablePath(out string executablePath)
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

        static void SetExecutableBit(string path)
        {
            if (Application.platform is not (RuntimePlatform.LinuxEditor or RuntimePlatform.OSXEditor))
                return;

            const int mode755 = 493;
            if (chmod(path, mode755) != 0)
                throw new IOException($"chmod failed for '{path}' (errno {Marshal.GetLastWin32Error()}).");
        }

        static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        static async Task DownloadTargetAsync(
            DownloadTarget target,
            int progressId,
            Func<bool> wasCancelled,
            int index,
            int total
        )
        {
            string tempPath = Path.Combine(
                Path.GetTempPath(),
                $"conduit-{Guid.NewGuid():N}.download"
            );
            string destinationDirectory = Path.GetDirectoryName(target.DestinationPath)
                                          ?? throw new InvalidOperationException(
                                              $"Invalid destination path '{target.DestinationPath}'."
                                          );
            string stagedPath = Path.Combine(
                destinationDirectory,
                $".{Path.GetFileName(target.DestinationPath)}-{Guid.NewGuid():N}.update"
            );
            try
            {
                // stage beside the destination so the final replacement stays on one filesystem
                Directory.CreateDirectory(destinationDirectory);
                using var request = UnityWebRequest.Get(target.Url);
                request.downloadHandler = new DownloadHandlerFile(tempPath) { removeFileOnAbort = true };
                request.SetRequestHeader("User-Agent", "Conduit-Unity-Setup");

                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    if (wasCancelled())
                    {
                        request.Abort();
                        throw new OperationCanceledException("Server download was cancelled.");
                    }

                    float progress = request.downloadProgress >= 0 ? request.downloadProgress : 0f;
                    Progress.Report(
                        progressId,
                        (index + progress) / total,
                        $"Downloading {Path.GetFileName(target.DestinationPath)} ({index + 1}/{total})");
                    await Task.Delay(100);
                }

                if (request.result != UnityWebRequest.Result.Success)
                    throw new InvalidOperationException(request.error ?? $"Download failed for '{target.Url}'.");

                File.Copy(tempPath, stagedPath, true);
                if (target.NeedsExecutableBit)
                    SetExecutableBit(stagedPath);

                PrepareDestinationForOverwrite(target.DestinationPath);
                ReplaceDownloadedFile(stagedPath, target.DestinationPath);
            }
            finally
            {
                TryDeleteFile(tempPath);
                TryDeleteFile(stagedPath);
            }
        }

        internal static void ReplaceDownloadedFile(string stagedPath, string destinationPath)
        {
            if (File.Exists(destinationPath))
                File.Replace(stagedPath, destinationPath, null);
            else
                File.Move(stagedPath, destinationPath);
        }

        static bool ShouldOfferServerUpdate(
            string executablePath,
            out string installedVersion,
            out string packageVersion
        )
        {
            installedVersion = string.Empty;
            packageVersion = GetCurrentPackageVersion();
            if (executablePath.Length == 0
                || !TryGetExecutableVersion(executablePath, out installedVersion)
                || !TryParseVersionCore(installedVersion, out var installedCore)
                || !TryParseVersionCore(packageVersion, out var packageCore))
                return false;

            return packageCore > installedCore;
        }

        static string GetCurrentPackageVersion()
        {
            if (GetCurrentPackageVersionOverride is { } getCurrentPackageVersion)
                return getCurrentPackageVersion();

            var packageInfo = PackageInfo.FindForAssembly(typeof(ConduitProjectIdentity).Assembly);
            if (packageInfo?.resolvedPath is not { } resolvedPath
                || string.IsNullOrWhiteSpace(resolvedPath))
                return packageInfo?.version ?? string.Empty;

            string packageJsonPath = Path.Combine(
                Path.GetFullPath(resolvedPath),
                "package.json"
            );
            if (!File.Exists(packageJsonPath))
                return packageInfo.version ?? string.Empty;

            var fileInfo = new FileInfo(packageJsonPath);
            lock (executableVersionCache)
                if (hasCachedPackageVersion
                    && PathsEqual(cachedPackageVersion.Path, packageJsonPath)
                    && cachedPackageVersion.Length == fileInfo.Length
                    && cachedPackageVersion.LastWriteUtc == fileInfo.LastWriteTimeUtc)
                    return cachedPackageVersion.Version;

            string version = packageInfo.version ?? string.Empty;
            lock (executableVersionCache)
            {
                cachedPackageVersion = new()
                {
                    Path = packageJsonPath,
                    Length = fileInfo.Length,
                    LastWriteUtc = fileInfo.LastWriteTimeUtc,
                    Version = version,
                };
                hasCachedPackageVersion = true;
            }

            return version;
        }

        static bool TryGetExecutableVersion(string executablePath, out string version)
        {
            version = string.Empty;
            if (!File.Exists(executablePath))
                return false;

            string fullPath = Path.GetFullPath(executablePath);
            var fileInfo = new FileInfo(fullPath);
            lock (executableVersionCache)
                if (executableVersionCache.TryGetValue(fullPath, out var cached)
                    && cached.Length == fileInfo.Length
                    && cached.LastWriteUtc == fileInfo.LastWriteTimeUtc)
                {
                    version = cached.Version;
                    return version.Length > 0;
                }

            version = ProbeExecutableVersionOverride?.Invoke(fullPath)
                      ?? ProbeExecutableVersion(fullPath)
                      ?? string.Empty;
            lock (executableVersionCache)
                executableVersionCache[fullPath] = new()
                {
                    Length = fileInfo.Length,
                    LastWriteUtc = fileInfo.LastWriteTimeUtc,
                    Version = version,
                };

            return version.Length > 0;
        }

        static string? ProbeExecutableVersion(string executablePath)
        {
            try
            {
                using var process = Process.Start(
                    new ProcessStartInfo(executablePath, "--version")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    }
                );

                if (process is null)
                    return null;

                if (!process.WaitForExit(3000))
                {
                    // configured commands are external input and must not hang the Preferences UI indefinitely
                    try
                    {
                        process.Kill();
                        process.WaitForExit(1000);
                    }
                    catch { }

                    return null;
                }

                string output = process.StandardOutput.ReadToEnd().Trim();
                return process.ExitCode == 0 && output.Length > 0 ? output : null;
            }
            catch
            {
                return null;
            }
        }

        static bool TryParseVersionCore(string value, out Version version)
        {
            version = new(0, 0);
            if (string.IsNullOrWhiteSpace(value))
                return false;

            int length = 0;
            bool sawDigit = false;
            for (; length < value.Length; length++)
            {
                char character = value[length];
                if (character is >= '0' and <= '9')
                {
                    sawDigit = true;
                    continue;
                }

                if (character == '.')
                    continue;

                break;
            }

            return sawDigit && Version.TryParse(value[..length], out version);
        }

        static void PrepareDestinationForOverwrite(string destinationPath)
        {
            string fullPath = Path.GetFullPath(destinationPath);
            if (StopRunningExecutableOverride is { } stopRunningExecutable)
            {
                stopRunningExecutable(fullPath);
                return;
            }

            // running executables are locked on Windows, and every OS should start the new version afterward
            foreach (var process in Process.GetProcesses())
                try
                {
                    if (process.Id == Process.GetCurrentProcess().Id)
                        continue;

                    if (!PathsEqual(TryGetProcessPath(process), fullPath))
                        continue;

                    process.Kill();
                    if (!process.WaitForExit(5000))
                        throw new TimeoutException($"Timed out waiting for '{fullPath}' to exit.");
                }
                finally
                {
                    process.Dispose();
                }
        }

        static string? TryGetProcessPath(Process process)
        {
            try
            {
                return process.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }

        internal static void ResetInstallStateForTests()
        {
            GetCurrentPackageVersionOverride = null;
            ProbeExecutableVersionOverride = null;
            StopRunningExecutableOverride = null;
            DiscoverExecutableOverride = null;
            GetUserInstalledExecutablePathOverride = null;
            lock (executableVersionCache)
            {
                executableVersionCache.Clear();
                cachedPackageVersion = default;
                hasCachedPackageVersion = false;
            }
            lock (executableWriteabilityCache)
                executableWriteabilityCache.Clear();
        }

        internal struct DownloadTarget
        {
            public string Url { get; set; }
            public string DestinationPath { get; set; }
            public bool NeedsExecutableBit { get; set; }
        }

        struct CachedExecutableVersion
        {
            public long Length { get; set; }
            public DateTime LastWriteUtc { get; set; }
            public string Version { get; set; }
        }

        struct CachedPackageVersion
        {
            public string Path { get; set; }
            public long Length { get; set; }
            public DateTime LastWriteUtc { get; set; }
            public string Version { get; set; }
        }

        [DllImport("libc", SetLastError = true)]
        static extern int chmod(string path, int mode);

        [DllImport("libc", SetLastError = true)]
        static extern int access(string path, int mode);
    }
}
