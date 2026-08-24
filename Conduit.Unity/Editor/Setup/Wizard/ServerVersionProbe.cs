#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Conduit
{
    static class ServerVersionProbe
    {
        internal static Func<string>? GetCurrentPackageVersionOverride;
        internal static Func<string, string?>? ProbeExecutableVersionOverride;

        // frequent Preferences repaints would otherwise spawn a process for every frame
        static readonly Dictionary<string, CachedExecutableVersion> executableVersionCache = new(
            StringComparer.OrdinalIgnoreCase
        );
        static CachedPackageVersion cachedPackageVersion;
        static bool hasCachedPackageVersion;

        internal static void ResetForTests()
        {
            GetCurrentPackageVersionOverride = null;
            ProbeExecutableVersionOverride = null;
            lock (executableVersionCache)
            {
                executableVersionCache.Clear();
                cachedPackageVersion = default;
                hasCachedPackageVersion = false;
            }
        }

        internal static bool ShouldOfferServerUpdate(
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
                    && SetupPathUtility.PathsEqual(cachedPackageVersion.Path, packageJsonPath)
                    && cachedPackageVersion.Length == fileInfo.Length
                    && cachedPackageVersion.LastWriteUtc == fileInfo.LastWriteTimeUtc)
                    return cachedPackageVersion.Version;

            string version = packageInfo.version ?? string.Empty;
            lock (executableVersionCache)
            {
                cachedPackageVersion = new(
                    packageJsonPath,
                    fileInfo.Length,
                    fileInfo.LastWriteTimeUtc,
                    version
                );
                hasCachedPackageVersion = true;
            }

            return version;
        }

        internal static bool TryGetExecutableVersion(string executablePath, out string version)
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
                executableVersionCache[fullPath] = new(
                    fileInfo.Length,
                    fileInfo.LastWriteTimeUtc,
                    version
                );

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

        readonly struct CachedExecutableVersion
        {
            internal CachedExecutableVersion(long length, DateTime lastWriteUtc, string version)
            {
                Length = length;
                LastWriteUtc = lastWriteUtc;
                Version = version;
            }

            internal long Length { get; }
            internal DateTime LastWriteUtc { get; }
            internal string Version { get; }
        }

        readonly struct CachedPackageVersion
        {
            internal CachedPackageVersion(
                string path,
                long length,
                DateTime lastWriteUtc,
                string version
            )
            {
                Path = path;
                Length = length;
                LastWriteUtc = lastWriteUtc;
                Version = version;
            }

            internal string Path { get; }
            internal long Length { get; }
            internal DateTime LastWriteUtc { get; }
            internal string Version { get; }
        }
    }
}
