#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Conduit.Runtime
{
    sealed partial class RuntimeBridgeEndpoint : IDisposable
    {
        const string LeaseTimestampPlaceholder = "__CONDUIT_LEASE_TIMESTAMP__";
        static readonly UTF8Encoding utf8NoBom = new(false);
        static readonly TimeSpan leaseInterval = TimeSpan.FromSeconds(2);
        readonly CancellationTokenSource cancellation = new();
        readonly ConcurrentDictionary<int, RuntimeBridgeSession> sessions = new();
        readonly BridgeEndpointDescriptor descriptor;
        readonly BridgeProjectHandshake handshake;
        readonly string endpointDirectory;
        readonly string descriptorPath;
        readonly string descriptorJsonPrefix;
        readonly string descriptorJsonSuffix;
        readonly string temporaryDescriptorPath;
        readonly bool useFifo;
        readonly bool readFifoSynchronously;
        int nextSessionId;

        internal RuntimeBridgeEndpoint()
        {
            var processId = BridgeStatusUtility.ProcessId;
            var sessionId = Guid.NewGuid().ToString("N");
            var wine = RuntimePlatformUtility.IsWine();
            var platform = Application.platform;
            var platformName = platform.ToString();
            var productName = Application.productName;
            var unityVersion = Application.unityVersion;
            var buildGuid = Application.buildGUID;
            var cloudProjectId = Application.cloudProjectId;
            var companyName = Application.companyName;
            var handoffToken = Environment.GetEnvironmentVariable("CONDUIT_HANDOFF_TOKEN") ?? string.Empty;
            var now = DateTimeOffset.UtcNow.ToString("O");
            useFifo = platform != RuntimePlatform.WindowsPlayer || wine;
            readFifoSynchronously = wine; // wine can consume FIFO bytes without completing overlapped reads
            var root = RuntimeIpcPaths.GetRoot(wine);
            endpointDirectory = Path.Combine(root, "endpoints", $"player-{processId}-{sessionId}");
            descriptorPath = Path.Combine(endpointDirectory, "endpoint.json");
            temporaryDescriptorPath = descriptorPath + ".tmp";
            Directory.CreateDirectory(Path.Combine(endpointDirectory, "clients"));
            RuntimeIpcPaths.TryRestrictDirectory(endpointDirectory);

            handshake = new()
            {
                display_name = productName,
                unity_version = unityVersion,
                process_id = processId,
                endpoint_kind = "player",
                platform = platformName,
                build_guid = buildGuid,
                cloud_project_id = cloudProjectId,
                company_name = companyName,
                product_name = productName,
                can_monitor_process = !wine,
                session_instance_id = sessionId,
                handoff_token = handoffToken,
                last_seen_utc = now,
            };
            descriptor = new()
            {
                endpoint_kind = "player",
                transport = useFifo ? "fifo" : "named_pipe",
                endpoint_id = Path.GetFileName(endpointDirectory),
                pipe_name = useFifo ? string.Empty : $"unity-conduit-player-{processId}-{sessionId[..12]}",
                process_id = processId,
                session_instance_id = sessionId,
                handoff_token = handoffToken,
                unity_version = unityVersion,
                platform = platformName,
                build_guid = buildGuid,
                cloud_project_id = cloudProjectId,
                company_name = companyName,
                product_name = productName,
                started_utc = now,
                last_seen_utc = LeaseTimestampPlaceholder,
                can_monitor_process = !wine,
                is_test_player = Environment.GetEnvironmentVariable("CONDUIT_TEST_PLAYER") == "1",
            };
            // lease refreshes change only this field, so avoid serializing the immutable descriptor every two seconds
            var descriptorJson = JsonUtility.ToJson(descriptor);
            var timestampIndex = descriptorJson.IndexOf(
                LeaseTimestampPlaceholder,
                StringComparison.Ordinal
            );
            if (timestampIndex < 0)
                throw new InvalidOperationException("The runtime endpoint descriptor omitted its lease timestamp.");
            descriptorJsonPrefix = descriptorJson.Substring(0, timestampIndex);
            descriptorJsonSuffix = descriptorJson.Substring(
                timestampIndex + LeaseTimestampPlaceholder.Length
            );
            descriptor.last_seen_utc = now;
        }

        internal void Start()
        {
            WriteDescriptor();
            _ = RefreshLeaseAsync(cancellation.Token);
            _ = useFifo
                ? RunFifoAcceptLoopAsync(cancellation.Token)
                : RunNamedPipeAcceptLoopAsync(cancellation.Token);
        }

        internal string SessionInstanceId => descriptor.session_instance_id;

        public void Dispose()
        {
            cancellation.Cancel();
            foreach (var session in sessions.Values)
                session.Dispose();
            sessions.Clear();
            cancellation.Dispose();

            try
            {
                Directory.Delete(endpointDirectory, recursive: true);
            }
            catch (Exception) { }
        }

        async Task RefreshLeaseAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(leaseInterval, ct).ConfigureAwait(false);
                    WriteDescriptor();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Conduit player endpoint lease update failed: {exception.Message}");
                }
            }
        }

        string WriteDescriptor()
        {
            lock (descriptor)
            {
                var lastSeenUtc = DateTimeOffset.UtcNow.ToString("O");
                File.WriteAllText(
                    temporaryDescriptorPath,
                    string.Concat(descriptorJsonPrefix, lastSeenUtc, descriptorJsonSuffix),
                    utf8NoBom
                );
                if (File.Exists(descriptorPath))
                    File.Replace(temporaryDescriptorPath, descriptorPath, null);
                else
                    File.Move(temporaryDescriptorPath, descriptorPath);
                return lastSeenUtc;
            }
        }

        static async Task DelayAfterFailureAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(500, ct);
            }
            catch (OperationCanceledException) { }
        }
    }
}
