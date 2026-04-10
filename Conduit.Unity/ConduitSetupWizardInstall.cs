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
        internal static Func<string>? GetCurrentPackageVersionOverride;
        internal static Func<string, string?>? ProbeExecutableVersionOverride;
        internal static Action<string>? StopRunningExecutableOverride;

        static readonly Dictionary<string, CachedExecutableVersion> executableVersionCache = new(StringComparer.OrdinalIgnoreCase);
        static CachedPackageVersion cachedPackageVersion;
        static bool hasCachedPackageVersion;

        public static ButtonModel EvaluateDownloadButton(string serverExecutablePath, string configuredExecutablePath, bool isRunning, bool hasError)
        {
            if (isRunning)
                return new() { State = ActionState.Running, Label = "Downloading MCP Server...", Hint = "Downloading the Windows and Linux server binaries." };

            if (hasError)
                return new() { State = ActionState.Error, Label = "Download MCP Server", Hint = "The previous download failed. Check the Console for details." };

            if (!CanDownloadServer(out var reason))
                return new() { State = ActionState.Disabled, Label = "Download MCP Server", Hint = reason };

            var executablePath = GetEffectiveExecutablePath(serverExecutablePath, configuredExecutablePath);
            if (ShouldOfferServerUpdate(executablePath, out var installedVersion, out var packageVersion))
                return new()
                {
                    State = ActionState.Enabled,
                    Label = "Update MCP Server",
                    Hint = $"Installed server {installedVersion} is older than package version {packageVersion}. Overwrite {executablePath}."
                };

            return executablePath.Length > 0
                ? new() { State = ActionState.Success, Label = "MCP Server Downloaded", Hint = executablePath }
                : new()
                {
                    State = ActionState.Enabled,
                    Label = "Download MCP Server",
                    Hint = $"Download the Windows and Linux server binaries into {GetInstallDisplayPath()}."
                };
        }

        public static string GetEffectiveExecutablePath(string serverExecutablePath, string configuredExecutablePath)
        {
            if (serverExecutablePath.Length > 0)
                return serverExecutablePath;

            if (configuredExecutablePath.Length > 0 && File.Exists(configuredExecutablePath))
                return configuredExecutablePath;

            return TryGetInstalledExecutablePath(out var installedPath) ? installedPath : string.Empty;
        }

        public static async Task<string> DownloadServerAsync()
        {
            if (!CanDownloadServer(out var reason))
                throw new InvalidOperationException(reason);

            var progressId = Progress.Start("Conduit Setup", "Downloading MCP server", Progress.Options.Managed);
            var wasCancelled = false;
            Progress.RegisterCancelCallback(progressId, () =>
            {
                wasCancelled = true;
                return true;
            });

            try
            {
                Directory.CreateDirectory(GetInstallDirectoryPath());
                var downloads = new[]
                {
                    new DownloadTarget
                    {
                        Url = "https://github.com/apkd/Conduit/releases/latest/download/conduit-linux-x64",
                        DestinationPath = GetInstalledLinuxExecutablePath(),
                        NeedsExecutableBit = true,
                    },
                    new DownloadTarget
                    {
                        Url = "https://github.com/apkd/Conduit/releases/latest/download/conduit-win-x64.exe",
                        DestinationPath = GetInstalledWindowsExecutablePath(),
                    },
                };

                for (var index = 0; index < downloads.Length; index++)
                    await DownloadTargetAsync(downloads[index], progressId, () => wasCancelled, index, downloads.Length);

                if (!TryGetInstalledExecutablePath(out var executablePath))
                    throw new InvalidOperationException("The server binaries were downloaded, but no executable matches the current OS.");

                return executablePath;
            }
            finally
            {
                Progress.Remove(progressId);
            }
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

        static string GetInstallDirectoryPath() => Combine(ConduitAssetPathUtility.GetProjectRootPath(), "Conduit");

        static string GetInstallDisplayPath() => $"{Path.GetFileName(ConduitAssetPathUtility.GetProjectRootPath())}/Conduit";

        static string GetInstalledWindowsExecutablePath() => Combine(GetInstallDirectoryPath(), "conduit.exe");

        static string GetInstalledLinuxExecutablePath() => Combine(GetInstallDirectoryPath(), "conduit");

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

        /*
         * Keep the download out of the project until it is complete.
         * A temp file avoids half-written executables at the final path and
         * lets us replace the destination in one short filesystem step.
         */
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

        static async Task DownloadTargetAsync(DownloadTarget target, int progressId, Func<bool> wasCancelled, int index, int total)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"conduit-{Guid.NewGuid():N}.download");
            try
            {
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

                    var progress = request.downloadProgress >= 0 ? request.downloadProgress : 0f;
                    Progress.Report(
                        progressId,
                        (index + progress) / total,
                        $"Downloading {Path.GetFileName(target.DestinationPath)} ({index + 1}/{total})");
                    await Task.Delay(100);
                }

                if (request.result != UnityWebRequest.Result.Success)
                    throw new InvalidOperationException(request.error ?? $"Download failed for '{target.Url}'.");

                PrepareDestinationForOverwrite(target.DestinationPath);
                File.Copy(tempPath, target.DestinationPath, true);
                if (target.NeedsExecutableBit)
                    SetExecutableBit(target.DestinationPath);
            }
            finally
            {
                TryDeleteFile(tempPath);
            }
        }

        static bool ShouldOfferServerUpdate(string executablePath, out string installedVersion, out string packageVersion)
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
            if (GetCurrentPackageVersionOverride != null)
                return GetCurrentPackageVersionOverride();

            var packageInfo = PackageInfo.FindForAssembly(typeof(ConduitProjectIdentity).Assembly);
            if (packageInfo == null || string.IsNullOrWhiteSpace(packageInfo.resolvedPath))
                return packageInfo?.version ?? string.Empty;

            var packageJsonPath = Path.Combine(Path.GetFullPath(packageInfo.resolvedPath), "package.json");
            if (!File.Exists(packageJsonPath))
                return packageInfo.version ?? string.Empty;

            var fileInfo = new FileInfo(packageJsonPath);
            lock (executableVersionCache)
                if (hasCachedPackageVersion
                    && PathsEqual(cachedPackageVersion.Path, packageJsonPath)
                    && cachedPackageVersion.Length == fileInfo.Length
                    && cachedPackageVersion.LastWriteUtc == fileInfo.LastWriteTimeUtc)
                    return cachedPackageVersion.Version;

            var version = packageInfo.version ?? string.Empty;
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

            var fullPath = Path.GetFullPath(executablePath);
            var fileInfo = new FileInfo(fullPath);
            lock (executableVersionCache)
                if (executableVersionCache.TryGetValue(fullPath, out var cached)
                    && cached.Length == fileInfo.Length
                    && cached.LastWriteUtc == fileInfo.LastWriteTimeUtc)
                {
                    version = cached.Version;
                    return version.Length > 0;
                }

            version = ProbeExecutableVersionOverride?.Invoke(fullPath) ?? ProbeExecutableVersion(fullPath) ?? string.Empty;
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

                if (process == null)
                    return null;

                if (!process.WaitForExit(3000))
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(1000);
                    }
                    catch { }

                    return null;
                }

                var output = process.StandardOutput.ReadToEnd().Trim();
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

            var length = 0;
            var sawDigit = false;
            for (; length < value.Length; length++)
            {
                var character = value[length];
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
            if (!IsCurrentPlatformInstalledExecutable(destinationPath))
                return;

            var fullPath = Path.GetFullPath(destinationPath);
            StopRunningExecutableOverride?.Invoke(fullPath);
            if (StopRunningExecutableOverride != null)
                return;

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

        static bool IsCurrentPlatformInstalledExecutable(string path)
            => Application.platform switch
            {
                RuntimePlatform.WindowsEditor => PathsEqual(path, GetInstalledWindowsExecutablePath()),
                RuntimePlatform.LinuxEditor => PathsEqual(path, GetInstalledLinuxExecutablePath()),
                _ => false,
            };

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
            lock (executableVersionCache)
            {
                executableVersionCache.Clear();
                cachedPackageVersion = default;
                hasCachedPackageVersion = false;
            }
        }

        struct DownloadTarget
        {
            public string Url;
            public string DestinationPath;
            public bool NeedsExecutableBit;
        }

        struct CachedExecutableVersion
        {
            public long Length;
            public DateTime LastWriteUtc;
            public string Version;
        }

        struct CachedPackageVersion
        {
            public string Path;
            public long Length;
            public DateTime LastWriteUtc;
            public string Version;
        }

        [DllImport("libc", SetLastError = true)]
        static extern int chmod(string path, int mode);
    }
}
