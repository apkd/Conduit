#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
#if MODULE_UNITYWEBREQUEST
using UnityEngine.Networking;
#endif

namespace Conduit
{
    static class ServerInstaller
    {
        internal static Action<string>? StopRunningExecutableOverride;

#if MODULE_UNITYWEBREQUEST
        static async Task DownloadTargetAsync(
            ServerDownloadTarget target,
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
                    ServerInstallation.SetExecutableBit(stagedPath);

                PrepareDestinationForOverwrite(target.DestinationPath);
                ReplaceDownloadedFile(stagedPath, target.DestinationPath);
            }
            finally
            {
                ConduitFileUtility.TryDelete(tempPath);
                ConduitFileUtility.TryDelete(stagedPath);
            }
        }
#else
        static Task DownloadTargetAsync(
            ServerDownloadTarget target,
            int progressId,
            Func<bool> wasCancelled,
            int index,
            int total
        ) => Task.FromException(
            new NotSupportedException("The Unity Web Request module is unavailable.")
        );
#endif

        internal static void ReplaceDownloadedFile(string stagedPath, string destinationPath)
        {
            if (File.Exists(destinationPath))
                File.Replace(stagedPath, destinationPath, null);
            else
                File.Move(stagedPath, destinationPath);
        }

        internal static void PrepareDestinationForOverwrite(string destinationPath)
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
                    if (process.Id == BridgeStatusUtility.ProcessId)
                        continue;

                    if (!SetupPathUtility.PathsEqual(TryGetProcessPath(process), fullPath))
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

        internal static Task<string> DownloadServerAsync(string existingExecutablePath = "")
            => DownloadServerAsync(SetupConfigurationLocation.Project, existingExecutablePath);

        internal static async Task<string> DownloadServerAsync(
            SetupConfigurationLocation location,
            string existingExecutablePath = ""
        )
        {
            if (!ServerInstallation.CanDownloadServer(out var reason))
                throw new InvalidOperationException(reason);

            if (existingExecutablePath.Length > 0
                && !ServerInstallation.CanAutomaticallyUpdateServer(
                    existingExecutablePath,
                    out var updateReason
                ))
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
                var downloads = ServerInstallation.CreateDownloadTargets(
                    location,
                    existingExecutablePath
                );

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

                if (location == SetupConfigurationLocation.User)
                    return ServerInstallation.GetUserInstalledExecutablePath();

                if (!ServerInstallation.TryGetInstalledExecutablePath(out var executablePath))
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

        internal static void ResetInstallStateForTests()
        {
            StopRunningExecutableOverride = null;
            ServerVersionProbe.ResetForTests();
            ServerExecutableLocator.ResetForTests();
        }
    }
}
