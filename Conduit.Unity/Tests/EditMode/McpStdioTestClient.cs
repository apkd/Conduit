#nullable enable

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Conduit
{
    sealed class McpStdioTestClient : IDisposable
    {
        const string SupportedProtocolVersion = "2025-03-26";
        const string ServerExecutableEnvironmentVariable = "CONDUIT_SERVER_EXECUTABLE";
        static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(20);
        static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);
        static readonly Regex responseIdRegex = new("\"id\":(?<id>\\d+)", RegexOptions.Compiled);
        static readonly Regex jsonStringPropertyRegex = new("\"(?<name>[^\"]+)\":\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.Compiled);
        static readonly Regex toolNameRegex = new("\"name\":\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.Compiled);
        static readonly Regex textContentRegex = new("\"type\":\"text\"\\s*,\\s*\"text\":\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.Compiled);
        static readonly Regex nestedServerNameRegex = new("\"serverInfo\":\\{[^}]*\"name\":\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.Compiled);
        static readonly Regex errorMessageRegex = new("\"error\":\\{[^}]*\"message\":\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.Compiled);
        static readonly Regex isErrorRegex = new("\"isError\":(?<value>true|false)", RegexOptions.Compiled);
        static readonly string[] fallbackRepoRoots = { "/UnityConduitRepo" };

        readonly Process process;
        readonly object stderrGate = new();
        readonly StringBuilder stderr = new();
        readonly StringBuilder protocolNoise = new();
        Task stderrPump;
        readonly SemaphoreSlim ioGate = new(1, 1);
        int nextRequestId;
        bool disposed;

        McpStdioTestClient(Process process, Task stderrPump)
        {
            this.process = process;
            this.stderrPump = stderrPump;
        }

        public string NegotiatedProtocolVersion { get; private set; } = string.Empty;

        public string ServerName { get; private set; } = string.Empty;

        public static async Task<McpStdioTestClient> StartAsync(TimeSpan startupTimeout)
        {
            var serverProjectPath = ResolveServerProjectPath();
            var repoRoot = Path.GetDirectoryName(Path.GetDirectoryName(serverProjectPath))
                           ?? throw new AssertionException($"Could not resolve the repository root for '{serverProjectPath}'.");

            Process? process = null;
            McpStdioTestClient? client = null;
            try
            {
                process = new()
                {
                    StartInfo = CreateStartInfo(serverProjectPath, repoRoot),
                    EnableRaisingEvents = true,
                };
                if (!process.Start())
                    throw new AssertionException($"Failed to start the MCP server process from '{process.StartInfo.FileName}'.");

                client = new(process, Task.CompletedTask);
                client.stderrPump = client.PumpStandardErrorAsync(process.StandardError);
                await client.InitializeAsync(startupTimeout).ConfigureAwait(false);
                return client;
            }
            catch
            {
                client?.Dispose();
                TryKillProcess(process);
                process?.Dispose();
                throw;
            }
        }

        public async Task<string[]> ListToolsAsync(TimeSpan? timeout = null)
        {
            var response = await SendRequestAsync(
                method: "tools/list",
                parameters: new Dictionary<string, object?>(),
                timeout ?? DefaultRequestTimeout).ConfigureAwait(false);

            if (TryGetErrorMessage(response, out var errorMessage))
                throw BuildAssertionException($"tools/list returned a JSON-RPC error: {errorMessage}");

            var matches = toolNameRegex.Matches(response);
            if (matches.Count == 0)
                throw BuildAssertionException("tools/list returned no tools.");

            var names = new string[matches.Count];
            for (var index = 0; index < matches.Count; index++)
                names[index] = UnescapeJson(matches[index].Groups["value"].Value);

            return names;
        }

        public async Task<McpToolCallResult> CallToolAsync(string name, IReadOnlyDictionary<string, object?> arguments, TimeSpan? timeout = null)
        {
            var response = await SendRequestAsync(
                method: "tools/call",
                parameters: new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["arguments"] = arguments,
                },
                timeout ?? DefaultRequestTimeout).ConfigureAwait(false);

            if (TryGetErrorMessage(response, out var errorMessage))
                return new(true, errorMessage);

            var builder = new StringBuilder();
            var matches = textContentRegex.Matches(response);
            for (var index = 0; index < matches.Count; index++)
            {
                if (builder.Length > 0)
                    builder.Append("\n\n");

                builder.Append(UnescapeJson(matches[index].Groups["value"].Value));
            }

            var isError = false;
            var isErrorMatch = isErrorRegex.Match(response);
            if (isErrorMatch.Success)
                isError = isErrorMatch.Groups["value"].Value == "true";

            return new(isError, builder.ToString());
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            ioGate.Dispose();

            try
            {
                process.StandardInput.Close();
            }
            catch (Exception) { }

            try
            {
                if (!process.WaitForExit((int)DefaultShutdownTimeout.TotalMilliseconds))
                    TryKillProcess(process);
            }
            catch (Exception) { }

            try
            {
                stderrPump.Wait(DefaultShutdownTimeout);
            }
            catch (Exception) { }

            process.Dispose();
        }

        async Task InitializeAsync(TimeSpan timeout)
        {
            var initializeResponse = await SendRequestAsync(
                method: "initialize",
                parameters: new Dictionary<string, object?>
                {
                    ["protocolVersion"] = SupportedProtocolVersion,
                    ["capabilities"] = new Dictionary<string, object?>(),
                    ["clientInfo"] = new Dictionary<string, object?>
                    {
                        ["name"] = "unity-editmode-tests",
                        ["version"] = Application.unityVersion,
                    },
                },
                timeout).ConfigureAwait(false);

            if (TryGetErrorMessage(initializeResponse, out var errorMessage))
                throw BuildAssertionException($"initialize returned a JSON-RPC error: {errorMessage}");

            if (!TryGetStringProperty(initializeResponse, "protocolVersion", out var protocolVersion)
                || string.IsNullOrWhiteSpace(protocolVersion))
                throw BuildAssertionException("initialize returned no negotiated protocol version.");

            NegotiatedProtocolVersion = protocolVersion;
            ServerName = TryGetServerName(initializeResponse, out var serverName) ? serverName : string.Empty;

            await SendNotificationAsync(
                "notifications/initialized",
                new Dictionary<string, object?>(),
                timeout).ConfigureAwait(false);
        }

        async Task<string> SendRequestAsync(string method, IReadOnlyDictionary<string, object?> parameters, TimeSpan timeout)
        {
            await ioGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var requestId = Interlocked.Increment(ref nextRequestId);
                var payload = SerializeEnvelope(method, requestId, parameters);
                await process.StandardInput.WriteLineAsync(payload).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);

                return await ReadResponseAsync(requestId, timeout).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not AssertionException)
            {
                throw BuildAssertionException($"Failed to send MCP request '{method}'.", exception);
            }
            finally
            {
                ioGate.Release();
            }
        }

        async Task SendNotificationAsync(string method, IReadOnlyDictionary<string, object?> parameters, TimeSpan timeout)
        {
            await ioGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var payload = SerializeEnvelope(method, null, parameters);
                await process.StandardInput.WriteLineAsync(payload).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not AssertionException)
            {
                throw BuildAssertionException($"Failed to send MCP notification '{method}'.", exception);
            }
            finally
            {
                ioGate.Release();
            }
        }

        async Task<string> ReadResponseAsync(int expectedId, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    throw BuildAssertionException($"Timed out while waiting for MCP response '{expectedId}'.");

                var line = await ReadStandardOutputLineAsync(remaining).ConfigureAwait(false);
                if (line == null)
                    throw BuildAssertionException($"The MCP server exited before responding to request '{expectedId}'.");

                if (!TryGetResponseId(line, out var responseId))
                {
                    RecordProtocolNoise(line);
                    continue;
                }

                if (responseId != expectedId)
                {
                    RecordProtocolNoise(line);
                    continue;
                }

                return line;
            }
        }

        async Task<string?> ReadStandardOutputLineAsync(TimeSpan timeout)
        {
            var readTask = process.StandardOutput.ReadLineAsync();
            var completed = await Task.WhenAny(readTask, Task.Delay(timeout)).ConfigureAwait(false);
            if (!ReferenceEquals(completed, readTask))
                throw BuildAssertionException($"Timed out after {timeout} while waiting for the MCP server to emit a response line.");

            return await readTask.ConfigureAwait(false);
        }

        void RecordProtocolNoise(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            lock (stderrGate)
            {
                if (protocolNoise.Length > 0)
                    protocolNoise.AppendLine();

                protocolNoise.Append(line);
            }
        }

        AssertionException BuildAssertionException(string message, Exception? innerException = null)
        {
            var builder = new StringBuilder(message);
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine($"Server process: {process.StartInfo.FileName} {string.Join(" ", process.StartInfo.ArgumentList)}");
            try
            {
                if (process.HasExited)
                    builder.AppendLine($"Exit code: {process.ExitCode}");
            }
            catch (InvalidOperationException) { }

            if (innerException != null)
            {
                builder.AppendLine();
                builder.AppendLine("INNER EXCEPTION:");
                builder.AppendLine(innerException.ToString());
            }

            lock (stderrGate)
            {
                if (stderr.Length > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine("STDERR:");
                    builder.Append(stderr);
                }

                if (protocolNoise.Length > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine();
                    builder.AppendLine("STDOUT NOISE:");
                    builder.Append(protocolNoise);
                }

            }

            return new AssertionException(builder.ToString());
        }

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

        static bool TryGetResponseId(string line, out int responseId)
        {
            responseId = 0;
            var match = responseIdRegex.Match(line);
            if (!match.Success || !int.TryParse(match.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out responseId))
                return false;

            return responseId > 0;
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

        static string SerializeEnvelope(string method, int? id, IReadOnlyDictionary<string, object?> parameters)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            AppendJsonProperty(builder, "jsonrpc", "2.0", first: true);
            if (id.HasValue)
                AppendJsonProperty(builder, "id", id.Value, first: false);

            AppendJsonProperty(builder, "method", method, first: false);
            AppendJsonProperty(builder, "params", parameters, first: false);
            builder.Append('}');
            return builder.ToString();
        }

        static void AppendJsonProperty(StringBuilder builder, string name, object? value, bool first)
        {
            if (!first)
                builder.Append(',');

            builder.Append('"');
            builder.Append(EscapeJson(name));
            builder.Append("\":");
            AppendJsonValue(builder, value);
        }

        static void AppendJsonValue(StringBuilder builder, object? value)
        {
            switch (value)
            {
                case null:
                    builder.Append("null");
                    return;
                case string text:
                    builder.Append('"');
                    builder.Append(EscapeJson(text));
                    builder.Append('"');
                    return;
                case bool boolean:
                    builder.Append(boolean ? "true" : "false");
                    return;
                case int number:
                    builder.Append(number.ToString(CultureInfo.InvariantCulture));
                    return;
                case long number:
                    builder.Append(number.ToString(CultureInfo.InvariantCulture));
                    return;
                case float number:
                    builder.Append(number.ToString(CultureInfo.InvariantCulture));
                    return;
                case double number:
                    builder.Append(number.ToString(CultureInfo.InvariantCulture));
                    return;
                case IReadOnlyDictionary<string, object?> dictionary:
                    AppendJsonObject(builder, dictionary);
                    return;
                case IDictionary<string, object?> dictionary:
                    AppendJsonObject(builder, dictionary);
                    return;
                case string[] strings:
                    AppendJsonArray(builder, strings);
                    return;
                case IEnumerable<string> strings:
                    AppendJsonArray(builder, strings);
                    return;
                default:
                    throw new NotSupportedException($"Unsupported MCP JSON value type '{value.GetType().FullName}'.");
            }
        }

        static void AppendJsonObject(StringBuilder builder, IEnumerable<KeyValuePair<string, object?>> values)
        {
            builder.Append('{');
            var first = true;
            foreach (var (key, value) in values)
            {
                AppendJsonProperty(builder, key, value, first);
                first = false;
            }

            builder.Append('}');
        }

        static void AppendJsonArray(StringBuilder builder, IEnumerable<string> values)
        {
            builder.Append('[');
            var first = true;
            foreach (var value in values)
            {
                if (!first)
                    builder.Append(',');

                builder.Append('"');
                builder.Append(EscapeJson(value));
                builder.Append('"');
                first = false;
            }

            builder.Append(']');
        }

        static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            var builder = new StringBuilder(value.Length + 8);
            foreach (var character in value)
            {
                switch (character)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (character < ' ')
                            builder.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)character);
                        else
                            builder.Append(character);
                        break;
                }
            }

            return builder.ToString();
        }

        static bool TryGetErrorMessage(string response, out string message)
        {
            var match = errorMessageRegex.Match(response);
            if (!match.Success)
            {
                message = string.Empty;
                return false;
            }

            message = UnescapeJson(match.Groups["value"].Value);
            return true;
        }

        static bool TryGetStringProperty(string response, string propertyName, out string value)
        {
            foreach (Match match in jsonStringPropertyRegex.Matches(response))
            {
                if (match.Groups["name"].Value != propertyName)
                    continue;

                value = UnescapeJson(match.Groups["value"].Value);
                return true;
            }

            value = string.Empty;
            return false;
        }

        static bool TryGetServerName(string response, out string serverName)
        {
            var match = nestedServerNameRegex.Match(response);
            if (!match.Success)
            {
                serverName = string.Empty;
                return false;
            }

            serverName = UnescapeJson(match.Groups["value"].Value);
            return true;
        }

        static string UnescapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            var builder = new StringBuilder(value.Length);
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character != '\\' || index == value.Length - 1)
                {
                    builder.Append(character);
                    continue;
                }

                var escaped = value[++index];
                switch (escaped)
                {
                    case '"':
                        builder.Append('"');
                        break;
                    case '\\':
                        builder.Append('\\');
                        break;
                    case '/':
                        builder.Append('/');
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'u':
                        if (index + 4 >= value.Length)
                            throw new FormatException("Invalid JSON unicode escape sequence.");

                        var codePoint = value.Substring(index + 1, 4);
                        builder.Append((char)int.Parse(codePoint, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        index += 4;
                        break;
                    default:
                        builder.Append(escaped);
                        break;
                }
            }

            return builder.ToString();
        }
    }

    readonly struct McpToolCallResult
    {
        public McpToolCallResult(bool isError, string text)
        {
            IsError = isError;
            Text = text;
        }

        public bool IsError { get; }

        public string Text { get; }
    }
}
#endif
