#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Conduit.Runtime
{
    static class RuntimeBridgeDispatcher
    {
        static readonly ConcurrentQueue<RuntimeRequest> requests = new();
        static bool executing;
        static int requestCount;

        public static void Enqueue(
            RuntimeBridgeSession session,
            string requestId,
            BridgeCommand command)
        {
            requests.Enqueue(new(session, requestId, command));
            Interlocked.Increment(ref requestCount);
        }

        public static void Pump()
        {
            if (Volatile.Read(ref executing)
                || Volatile.Read(ref requestCount) == 0
                || !requests.TryDequeue(out var request))
                return;

            Interlocked.Decrement(ref requestCount);
            Volatile.Write(ref executing, true);
            ExecuteAsync(request);
        }

        static async void ExecuteAsync(RuntimeRequest request)
        {
            var ct = request.Session.Begin(request.RequestId);
            BridgeCommandResult result;
            try
            {
                result = await RuntimeToolDispatcher.ExecuteAsync(request.Command, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                result = new()
                {
                    outcome = ToolOutcome.Cancelled,
                    diagnostic = "The request was cancelled.",
                };
            }
            catch (Exception exception)
            {
                result = BridgeCommandResult.FromException(Unwrap(exception));
            }

            request.Session.Complete(request.RequestId);
            Volatile.Write(ref executing, false); // a closing one-command FIFO must not hold the player command queue
            try
            {
                await request.Session.SendAsync(
                    BridgeMessage.CreateCommandResult(request.RequestId, result)
                );
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        }

        static Exception Unwrap(Exception exception) =>
            exception is TargetInvocationException { InnerException: { } inner }
                ? inner
                : exception;

        readonly struct RuntimeRequest
        {
            public RuntimeRequest(
                RuntimeBridgeSession session,
                string requestId,
                BridgeCommand command)
            {
                Session = session;
                RequestId = requestId;
                Command = command;
            }

            public RuntimeBridgeSession Session { get; }
            public string RequestId { get; }
            public BridgeCommand Command { get; }
        }
    }

    static class RuntimeToolDispatcher
    {
        const int MaximumDisplayedFieldCount = 100;
        static readonly ConcurrentDictionary<Type, FieldInfo[]> fieldCache = new();

#if !MODULE_IMAGECONVERSION && !MODULE_SCREENCAPTURE
        const string ScreenshotModuleUnavailableDiagnostic =
            "ERROR: Unity built-in modules `com.unity.modules.imageconversion` and " +
            "`com.unity.modules.screencapture` are not enabled in this project. " +
            "Ask the user for permission to enable the modules so that the `screenshot` tool can be used.";
#elif !MODULE_IMAGECONVERSION
        const string ScreenshotModuleUnavailableDiagnostic =
            "ERROR: Unity built-in module `com.unity.modules.imageconversion` is not enabled in this project. " +
            "Ask the user for permission to enable the module so that the `screenshot` tool can be used.";
#elif !MODULE_SCREENCAPTURE
        const string ScreenshotModuleUnavailableDiagnostic =
            "ERROR: Unity built-in module `com.unity.modules.screencapture` is not enabled in this project. " +
            "Ask the user for permission to enable the module so that the `screenshot` tool can be used.";
#endif
        public static async Task<BridgeCommandResult> ExecuteAsync(
            BridgeCommand command,
            CancellationToken ct)
        {
            using var logs = new BridgeLogCapture();
            var result = command.command_type switch
            {
                BridgeCommandTypes.Status => BridgeCommandResult.Success(BuildStatus()),
                BridgeCommandTypes.Help => BridgeCommandResult.Success(BuildHelp()),
                BridgeCommandTypes.Search => BridgeCommandResult.Success(Search(command.target)),
                BridgeCommandTypes.Show => BridgeCommandResult.Success(Show(command.target)),
                BridgeCommandTypes.ToJson => BridgeCommandResult.Success(ToJson(command.target)),
                BridgeCommandTypes.FromJsonOverwrite => BridgeCommandResult.Success(
                    FromJsonOverwrite(command.target, command.snippet)
                ),
                BridgeCommandTypes.Screenshot => await ScreenshotAsync(command.target, ct),
                BridgeCommandTypes.Reflect => reflect.Reflect(command.args),
                BridgeCommandTypes.ExecuteCode => await ExecuteCodeAsync(command, ct),
                BridgeCommandTypes.Detour => Detour(command),
                BridgeCommandTypes.CompilationReferences => AssemblyReferences.GetManifest(),
                BridgeCommandTypes.AssemblyBlob => AssemblyReferences.GetAssemblyBlobs(command.args),
                BridgeCommandTypes.Restart => Restart(),
                BridgeCommandTypes.QuitPlayer => QuitPlayer(),
                _ => BridgeCommandResult.EditorOnly(command.command_type),
            };

            result.logs = logs.Drain();
            return result;
        }

        static string BuildStatus()
        {
            var scenes = new string[SceneManager.sceneCount];
            var activeDetours = DetourRuntime.ActiveMethodNames;
            var activeScene = SceneManager.GetActiveScene();
            for (var index = 0; index < scenes.Length; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                var scenePath = scene.path;
                var name = string.IsNullOrWhiteSpace(scenePath) ? scene.name : scenePath;
                scenes[index] = scene == activeScene
                    ? $"{name} [loaded, active]"
                    : $"{name} [loaded]";
            }

            return JsonUtility.ToJson(
                new RuntimePingSnapshot
                {
                    unity_version = Application.unityVersion,
                    platform = Application.platform.ToString(),
                    editor_process_id = BridgeStatusUtility.ProcessId,
                    uptime = BridgeStatusUtility.FormatDuration(
                        TimeSpan.FromSeconds(Time.realtimeSinceStartupAsDouble)
                    ),
                    editor_mode = "player",
                    active_command_type = BridgeCommandTypes.Status,
                    active_detour_count = activeDetours.Length,
                    active_detours = activeDetours,
                    scenes = scenes,
                }
            );
        }

        static string BuildHelp() =>
            "Player searches inspect loaded objects only.\n"
            + $"Use {BridgeObjectId.Prefix}12345 for an exact object ID, /Root/Child for a hierarchy path,\n"
            + "t:Camera for a loaded type, or a case-insensitive object/type name fragment.";

        static string Search(string? query)
        {
            var matches = ConduitRuntimeSearch.ResolveManyForDisplay(query);
            if (matches.Count == 0)
                return $"No matches for '{query?.Trim() ?? string.Empty}'.";

            return ConduitRuntimeSearch.FormatMatches(matches, includeHint: false);
        }

        static string Show(string? query)
        {
            var target = ConduitRuntimeSearch.ResolveOne(query ?? string.Empty);
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            builder.Append(target.name)
                .Append(" [")
                .Append(target.GetType().FullName)
                .Append("] ")
                .AppendLine(BridgeObjectId.Format(target));

            if (GetGameObject(target) is { } gameObject)
            {
                builder.Append("Path: ")
                    .AppendLine(ConduitRuntimeSearch.GetHierarchyPath(gameObject));
                AppendHierarchy(builder, gameObject.transform);
            }

            if (target is GameObject targetGameObject)
            {
                builder.AppendLine("Components:");
                using var pooledComponents = ListPool<Component>.Get(out var components);
                components.Clear();
                targetGameObject.GetComponents(components);
                foreach (var component in components)
                {
                    builder.Append("- ");
                    if (component == null)
                        builder.AppendLine("Missing Component");
                    else
                        builder.Append(component.GetType().FullName)
                            .Append(" [")
                            .Append(BridgeObjectId.Format(component))
                            .AppendLine("]");
                }
            }

            builder.AppendLine("Properties:")
                .AppendLine(RuntimeObjectJsonUtility.ToJson(target));

            AppendFields(builder, target);
            while (builder.Length > 0 && char.IsWhiteSpace(builder[builder.Length - 1]))
                builder.Length--;

            return builder.ToString();
        }

        static string ToJson(string? query)
        {
            var target = ConduitRuntimeSearch.ResolveOne(query ?? string.Empty);
            return RuntimeObjectJsonUtility.ToJson(target);
        }

        static string FromJsonOverwrite(string? query, string? json)
        {
            var target = ConduitRuntimeSearch.ResolveOne(query ?? string.Empty);
            return RuntimeObjectJsonUtility.FromJsonOverwrite(target, json ?? string.Empty);
        }

        static Task<BridgeCommandResult> ScreenshotAsync(
            string? target,
            CancellationToken ct)
        {
#if !(MODULE_IMAGECONVERSION && MODULE_SCREENCAPTURE)
            return Task.FromResult(
                new BridgeCommandResult
                {
                    outcome = ToolOutcome.Exception,
                    diagnostic = ScreenshotModuleUnavailableDiagnostic,
                }
            );
#else
            return CaptureScreenshotAsync(target, ct);
#endif
        }

#if MODULE_IMAGECONVERSION && MODULE_SCREENCAPTURE
        static async Task<BridgeCommandResult> CaptureScreenshotAsync(
            string? target,
            CancellationToken ct)
        {
            var normalized = target?.Trim() ?? string.Empty;
            if (normalized.Length > 0
                && normalized is not ("game_view" or "player" or "screen")
                && !normalized.StartsWith("eid:", StringComparison.OrdinalIgnoreCase)
                && !normalized.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
                && normalized[0] != '/')
            {
                return new()
                {
                    outcome = ToolOutcome.Exception,
                    diagnostic = $"Screenshot target '{normalized}' is unavailable in a player.",
                };
            }

            if (Application.isBatchMode
                || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                return new()
                {
                    outcome = ToolOutcome.Exception,
                    diagnostic = "Player screenshots require an interactive Unity player with a graphics device.",
                };
            }

            Texture2D texture;
            if (normalized.StartsWith("eid:", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("/", StringComparison.Ordinal))
            {
                var resolved = ConduitRuntimeSearch.ResolveOne(normalized);
                var camera = resolved as Camera ?? GetGameObject(resolved)?.GetComponent<Camera>();
                if (camera == null)
                    throw new InvalidOperationException($"Screenshot target '{normalized}' is not a camera.");
                texture = CaptureCamera(camera);
            }
            else
                texture = await RuntimeBridgeBootstrap.CaptureScreenshotAsync(ct);

            try
            {
                var bytes = texture.EncodeToPNG();
                return new()
                {
                    return_value = "Player image captured.",
                    artifacts = new[]
                    {
                        BridgeArtifact.FromBytes(
                            $"player-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.png",
                            "image/png",
                            bytes
                        ),
                    },
                };
            }
            finally
            {
                Object.Destroy(texture);
            }
        }
#endif

        internal static Texture2D CaptureCamera(Camera camera)
        {
            var width = Math.Max(1, Screen.width);
            var height = Math.Max(1, Screen.height);
            var renderTexture = RenderTexture.GetTemporary(width, height, 24);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0); // encoding reads CPU pixels; no GPU upload is needed
                return texture;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        static Task<BridgeCommandResult> ExecuteCodeAsync(
            BridgeCommand command,
            CancellationToken ct)
            => CompiledSnippetRunner.ExecuteAsync(
                command.artifacts,
                command.target,
                command.display_name,
                static artifact => artifact.Decode(),
                ct
            );

        static BridgeCommandResult Detour(BridgeCommand command)
            => DetourCommandRunner.Execute(
                command.args,
                command.artifacts,
                command.target,
                command.display_name,
                static artifact => artifact.Decode()
            );

        static BridgeCommandResult Restart()
        {
            var arguments = Environment.GetCommandLineArgs();
            if (arguments.Length == 0)
                throw new InvalidOperationException("The player executable path is unavailable.");

            var startInfo = new ProcessStartInfo
            {
                FileName = arguments[0],
                Arguments = string.Join(" ", arguments.Skip(1).Select(QuoteArgument)),
                UseShellExecute = false,
            };
            var handoffToken = Guid.NewGuid().ToString("N");
            startInfo.EnvironmentVariables["CONDUIT_HANDOFF_TOKEN"] = handoffToken;
            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("The replacement player process did not start.");
            var processId = process.Id;
            RuntimeBridgeBootstrap.RequestQuit();
            return BridgeCommandResult.Success(
                JsonUtility.ToJson(
                    new RuntimeRestartResult
                    {
                        process_id = processId,
                        handoff_token = handoffToken,
                    }
                )
            );
        }

        static BridgeCommandResult QuitPlayer()
        {
            RuntimeBridgeBootstrap.RequestQuit();
            return BridgeCommandResult.Success();
        }

        static void AppendHierarchy(StringBuilder builder, Transform root)
        {
            using var pooledPending = ListPool<(Transform Transform, int Depth)>.Get(out var pending);
            pending.Clear();
            pending.Add((root, 0));
            while (pending.Count > 0)
            {
                var lastIndex = pending.Count - 1;
                var (transform, depth) = pending[lastIndex];
                pending.RemoveAt(lastIndex);
                builder.Append(' ', depth * 2)
                .Append("- ")
                .Append(transform.name)
                .Append(" [")
                .Append(BridgeObjectId.Format(transform.gameObject))
                .AppendLine("]");

                // reverse insertion preserves Unity's sibling order when the stack is consumed.
                for (var index = transform.childCount - 1; index >= 0; --index)
                    pending.Add((transform.GetChild(index), depth + 1));
            }
        }

        static void AppendFields(StringBuilder builder, Object target)
        {
            var fields = fieldCache.GetOrAdd(target.GetType(), static targetType =>
            {
                var fields = new List<FieldInfo>();
                for (var type = targetType;
                     type != null && type != typeof(Object);
                     type = type.BaseType)
                {
                    foreach (var field in type.GetFields(
                                 BindingFlags.Instance
                                 | BindingFlags.Public
                                 | BindingFlags.NonPublic
                                 | BindingFlags.DeclaredOnly
                             ))
                    {
                        fields.Add(field);
                        if (fields.Count == MaximumDisplayedFieldCount)
                            return fields.ToArray();
                    }
                }

                return fields.ToArray();
            });
            if (fields.Length == 0)
                return;

            builder.AppendLine("Fields:");
            foreach (var field in fields)
            {
                object? value;
                try
                {
                    value = field.GetValue(target);
                }
                catch (Exception exception)
                {
                    value = $"<{exception.GetType().Name}>";
                }

                builder.Append("- ")
                    .Append(field.Name)
                    .Append(": ")
                    .AppendLine(BridgeValueFormatter.Format(value) ?? "null");
            }
        }

        static GameObject? GetGameObject(Object target) =>
            target switch
            {
                GameObject gameObject => gameObject,
                Component component => component.gameObject,
                _ => null,
            };

        static string QuoteArgument(string argument) =>
            argument.Length > 0 && argument.All(static value => !char.IsWhiteSpace(value) && value != '"')
                ? argument
                : '"' + argument.Replace("\\", "\\\\").Replace("\"", "\\\"") + '"';

        [Serializable]
        sealed class RuntimePingSnapshot
        {
            public string unity_version = string.Empty;
            public string platform = string.Empty;
            public int editor_process_id;
            public string uptime = string.Empty;
            public string editor_mode = "player";
            public string? active_command_type;
            public int active_detour_count;
            public string[] active_detours = Array.Empty<string>();
            public string[] scenes = Array.Empty<string>();
            public string[] dirty_scenes = Array.Empty<string>();
        }

        [Serializable]
        sealed class RuntimeRestartResult
        {
            public int process_id;
            public string handoff_token = string.Empty;
        }

    }
}
