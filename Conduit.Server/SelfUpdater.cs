using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Conduit;

static class SelfUpdater
{
    const string Repository = "apkd/Conduit";

    static readonly Uri LatestReleaseUri = new($"https://api.github.com/repos/{Repository}/releases/latest");

    public static async Task<SelfUpdateResult> UpdateAsync(CancellationToken ct)
    {
        var executablePath = GetCurrentExecutablePath();
        var assetName = GetReleaseAssetName();

        using var httpClient = CreateHttpClient();
        using var release = await ReadLatestReleaseAsync(httpClient, ct);
        var releaseName = GetReleaseName(release.RootElement);
        var asset = FindAsset(release.RootElement, assetName)
                    ?? throw new InvalidOperationException(
                        $"Release '{releaseName}' does not contain asset '{assetName}'. Available assets: {ListAssetNames(release.RootElement)}."
                    );

        var stagedPath = GetStagedPath(executablePath);
        try
        {
            await DownloadAssetAsync(httpClient, asset.DownloadUrl, stagedPath, ct);
            await ApplyUnixModeAsync(executablePath, stagedPath, ct);

            if (FilesAreEqual(executablePath, stagedPath))
            {
                File.Delete(stagedPath);
                return new($"Conduit is already current ({releaseName}).");
            }

            if (OperatingSystem.IsWindows())
            {
                StartWindowsReplacement(Process.GetCurrentProcess().Id, stagedPath, executablePath);
                return new(
                    $"Downloaded {asset.Name} from {releaseName}. The executable will be replaced after this process exits."
                );
            }

            File.Move(stagedPath, executablePath, overwrite: true);
            return new($"Updated Conduit to {releaseName}.");
        }
        catch
        {
            TryDelete(stagedPath);
            throw;
        }
    }

    internal static string GetReleaseAssetName()
    {
        var architecture = RuntimeInformation.ProcessArchitecture;
        if (architecture != Architecture.X64)
            throw new PlatformNotSupportedException($"Self-update only supports x64 builds. Current architecture: {architecture}.");

        if (OperatingSystem.IsWindows())
            return "conduit-win-x64.exe";

        if (OperatingSystem.IsLinux())
            return "conduit-linux-x64";

        throw new PlatformNotSupportedException($"Self-update is not supported on {RuntimeInformation.OSDescription}.");
    }

    internal static ReleaseAsset? FindAsset(JsonElement release, string assetName)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameProperty) || nameProperty.GetString() is not { } name)
                continue;

            if (!string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!asset.TryGetProperty("browser_download_url", out var urlProperty)
                || Uri.TryCreate(urlProperty.GetString(), UriKind.Absolute, out var downloadUrl) is false)
                throw new InvalidOperationException($"Release asset '{assetName}' does not have a valid download URL.");

            return new(name, downloadUrl);
        }

        return null;
    }

    static async Task<JsonDocument> ReadLatestReleaseAsync(HttpClient httpClient, CancellationToken ct)
    {
        using var response = await httpClient.GetAsync(LatestReleaseUri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    static async Task DownloadAssetAsync(HttpClient httpClient, Uri downloadUrl, string path, CancellationToken ct)
    {
        using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, ct);
    }

    static Task ApplyUnixModeAsync(string executablePath, string stagedPath, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return Task.CompletedTask;

        ct.ThrowIfCancellationRequested();

        var mode = File.GetUnixFileMode(executablePath)
                   | UnixFileMode.UserExecute
                   | UnixFileMode.GroupExecute
                   | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(stagedPath, mode);
        return Task.CompletedTask;
    }

    static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(
                Assembly.GetExecutingAssembly().GetName().Name ?? "conduit",
                ConduitServerMetadata.GetPackageVersion()
            )
        );
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return httpClient;
    }

    static string GetCurrentExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Unable to determine the current executable path.");

        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Self-update requires running the published executable, not 'dotnet <assembly>'.");

        if (!File.Exists(path))
            throw new FileNotFoundException("Current executable path does not exist.", path);

        return path;
    }

    static string GetStagedPath(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath)
                        ?? throw new InvalidOperationException("Unable to determine the executable directory.");
        return Path.Combine(directory, $".{Path.GetFileName(executablePath)}.update-{Guid.NewGuid():N}");
    }

    static string GetReleaseName(JsonElement release)
    {
        if (release.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } releaseName)
            return releaseName;

        if (release.TryGetProperty("tag_name", out var tag) && tag.GetString() is { Length: > 0 } tagName)
            return tagName;

        return "latest release";
    }

    static string ListAssetNames(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return "none";

        var names = assets.EnumerateArray()
            .Select(static asset => asset.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        return names.Length == 0 ? "none" : string.Join(", ", names);
    }

    static bool FilesAreEqual(string left, string right)
    {
        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        return SHA256.HashData(leftStream).SequenceEqual(SHA256.HashData(rightStream));
    }

    static void StartWindowsReplacement(int processId, string stagedPath, string executablePath)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"conduit-update-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(
            scriptPath,
            """
            param(
                [int]$TargetProcessId,
                [string]$Source,
                [string]$Target,
                [string]$Script
            )

            $ErrorActionPreference = 'Stop'
            try {
                Wait-Process -Id $TargetProcessId -ErrorAction SilentlyContinue

                $targetFullPath = [System.IO.Path]::GetFullPath($Target)
                $targetName = [System.IO.Path]::GetFileName($Target)
                $targetProcesses = @(
                    Get-CimInstance Win32_Process |
                    Where-Object {
                        $_.ExecutablePath -and
                        $_.Name -ieq $targetName -and
                        [System.IO.Path]::GetFullPath($_.ExecutablePath) -ieq $targetFullPath
                    }
                )

                foreach ($process in $targetProcesses) {
                    try {
                        Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
                    }
                    catch {
                        if (Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue) {
                            throw
                        }
                    }
                }

                foreach ($process in $targetProcesses) {
                    Wait-Process -Id $process.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
                }

                Move-Item -LiteralPath $Source -Destination $Target -Force
            }
            finally {
                Remove-Item -LiteralPath $Script -Force -ErrorAction SilentlyContinue
            }
            """
        );

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-TargetProcessId");
        startInfo.ArgumentList.Add(processId.ToString());
        startInfo.ArgumentList.Add("-Source");
        startInfo.ArgumentList.Add(stagedPath);
        startInfo.ArgumentList.Add("-Target");
        startInfo.ArgumentList.Add(executablePath);
        startInfo.ArgumentList.Add("-Script");
        startInfo.ArgumentList.Add(scriptPath);

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Unable to start the Windows replacement process.");
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup after a failed update.
        }
    }
}

readonly record struct SelfUpdateResult(string Message);

readonly record struct ReleaseAsset(string Name, Uri DownloadUrl);
