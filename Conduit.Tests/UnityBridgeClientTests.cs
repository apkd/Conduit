using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Conduit;

public sealed class UnityBridgeClientTests
{
    [Test]
    public async Task BridgeCommandSerializesToolUsageIntent()
    {
        var payload = BridgeProtocol.Serialize(
            BridgeMessage.CreateCommand(
                "usage-test",
                new()
                {
                    CommandType = BridgeCommandTypes.Show,
                    TrackUsage = true,
                }
            )
        );
        var command = JsonNode.Parse(payload)?["command"];

        await Assert.That(command?["track_usage"]?.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task ProbeTimeoutWhileWaitingForTheProjectGateReturnsATimeoutResult()
    {
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var projectPath = $"/tmp/conduit-probe-timeout-{Guid.NewGuid():N}";

        var firstProbe = client.ProbeAsync(
            projectPath,
            processIdHint: null,
            timeout: TimeSpan.FromMilliseconds(900),
            CancellationToken.None
        );

        await Task.Delay(50);

        var secondProbe = await client.ProbeAsync(
            projectPath,
            processIdHint: null,
            timeout: TimeSpan.FromMilliseconds(50),
            CancellationToken.None
        );

        await Assert.That(secondProbe.FailureKind).IsEqualTo(BridgeRuntimeFailureKind.ConnectTimedOut);
        await Assert.That(secondProbe.FailureDiagnostic).Contains("Could not establish a Unity connection");
        await Assert.That(secondProbe.Result).IsNull();

        await firstProbe;
    }

    [Test]
    public async Task ProbeTreatsProcessIdHintAsAHintNotAFatalLivenessCheck()
    {
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var projectPath = $"/tmp/conduit-stale-pid-{Guid.NewGuid():N}";

        var result = await client.ProbeAsync(
            projectPath,
            processIdHint: int.MaxValue,
            timeout: TimeSpan.FromMilliseconds(50),
            CancellationToken.None
        );

        await Assert.That(result.FailureKind).IsEqualTo(BridgeRuntimeFailureKind.ConnectTimedOut);
        await Assert.That(result.FailureDiagnostic).Contains("Could not establish a Unity connection");
        await Assert.That(result.FailureDiagnostic).DoesNotContain("exited");
    }

    [Test]
    public async Task BridgeTransportConnectsToDotNetNamedPipeServer()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var pipeName = $"unity-conduit-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous
        );

        var waitForConnection = server.WaitForConnectionAsync();
        await using var transport = await UnityBridgeClient.BridgeTransport.ConnectAsync(
            pipeName,
            TimeSpan.FromSeconds(2),
            CancellationToken.None
        );

        await waitForConnection.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.That(server.IsConnected).IsTrue();
        await Assert.That(transport.IsConnected).IsTrue();

        await transport.DisposeAsync();
        await Assert.That(transport.IsConnected).IsFalse();
    }

    [Test]
    public async Task FifoTransportReadHonorsCancellation()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-fifo-cancellation-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(projectPath, int.MaxValue);
        await using var transport = await UnityBridgeClient.BridgeTransport.ConnectAsync(
            ConduitUtility.GetPipeName(projectPath),
            TimeSpan.FromSeconds(2),
            CancellationToken.None
        );
        await transport.WritePayloadAsync(
            BridgeProtocol.Serialize(
                BridgeMessage.CreateHello(new() { ProjectPath = projectPath })
            ),
            CancellationToken.None
        );
        await Assert.That(await transport.ReadLineAsync(CancellationToken.None)).IsNotNull();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var cancelled = false;
        try
        {
            await transport.ReadLineAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            cancelled = true;
        }

