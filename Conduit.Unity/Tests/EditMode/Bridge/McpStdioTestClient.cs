#nullable enable

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace Conduit
{
    sealed partial class McpStdioTestClient : IDisposable
    {
        const string SupportedProtocolVersion = "2025-03-26";
        // cold Roslyn compilation and asset import requests routinely exceed short RPC-style timeouts
        static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(2);
        static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);

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

    }
}
#endif
