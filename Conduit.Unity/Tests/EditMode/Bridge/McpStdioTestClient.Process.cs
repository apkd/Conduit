#nullable enable

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor.PackageManager;

namespace Conduit
{
    sealed partial class McpStdioTestClient
    {
        const string RepoRootEnvironmentVariable = "CONDUIT_REPO_ROOT";
        const string ServerExecutableEnvironmentVariable = "CONDUIT_SERVER_EXECUTABLE";
        static readonly string[] fallbackRepoRoots = { "/UnityConduitRepo" };

        static ProcessStartInfo CreateStartInfo(string serverProjectPath, string repoRoot)
        {
            if (ResolveConfiguredServerPath(repoRoot) is { } configuredServerPath)
                return CreateProcessStartInfo(configuredServerPath, repoRoot);

            var builtServerPath = Path.Combine(
                Path.GetDirectoryName(serverProjectPath) ?? throw new AssertionException($"Could not resolve the server directory from '{serverProjectPath}'."),
                "bin",
                "Debug",
                "net10.0",
                "conduit.dll");
            var startInfo = CreateProcessStartInfo("dotnet", repoRoot);
            if (File.Exists(builtServerPath))
                startInfo.ArgumentList.Add(builtServerPath);
            else
            {
                startInfo.ArgumentList.Add("run");
                startInfo.ArgumentList.Add("--project");
                startInfo.ArgumentList.Add(serverProjectPath);
                startInfo.ArgumentList.Add("--no-build");
                startInfo.ArgumentList.Add("--");
            }
            startInfo.Environment["DOTNET_NOLOGO"] = "1";
            startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            return startInfo;
        }

        static string? ResolveConfiguredServerPath(string repoRoot)
        {
            var configuredPath = Environment.GetEnvironmentVariable(ServerExecutableEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(configuredPath))
                return null;

            var fullPath = Path.IsPathRooted(configuredPath)
                ? Path.GetFullPath(configuredPath)
                : Path.GetFullPath(Path.Combine(repoRoot, configuredPath));
            if (File.Exists(fullPath))
                return fullPath;

            throw new AssertionException(
                $"The configured MCP server executable '{fullPath}' from {ServerExecutableEnvironmentVariable} does not exist.");
        }

        static ProcessStartInfo CreateProcessStartInfo(string fileName, string workingDirectory)
            => new(fileName)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

        static void TryKillProcess(Process? process)
        {
            if (process == null)
                return;

            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException) { }
            catch (NotSupportedException) { }
        }

        async Task PumpStandardErrorAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                lock (stderrGate)
                {
                    if (stderr.Length > 0)
                        stderr.AppendLine();

                    stderr.Append(line);
                }
            }
        }

        static string ResolveServerProjectPath()
        {
            var checkedPaths = new List<string>();
            foreach (var candidate in EnumerateServerProjectCandidates())
            {
                checkedPaths.Add(candidate);
                if (File.Exists(candidate))
                    return candidate;
            }

            throw new AssertionException(
                $"Could not locate Conduit.Server/Conduit.csproj for the repo-scoped MCP end-to-end tests.\nChecked:\n{string.Join("\n", checkedPaths)}");
        }

        static IEnumerable<string> EnumerateServerProjectCandidates()
        {
            var configuredRepoRoot = Environment.GetEnvironmentVariable(RepoRootEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configuredRepoRoot))
                yield return Path.Combine(Path.GetFullPath(configuredRepoRoot), "Conduit.Server", "Conduit.csproj");

            var packageRoot = ResolvePackageRootPath();
            if (string.Equals(Path.GetFileName(packageRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    "Conduit.Unity",
                    StringComparison.OrdinalIgnoreCase))
            {
                var repoRoot = Path.GetDirectoryName(packageRoot);
                if (!string.IsNullOrWhiteSpace(repoRoot))
                    yield return Path.Combine(repoRoot, "Conduit.Server", "Conduit.csproj");
            }

            foreach (var repoRoot in fallbackRepoRoots)
                yield return Path.Combine(repoRoot, "Conduit.Server", "Conduit.csproj");
        }

        static string ResolvePackageRootPath()
        {
            var packageInfo = PackageInfo.FindForAssembly(typeof(ConduitProjectIdentity).Assembly);
            if (packageInfo == null || string.IsNullOrWhiteSpace(packageInfo.resolvedPath))
                throw new AssertionException("Could not resolve the Conduit package root from the Unity editor.");

            return Path.GetFullPath(packageInfo.resolvedPath);
        }
    }
}
#endif