        await Assert.That(cancelled).IsTrue();
    }

    [Test]
    public async Task ExecuteCommandIgnoresHandshakeProcessIdThatIsNotVisible()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-invisible-pid-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(projectPath, int.MaxValue);
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);

        var result = await client.ExecuteCommandAsync(
            projectPath,
            ConduitUtility.CreateRequestId(),
            new() { CommandType = BridgeCommandTypes.Status },
            TimeSpan.FromSeconds(10),
            processIdHint: null,
            CancellationToken.None
        );

        await Assert.That(result.FailureKind).IsNull();
        await Assert.That(result.Result?.Outcome).IsEqualTo(ToolOutcome.Success);
    }

    [Test]
    public async Task ExecuteCommandReadsCoalescedUnixSocketResponses()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-coalesced-response-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(
            projectPath,
            int.MaxValue,
            coalesceCommandResponses: true
        );
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);

        var result = await client.ExecuteCommandAsync(
            projectPath,
            ConduitUtility.CreateRequestId(),
            new() { CommandType = BridgeCommandTypes.Status },
            TimeSpan.FromSeconds(10),
            processIdHint: null,
            CancellationToken.None
        );

        await Assert.That(result.FailureKind).IsNull();
        await Assert.That(result.Result?.Outcome).IsEqualTo(ToolOutcome.Success);
    }

    [Test]
    public async Task IdleRemoteCloseInvalidatesTheCachedHandshake()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-idle-close-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(projectPath, int.MaxValue);
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var result = await client.ExecuteCommandAsync(
            projectPath,
            ConduitUtility.CreateRequestId(),
            new() { CommandType = BridgeCommandTypes.Status },
            TimeSpan.FromSeconds(10),
            processIdHint: null,
            CancellationToken.None
        );

        await Assert.That(result.Result?.Outcome).IsEqualTo(ToolOutcome.Success);
        for (var attempt = 0; attempt < 100
             && client.TryGetLiveHandshake(projectPath, out _); attempt++)
            await Task.Delay(10);

        await Assert.That(client.TryGetLiveHandshake(projectPath, out _)).IsFalse();
    }

    [Test]
    public async Task StatusCompletesWhileAnotherCommandIsStillRunning()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-multiplex-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(
            projectPath,
            int.MaxValue,
            multiplexCommands: true
        );
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var longCommand = client.ExecuteCommandAsync(
            projectPath,
            "long-command",
            new() { CommandType = BridgeCommandTypes.RunTestsEditMode },
            TimeSpan.FromSeconds(10),
            processIdHint: null,
            CancellationToken.None
        );

        await bridge.CommandStarted.WaitAsync(TimeSpan.FromSeconds(10));
        var status = client.ExecuteCommandAsync(
            projectPath,
            "concurrent-status",
            new() { CommandType = BridgeCommandTypes.Status },
            TimeSpan.FromSeconds(10),
            processIdHint: null,
            CancellationToken.None
        );

        await bridge.ConcurrentCommandCompleted.WaitAsync(TimeSpan.FromSeconds(10));
        var statusResult = await status.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(statusResult.Result?.Outcome).IsEqualTo(ToolOutcome.Success);
        await Assert.That(longCommand.IsCompleted).IsFalse();

        bridge.ReleaseFirstCommand();
        await Assert.That((await longCommand).Result?.Outcome).IsEqualTo(ToolOutcome.Success);
        await Assert.That(bridge.ConnectionCount).IsEqualTo(1);
    }

    [Test]
    public async Task IdempotentCommandReconnectsOnceAfterDisconnect()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-idempotent-retry-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(
            projectPath,
            int.MaxValue,
            disconnectFirstCommand: true
        );
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var result = await client.ExecuteIdempotentCommandAsync(
            projectPath,
            "idempotent-read",
            new() { CommandType = BridgeCommandTypes.CompilationReferences },
            TimeSpan.FromSeconds(10),
            CancellationToken.None
        );

        await Assert.That(result.Result?.Outcome).IsEqualTo(ToolOutcome.Success);
        await Assert.That(bridge.ConnectionCount).IsEqualTo(2);
    }

    [Test]
    public async Task CompilationReferencesAreSingleFlightWithinAUnitySession()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-reference-cache-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(projectPath, int.MaxValue);
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var compiler = new SnippetCompiler(client);

        var first = compiler.GetReferencePathsAsync(projectPath, CancellationToken.None);
        var second = compiler.GetReferencePathsAsync(projectPath, CancellationToken.None);
        var results = await Task.WhenAll(first, second);

        await Assert.That(results.All(static result => result.Failure is null)).IsTrue();
        await Assert.That(bridge.CommandCount).IsEqualTo(1);
    }

    [Test]
    public async Task NewUnitySessionRefetchesCompilationReferences()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-reference-session-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(
            projectPath,
            int.MaxValue,
            changeSessionOnReconnect: true
        );
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var compiler = new SnippetCompiler(client);

        await Assert.That(
            (await compiler.GetReferencePathsAsync(projectPath, CancellationToken.None)).Failure
        ).IsNull();
        for (var attempt = 0; attempt < 100
             && client.TryGetLiveHandshake(projectPath, out _); attempt++)
            await Task.Delay(10);

        await Assert.That(
            (await compiler.GetReferencePathsAsync(projectPath, CancellationToken.None)).Failure
        ).IsNull();
        await Assert.That(bridge.CommandCount).IsEqualTo(2);
    }

    [Test]
    public async Task CachedCompilationReferencesRefreshHandshakeMetadata()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-reference-handshake-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(
            projectPath,
            int.MaxValue,
            preserveSnippetsAfterReconnect: true
        );
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var compiler = new SnippetCompiler(client);

        var first = await compiler.GetReferencePathsAsync(projectPath, CancellationToken.None);
        await Assert.That(first.Failure).IsNull();
        await Assert.That(first.PreserveSnippets).IsFalse();
        for (var attempt = 0; attempt < 100
             && client.TryGetLiveHandshake(projectPath, out _); attempt++)
            await Task.Delay(10);

        var second = await compiler.GetReferencePathsAsync(projectPath, CancellationToken.None);
        await Assert.That(second.Failure).IsNull();
        await Assert.That(second.PreserveSnippets).IsTrue();
        await Assert.That(bridge.CommandCount).IsEqualTo(2);
    }

    [Test]
    public async Task OlderUnityProtocolReturnsATerminalCompatibilityDiagnostic() =>
        await AssertProtocolMismatchAsync(
            BridgeProtocol.Version - 1,
            $"Unity Editor bridge protocol {BridgeProtocol.Version - 1} is older than Conduit server protocol {BridgeProtocol.Version}."
        );

    [Test]
    public async Task NewerUnityProtocolReturnsATerminalCompatibilityDiagnostic() =>
        await AssertProtocolMismatchAsync(
            BridgeProtocol.Version + 1,
            $"Conduit server protocol {BridgeProtocol.Version} is older than Unity Editor bridge protocol {BridgeProtocol.Version + 1}."
        );

    [Test]
    public async Task CancellingATestRequestSendsCancellationAndWaitsForUnityToFinish()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-cancel-test-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(
            projectPath,
            int.MaxValue,
            waitForCancellation: true
        );
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        using var cancellation = new CancellationTokenSource();

        var execution = client.ExecuteCommandAsync(
            projectPath,
            "cancel-test-request",
            new() { CommandType = BridgeCommandTypes.RunTestsEditMode },
            TimeSpan.FromSeconds(10),
            processIdHint: null,
            CancellationToken.None,
            cancellation.Token
        );

        await bridge.CommandStarted.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(15));

        await Assert.That(
            await bridge.CancelledRequestId.WaitAsync(TimeSpan.FromSeconds(15))
        ).IsEqualTo("cancel-test-request");
        await Assert.That(result.FailureKind).IsNull();
        await Assert.That(result.Result?.Outcome).IsEqualTo(ToolOutcome.Cancelled);
    }

    [Test]
    public async Task ReceivePumpRoutesCommandStartedWhenTransportLivenessProbeIsFalse()
    {
        var requestId = ConduitUtility.CreateRequestId();
        var payload = BridgeProtocol.Serialize(BridgeMessage.CreateCommandStarted(requestId));
        var read = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCount = 0;
        await using var transport = new UnityBridgeClient.BridgeTransport(
            async ct =>
            {
                if (Interlocked.Increment(ref readCount) == 1)
                    return await read.Task;

                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return null;
            },
            static (_, _) => Task.CompletedTask,
            static () => false,
            static () => ValueTask.CompletedTask
        );
        await using var connection = new UnityBridgeClient.BridgeClientConnection(
            transport,
            new()
            {
                ProjectPath = "/tmp/conduit-readable-disconnected",
                EditorProcessId = 0,
                SessionInstanceId = "test",
            },
            NullLogger<UnityBridgeClient>.Instance
        );
        var pending = connection.RegisterRequest(requestId, BridgeCommandTypes.Status);
        pending.MarkSent();
        read.SetResult(payload);

        var outcome = await connection.WaitForCommandStartedAsync(
            pending,
            TimeSpan.FromSeconds(1),
            CancellationToken.None,
            CancellationToken.None
        );

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(outcome.FinalResult).IsNull();
    }

    static async Task AssertProtocolMismatchAsync(int unityProtocolVersion, string expectedDiagnostic)
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-protocol-mismatch-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(
            projectPath,
            int.MaxValue,
            handshakeProtocolVersion: unityProtocolVersion
        );
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);

        var result = await client.ProbeAsync(
            projectPath,
            processIdHint: null,
            timeout: TimeSpan.FromSeconds(10),
            ct: CancellationToken.None
        );

        await Assert.That(result.FailureKind).IsEqualTo(BridgeRuntimeFailureKind.ProtocolMismatch);
        await Assert.That(result.FailureDiagnostic).IsEqualTo(expectedDiagnostic);
        await Assert.That(result.Handshake).IsNull();
        await Assert.That(result.CommandSent).IsFalse();
        await Assert.That(bridge.ConnectionCount).IsEqualTo(1);
    }

    sealed class FakeFifoBridge : IAsyncDisposable
    {
        static readonly UTF8Encoding Utf8NoBom = new(false);
        static readonly byte[] Newline = [(byte)'\n'];
        readonly CancellationTokenSource cts = new();
        readonly string projectPath;
        readonly int editorProcessId;
        readonly int handshakeProtocolVersion;
        readonly bool coalesceCommandResponses;
        readonly bool waitForCancellation;
        readonly bool multiplexCommands;
        readonly bool disconnectFirstCommand;
        readonly bool changeSessionOnReconnect;
        readonly bool preserveSnippetsAfterReconnect;
        readonly string endpointDirectory;
        readonly Task serverTask;
        readonly TaskCompletionSource commandStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource<string?> cancelledRequestId = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource concurrentCommandCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource releaseFirstCommand = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int connectionCount;
        int commandCount;

        FakeFifoBridge(
            string projectPath,
            int editorProcessId,
            int handshakeProtocolVersion,
            bool coalesceCommandResponses,
            bool waitForCancellation,
            bool multiplexCommands,
            bool disconnectFirstCommand,
            bool changeSessionOnReconnect,
            bool preserveSnippetsAfterReconnect,
            string endpointDirectory)
        {
            this.projectPath = projectPath;
            this.editorProcessId = editorProcessId;
            this.handshakeProtocolVersion = handshakeProtocolVersion;
            this.coalesceCommandResponses = coalesceCommandResponses;
            this.waitForCancellation = waitForCancellation;
            this.multiplexCommands = multiplexCommands;
            this.disconnectFirstCommand = disconnectFirstCommand;
            this.changeSessionOnReconnect = changeSessionOnReconnect;
            this.preserveSnippetsAfterReconnect = preserveSnippetsAfterReconnect;
            this.endpointDirectory = endpointDirectory;
            serverTask = Task.Run(RunAsync);
        }

        public Task CommandStarted => commandStarted.Task;

        public Task<string?> CancelledRequestId => cancelledRequestId.Task;

        public Task ConcurrentCommandCompleted => concurrentCommandCompleted.Task;

        public int ConnectionCount => Volatile.Read(ref connectionCount);
        public int CommandCount => Volatile.Read(ref commandCount);

        public void ReleaseFirstCommand() => releaseFirstCommand.TrySetResult();

        public static Task<FakeFifoBridge> StartAsync(
            string projectPath,
            int editorProcessId,
            bool coalesceCommandResponses = false,
            bool waitForCancellation = false,
            bool multiplexCommands = false,
            bool disconnectFirstCommand = false,
            bool changeSessionOnReconnect = false,
            bool preserveSnippetsAfterReconnect = false,
            int handshakeProtocolVersion = BridgeProtocol.Version)
        {
            var endpointDirectory = ConduitIpcPaths.GetEndpointDirectory(
                ConduitIpcPaths.GetDiscoveryRoots()[0],
                "editor-" + ConduitUtility.GetPipeName(projectPath)
            );
            try
            {
                Directory.Delete(endpointDirectory, recursive: true);
            }
            catch { }

            Directory.CreateDirectory(Path.Combine(endpointDirectory, "clients"));
            return Task.FromResult(
                new FakeFifoBridge(
                    projectPath,
                    editorProcessId,
                    handshakeProtocolVersion,
                    coalesceCommandResponses,
                    waitForCancellation,
                    multiplexCommands,
                    disconnectFirstCommand,
                    changeSessionOnReconnect,
                    preserveSnippetsAfterReconnect,
                    endpointDirectory
                )
            );
        }

        async Task RunAsync()
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    foreach (var clientDirectory in Directory.GetDirectories(
                                 Path.Combine(endpointDirectory, "clients")
                             ))
                    {
                        var publicationPath = Path.Combine(clientDirectory, "request.json");
                        if (!File.Exists(publicationPath))
                            continue;

                        try
                        {
                            File.Move(
                                publicationPath,
                                Path.Combine(clientDirectory, "accepted.json")
                            );
                        }
                        catch (Exception exception) when (
                            exception is IOException or UnauthorizedAccessException)
                        {
                            continue;
                        }

                        await HandleClientAsync(clientDirectory);
                    }

                    await Task.Delay(10, cts.Token);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        }

        async Task HandleClientAsync(string clientDirectory)
        {
            try
            {
                var connectionNumber = Interlocked.Increment(ref connectionCount);
                await using var input = new FileStream(
                    Path.Combine(clientDirectory, "to-unity.fifo"),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    4096,
                    FileOptions.Asynchronous
                );
                await using var output = new FileStream(
                    Path.Combine(clientDirectory, "from-unity.fifo"),
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    4096,
                    FileOptions.Asynchronous
                );
                await File.WriteAllTextAsync(
                    Path.Combine(clientDirectory, "connected"),
                    string.Empty,
                    cts.Token
                );
                using var reader = new StreamReader(
                    input,
                    Utf8NoBom,
                    detectEncodingFromByteOrderMarks: false,
                    leaveOpen: true
                );

                if (await reader.ReadLineAsync(cts.Token) is null)
                    return;

                await WritePayloadAsync(output, new JsonObject
                {
                    ["protocol_version"] = handshakeProtocolVersion,
                    ["message_type"] = "hello",
                    ["project"] = new JsonObject
                    {
                        ["project_path"] = projectPath,
                        ["display_name"] = Path.GetFileName(projectPath),
                        ["unity_version"] = "6000.3.10f1",
                        ["editor_process_id"] = editorProcessId,
                        ["session_instance_id"] = changeSessionOnReconnect
                            ? "fake-bridge-" + connectionNumber
                            : "fake-bridge",
                        ["preserve_snippets"] = preserveSnippetsAfterReconnect
                            && connectionNumber > 1,
                        ["last_seen_utc"] = DateTimeOffset.UtcNow,
                    },
                }, cts.Token);

                if (handshakeProtocolVersion != BridgeProtocol.Version)
                    return;

                var commandPayload = await reader.ReadLineAsync(cts.Token);
                if (commandPayload is null)
                    return;

                var currentCommand = Interlocked.Increment(ref commandCount);
                if (disconnectFirstCommand && currentCommand == 1)
                    return;

                var commandJson = JsonNode.Parse(commandPayload);
                var requestId = commandJson?["request_id"]?.GetValue<string>() ?? "";
                var commandType = commandJson?["command"]?["command_type"]?.GetValue<string>();
                var commandStarted = new JsonObject
                {
                    ["protocol_version"] = BridgeProtocol.Version,
                    ["message_type"] = "command_started",
                    ["request_id"] = requestId,
                };

                var commandResult = new JsonObject
                {
                    ["protocol_version"] = BridgeProtocol.Version,
                    ["message_type"] = "command_result",
                    ["request_id"] = requestId,
                    ["result"] = new JsonObject
                    {
                        ["outcome"] = ToolOutcome.Success,
                        ["logs"] = "",
                        ["return_value"] = "",
                    },
                };
                if (commandType == BridgeCommandTypes.CompilationReferences)
                {
                    commandResult["result"]!["return_value"] = JsonSerializer.Serialize(
                        new BridgeAssemblyReferenceManifest
                        {
                            References =
                            [
                                new()
                                {
                                    Id = Guid.NewGuid().ToString("N"),
                                    AssemblyName = typeof(object).Assembly.FullName ?? "System.Private.CoreLib",
                                    Path = typeof(object).Assembly.Location,
                                    Length = new FileInfo(typeof(object).Assembly.Location).Length,
                                },
                            ],
                        },
                        ConduitJsonContext.Default.BridgeAssemblyReferenceManifest
                    );
                }

                if (multiplexCommands)
                {
                    await WritePayloadAsync(output, commandStarted, cts.Token);
                    this.commandStarted.TrySetResult();
                    var concurrentPayload = await reader.ReadLineAsync(cts.Token);
                    if (concurrentPayload is null)
                        return;

                    var concurrentRequestId = JsonNode.Parse(concurrentPayload)?["request_id"]?.GetValue<string>() ?? "";
                    commandStarted["request_id"] = concurrentRequestId;
                    commandResult["request_id"] = concurrentRequestId;
                    await WritePayloadsAsync(output, cts.Token, commandStarted, commandResult);
                    concurrentCommandCompleted.TrySetResult();
                    await releaseFirstCommand.Task.WaitAsync(cts.Token);
                    commandResult["request_id"] = requestId;
                    await WritePayloadAsync(output, commandResult, cts.Token);
                    return;
                }

                if (waitForCancellation)
                {
                    await WritePayloadAsync(output, commandStarted, cts.Token);
                    this.commandStarted.TrySetResult();
                    var cancellationPayload = await reader.ReadLineAsync(cts.Token);
                    if (cancellationPayload is null)
                        return;

                    var cancellation = JsonNode.Parse(cancellationPayload);
                    cancelledRequestId.TrySetResult(
                        cancellation?["message_type"]?.GetValue<string>() == "cancel_command"
                            ? cancellation["request_id"]?.GetValue<string>()
                            : null
                    );
                    commandResult["result"]!["outcome"] = ToolOutcome.Cancelled;
                    await WritePayloadAsync(output, commandResult, cts.Token);
                }
                else if (coalesceCommandResponses)
                    await WritePayloadsAsync(output, cts.Token, commandStarted, commandResult);
                else
                {
                    await WritePayloadAsync(output, commandStarted, cts.Token);
                    await WritePayloadAsync(output, commandResult, cts.Token);
                }
            }
            catch (Exception exception) when (!cts.IsCancellationRequested)
            {
                Console.Error.WriteLine($"Fake FIFO bridge client failed: {exception}");
            }
        }

        static async Task WritePayloadAsync(Stream stream, JsonObject payload, CancellationToken ct)
            => await WritePayloadsAsync(stream, ct, payload);

        static async Task WritePayloadsAsync(Stream stream, CancellationToken ct, params JsonObject[] payloads)
        {
            foreach (var payload in payloads)
            {
                await stream.WriteAsync(Utf8NoBom.GetBytes(payload.ToJsonString(new() { WriteIndented = false })), ct);
                await stream.WriteAsync(Newline, ct);
            }

            await stream.FlushAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            cts.Cancel();
            try
            {
                await serverTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch { }

            try
            {
                Directory.Delete(endpointDirectory, recursive: true);
            }
            catch { }

            cts.Dispose();
        }
    }
}
