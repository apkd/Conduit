#nullable enable

using System;
using System.Threading.Tasks;
using UnityEditor.PackageManager;
using UnityEngine.Networking;

namespace Conduit
{
    enum ConduitPackageUpdateState
    {
        NotApplicable,
        Current,
        Outdated,
        Unavailable,
    }

    readonly struct ConduitPackageUpdateStatus
    {
        internal ConduitPackageUpdateStatus(
            ConduitPackageUpdateState state,
            string installedHash = "",
            string latestHash = ""
        )
        {
            State = state;
            InstalledHash = installedHash;
            LatestHash = latestHash;
        }

        internal ConduitPackageUpdateState State { get; }
        internal string InstalledHash { get; }
        internal string LatestHash { get; }
    }

    static class ConduitPackageUpdater
    {
        internal const string ReleasesUrl = "https://github.com/apkd/Conduit/releases";
        internal const string PackageInstallUrl =
            "https://github.com/apkd/Conduit.git?path=/Conduit.Unity#release";
        const string LatestReleaseCommitUrl =
            "https://api.github.com/repos/apkd/Conduit/commits/release";

        internal static async Task<ConduitPackageUpdateStatus> CheckAsync()
        {
            // local, embedded, and source installs have no authoritative UPM update target and remain user-managed
            if (PackageInfo.FindForAssembly(typeof(ConduitProjectIdentity).Assembly) is not { } packageInfo
                || !IsOfficialGitPackage(packageInfo.source, packageInfo.packageId)
                || packageInfo.git?.hash is not { Length: > 0 } installedHash)
                return new(ConduitPackageUpdateState.NotApplicable);

            try
            {
                string latestHash = await GetLatestReleaseHashAsync();
                return CompareHashes(installedHash, latestHash);
            }
            catch
            {
                return new(
                    ConduitPackageUpdateState.Unavailable,
                    installedHash
                );
            }
        }

        internal static async Task UpdateAsync()
        {
            var request = Client.Add(PackageInstallUrl);
            while (!request.IsCompleted)
                await Task.Delay(100);

            if (request.Status == StatusCode.Failure)
                throw new InvalidOperationException(
                    request.Error?.message ?? "Unity Package Manager could not update Conduit."
                );
        }

        internal static bool IsOfficialGitPackage(PackageSource source, string? packageId)
            => source == PackageSource.Git
               && packageId?.Contains("github.com/apkd/Conduit", StringComparison.OrdinalIgnoreCase) == true;

        internal static ConduitPackageUpdateStatus CompareHashes(string installedHash, string latestHash)
            => new(
                string.Equals(installedHash, latestHash, StringComparison.OrdinalIgnoreCase)
                    ? ConduitPackageUpdateState.Current
                    : ConduitPackageUpdateState.Outdated,
                installedHash,
                latestHash
            );

        static async Task<string> GetLatestReleaseHashAsync()
        {
            using var request = UnityWebRequest.Get(LatestReleaseCommitUrl);
            request.timeout = 10;
            // requesting GitHub's SHA media type keeps the unauthenticated response body to one 40-byte hash
            request.SetRequestHeader("Accept", "application/vnd.github.sha");
            request.SetRequestHeader("User-Agent", "Conduit-Unity-Settings");

            var operation = request.SendWebRequest();
            while (!operation.isDone)
                await Task.Delay(100);

            if (request.result != UnityWebRequest.Result.Success)
                throw new InvalidOperationException(
                    request.error ?? $"GitHub returned HTTP {request.responseCode}."
                );

            string hash = request.downloadHandler.text.Trim();
            if (hash.Length != 40)
                throw new InvalidOperationException("GitHub returned an invalid release commit hash.");

            for (int index = 0, length = hash.Length; index < length; ++index)
                if (!Uri.IsHexDigit(hash[index]))
                    throw new InvalidOperationException("GitHub returned an invalid release commit hash.");

            return hash;
        }
    }
}
