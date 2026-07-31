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
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Conduit.Runtime
{
    static class RuntimeBridgeDispatcher
    {
        static readonly ConcurrentQueue<RuntimeRequest> requests = new();
        static bool executing;

        public static void Enqueue(
            RuntimeBridgeSession session,
            string requestId,
            RuntimeBridgeCommand command) =>
            requests.Enqueue(new(session, requestId, command));

        public static void Pump()
        {
            if (executing || !requests.TryDequeue(out var request))
                return;

            executing = true;
            ExecuteAsync(request);
        }

        static async void ExecuteAsync(RuntimeRequest request)
        {
            var ct = request.Session.Begin(request.RequestId);
            RuntimeBridgeCommandResult result;
            try
            {
                result = await RuntimeToolDispatcher.ExecuteAsync(request.Command, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                result = new()
                {
                    outcome = RuntimeToolOutcome.Cancelled,
                    diagnostic = "The request was cancelled.",
                };
            }
            catch (Exception exception)
            {
                result = RuntimeBridgeCommandResult.FromException(Unwrap(exception));
            }

            request.Session.Complete(request.RequestId);
            executing = false; // a closing one-command FIFO must not hold the player command queue
            try
            {
                await request.Session.SendAsync(
                    RuntimeBridgeMessage.Result(request.RequestId, result)
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
                RuntimeBridgeCommand command)
            {
                Session = session;
                RequestId = requestId;
                Command = command;
            }

            public RuntimeBridgeSession Session { get; }
            public string RequestId { get; }
            public RuntimeBridgeCommand Command { get; }
        }
    }

    static class RuntimeToolDispatcher
    {
        static readonly DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        static readonly Dictionary<string, CachedSnippet> snippets = new(StringComparer.OrdinalIgnoreCase);
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
        static readonly string[] capabilities =
        {
            RuntimeBridgeCommandTypes.Status,
            RuntimeBridgeCommandTypes.Restart,
            RuntimeBridgeCommandTypes.Help,
            RuntimeBridgeCommandTypes.Search,
            RuntimeBridgeCommandTypes.Show,
            RuntimeBridgeCommandTypes.ToJson,
            RuntimeBridgeCommandTypes.FromJsonOverwrite,
            RuntimeBridgeCommandTypes.Screenshot,
            RuntimeBridgeCommandTypes.ExecuteCode,
            RuntimeBridgeCommandTypes.Reflect,
            "profiler_marker",
        };

        public static string[] Capabilities => capabilities;

        public static async Task<RuntimeBridgeCommandResult> ExecuteAsync(
            RuntimeBridgeCommand command,
            CancellationToken ct)
        {
            var logs = new StringBuilder();
            void CaptureLog(string condition, string stackTrace, LogType type)
            {
                logs.Append('[').Append(type).Append("] ").AppendLine(condition);
                if (type is LogType.Exception or LogType.Error && !string.IsNullOrWhiteSpace(stackTrace))
                    logs.AppendLine(stackTrace);
            }

            Application.logMessageReceived += CaptureLog;
            RuntimeBridgeCommandResult result;
            try
            {
                result = command.command_type switch
                {
                    RuntimeBridgeCommandTypes.Status => RuntimeBridgeCommandResult.Success(BuildStatus()),
                    RuntimeBridgeCommandTypes.Help => RuntimeBridgeCommandResult.Success(BuildHelp()),
                    RuntimeBridgeCommandTypes.Search => RuntimeBridgeCommandResult.Success(Search(command.target)),
                    RuntimeBridgeCommandTypes.Show => RuntimeBridgeCommandResult.Success(Show(command.target)),
                    RuntimeBridgeCommandTypes.ToJson => RuntimeBridgeCommandResult.Success(ToJson(command.target)),
                    RuntimeBridgeCommandTypes.FromJsonOverwrite => RuntimeBridgeCommandResult.Success(
                        FromJsonOverwrite(command.target, command.snippet)
                    ),
                    RuntimeBridgeCommandTypes.Screenshot => await ScreenshotAsync(command.target, ct),
                    RuntimeBridgeCommandTypes.Reflect => RuntimeBridgeCommandResult.Success(Reflect(command.args)),
                    RuntimeBridgeCommandTypes.ExecuteCode => await ExecuteCodeAsync(command, ct),
                    RuntimeBridgeCommandTypes.CompilationReferences => RuntimeBridgeCommandResult.Success(
                        BuildCompilationReferences()
                    ),
                    RuntimeBridgeCommandTypes.AssemblyBlob => GetAssemblyBlob(command.target),
                    RuntimeBridgeCommandTypes.Restart => Restart(),
                    _ => RuntimeBridgeCommandResult.EditorOnly(command.command_type),
                };
            }
            finally
            {
                Application.logMessageReceived -= CaptureLog;
            }

            result.logs = logs.ToString().TrimEnd();
            return result;
        }

        static string BuildStatus()
        {
            var scenes = new string[SceneManager.sceneCount];
            for (var index = 0; index < scenes.Length; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                var name = string.IsNullOrWhiteSpace(scene.path) ? scene.name : scene.path;
                scenes[index] = scene == SceneManager.GetActiveScene()
                    ? $"{name} [loaded, active]"
                    : $"{name} [loaded]";
            }

            return JsonUtility.ToJson(
                new RuntimePingSnapshot
                {
                    unity_version = Application.unityVersion,
                    platform = Application.platform.ToString(),
                    editor_process_id = Process.GetCurrentProcess().Id,
                    uptime = FormatDuration(DateTimeOffset.UtcNow - startedAtUtc),
                    editor_mode = "player",
                    active_command_type = RuntimeBridgeCommandTypes.Status,
                    scenes = scenes,
                }
            );
        }

        static string BuildHelp() =>
            "Player searches inspect loaded objects only.\n"
            + $"Use {RuntimeObjectId.Prefix}12345 for an exact object ID, /Root/Child for a hierarchy path,\n"
            + "t:Camera for a loaded type, or a case-insensitive object/type name fragment.";

        static string Search(string? query)
        {
            var matches = ConduitRuntimeSearch.ResolveMany(query);
            if (matches.Count == 0)
                return "No loaded objects matched.";

            var builder = new StringBuilder();
            foreach (var match in matches.Take(200))
            {
                builder.Append(match.name)
                    .Append(" [")
                    .Append(match.GetType().FullName)
                    .Append("] ")
                    .Append(RuntimeObjectId.Format(match));
                if (GetGameObject(match) is { } gameObject)
                    builder.Append(' ').Append(ConduitRuntimeSearch.GetHierarchyPath(gameObject));
                builder.AppendLine();
            }

            if (matches.Count > 200)
                builder.Append("... ").Append(matches.Count - 200).AppendLine(" more");

            return builder.ToString().TrimEnd();
        }

        static string Show(string? query)
        {
            var target = ConduitRuntimeSearch.ResolveOne(query ?? string.Empty);
            var builder = new StringBuilder();
            builder.Append(target.name)
                .Append(" [")
                .Append(target.GetType().FullName)
                .Append("] ")
                .AppendLine(RuntimeObjectId.Format(target));

            if (GetGameObject(target) is { } gameObject)
            {
                builder.Append("Path: ")
                    .AppendLine(ConduitRuntimeSearch.GetHierarchyPath(gameObject));
                AppendHierarchy(builder, gameObject.transform, string.Empty);
            }

            AppendFields(builder, target);
            return builder.ToString().TrimEnd();
        }

        static string ToJson(string? query)
        {
            var target = ConduitRuntimeSearch.ResolveOne(query ?? string.Empty);
            return RuntimeObjectJsonUtility.ToJson(target);
        }

        static string FromJsonOverwrite(string? query, string? json)
        {
            var target = ConduitRuntimeSearch.ResolveOne(query ?? string.Empty);
            RuntimeObjectJsonUtility.FromJsonOverwrite(target, json ?? string.Empty);
            return $"Updated {target.name} [{RuntimeObjectId.Format(target)}].";
        }

        static Task<RuntimeBridgeCommandResult> ScreenshotAsync(
            string? target,
            CancellationToken ct)
        {
#if !(MODULE_IMAGECONVERSION && MODULE_SCREENCAPTURE)
            return Task.FromResult(
                new RuntimeBridgeCommandResult
                {
                    outcome = RuntimeToolOutcome.Exception,
                    diagnostic = ScreenshotModuleUnavailableDiagnostic,
                }
            );
#else
            return CaptureScreenshotAsync(target, ct);
#endif
        }

#if MODULE_IMAGECONVERSION && MODULE_SCREENCAPTURE
        static async Task<RuntimeBridgeCommandResult> CaptureScreenshotAsync(
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
                    outcome = RuntimeToolOutcome.Exception,
                    diagnostic = $"Screenshot target '{normalized}' is unavailable in a player.",
                };
            }

            if (Application.isBatchMode
                || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                return new()
                {
                    outcome = RuntimeToolOutcome.Exception,
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
                        RuntimeBridgeArtifact.FromBytes(
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
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                return texture;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        static string Reflect(string[] args)
        {
            var mode = args.Length > 0 ? args[0] : "types";
            var type = args.Length > 1 ? NullIfEmpty(args[1]) : null;
            var member = args.Length > 2 ? NullIfEmpty(args[2]) : null;
            return ConduitRuntimeReflect.Format(mode, type, member);
        }

        static async Task<RuntimeBridgeCommandResult> ExecuteCodeAsync(
            RuntimeBridgeCommand command,
            CancellationToken ct)
        {
            if (command.artifacts.Length == 0)
                throw new InvalidOperationException("The MCP server did not provide a compiled snippet assembly.");

            var assemblyArtifact = command.artifacts.FirstOrDefault(
                static value => value.name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            );
            if (assemblyArtifact == null)
                throw new InvalidOperationException("The compiled snippet payload has no DLL artifact.");

            if (!snippets.TryGetValue(assemblyArtifact.sha256, out var snippet))
            {
                var pdb = command.artifacts.FirstOrDefault(
                    static value => value.name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                );
                var assemblyBytes = assemblyArtifact.Decode();
                Assembly assembly;
                try
                {
                    assembly = pdb == null
                        ? Assembly.Load(assemblyBytes)
                        : Assembly.Load(assemblyBytes, pdb.Decode());
                }
                catch (ArgumentException) when (pdb != null)
                {
                    assembly = Assembly.Load(assemblyBytes);
                }
                var type = assembly.GetType(command.target ?? string.Empty, throwOnError: true);
                var method = type.GetMethod(
                    "Execute",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                ) ?? throw new MissingMethodException(type.FullName, "Execute");
                snippet = new(method, command.display_name ?? assemblyArtifact.name);
                snippets[assemblyArtifact.sha256] = snippet;
            }

            ct.ThrowIfCancellationRequested();
            var value = snippet.Method.Invoke(null, null);
            if (value is Task task)
            {
                await task;
                value = task.GetType().GetProperty("Result")?.GetValue(task);
            }

            return new()
            {
                display_name = snippet.DisplayName,
                return_value = FormatValue(value),
            };
        }

        static string BuildCompilationReferences()
        {
            var references = new List<RuntimeAssemblyReference>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic || string.IsNullOrWhiteSpace(assembly.Location))
                        continue;

                    var file = new FileInfo(assembly.Location);
                    references.Add(
                        new()
                        {
                            id = assembly.ManifestModule.ModuleVersionId.ToString("N"),
                            assembly_name = assembly.FullName ?? assembly.GetName().Name ?? string.Empty,
                            path = assembly.Location,
                            length = file.Exists ? file.Length : 0,
                        }
                    );
                }
                catch (Exception) { }
            }

            return JsonUtility.ToJson(new RuntimeAssemblyReferenceManifest { references = references.ToArray() });
        }

        static RuntimeBridgeCommandResult GetAssemblyBlob(string? referenceId)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic
                        || assembly.ManifestModule.ModuleVersionId.ToString("N") != referenceId
                        || string.IsNullOrWhiteSpace(assembly.Location))
                        continue;

                    var bytes = File.ReadAllBytes(assembly.Location);
                    return new()
                    {
                        artifacts = new[]
                        {
                            RuntimeBridgeArtifact.FromBytes(
                                Path.GetFileName(assembly.Location),
                                "application/vnd.microsoft.portable-executable",
                                bytes
                            ),
                        },
                    };
                }
                catch (Exception exception)
                {
                    return RuntimeBridgeCommandResult.FromException(exception);
                }
            }

            return new()
            {
                outcome = RuntimeToolOutcome.Exception,
                diagnostic = $"Loaded assembly reference '{referenceId}' was not found.",
            };
        }

        static RuntimeBridgeCommandResult Restart()
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
            var process = Process.Start(startInfo)
                          ?? throw new InvalidOperationException("The replacement player process did not start.");
            RuntimeBridgeBootstrap.RequestQuit();
            return RuntimeBridgeCommandResult.Success(
                JsonUtility.ToJson(
                    new RuntimeRestartResult
                    {
                        process_id = process.Id,
                        handoff_token = handoffToken,
                    }
                )
            );
        }

        static void AppendHierarchy(StringBuilder builder, Transform transform, string indent)
        {
            builder.Append(indent)
                .Append("- ")
                .Append(transform.name)
                .Append(" [")
                .Append(RuntimeObjectId.Format(transform.gameObject))
                .AppendLine("]");
            var childIndent = indent + "  ";
            for (var index = 0; index < transform.childCount; index++)
                AppendHierarchy(builder, transform.GetChild(index), childIndent);
        }

        static void AppendFields(StringBuilder builder, Object target)
        {
            builder.AppendLine("Fields:");
            var count = 0;
            for (var type = target.GetType(); type != null && type != typeof(Object); type = type.BaseType)
                foreach (var field in type.GetFields(
                             BindingFlags.Instance
                             | BindingFlags.Public
                             | BindingFlags.NonPublic
                             | BindingFlags.DeclaredOnly
                         ))
                {
                    if (field.IsStatic || count++ == 100)
                        continue;

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
                        .AppendLine(FormatValue(value) ?? "null");
                }
        }

        static GameObject? GetGameObject(Object target) =>
            target switch
            {
                GameObject gameObject => gameObject,
                Component component => component.gameObject,
                _ => null,
            };

        static string? FormatValue(object? value) =>
            value switch
            {
                null => null,
                Object unityObject => $"{unityObject.name} [{RuntimeObjectId.Format(unityObject)}]",
                string text => text,
                System.Collections.IEnumerable sequence => string.Join(
                    ", ",
                    sequence.Cast<object>().Take(25).Select(FormatValue)
                ),
                _ => value.ToString(),
            };

        static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalDays >= 1)
                return $"{(int)duration.TotalDays} days {duration.Hours} hours";
            if (duration.TotalHours >= 1)
                return $"{(int)duration.TotalHours} hours {duration.Minutes} minutes";
            if (duration.TotalMinutes >= 1)
                return $"{(int)duration.TotalMinutes} minutes {duration.Seconds} seconds";
            return $"{Math.Max(1, duration.Seconds)} seconds";
        }

        static string QuoteArgument(string argument) =>
            argument.Length > 0 && argument.All(static value => !char.IsWhiteSpace(value) && value != '"')
                ? argument
                : '"' + argument.Replace("\\", "\\\\").Replace("\"", "\\\"") + '"';

        static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

        [Serializable]
        sealed class RuntimePingSnapshot
        {
            public string unity_version = string.Empty;
            public string platform = string.Empty;
            public int editor_process_id;
            public string uptime = string.Empty;
            public string editor_mode = "player";
            public string? active_command_type;
            public string[] scenes = Array.Empty<string>();
            public string[] dirty_scenes = Array.Empty<string>();
        }

        [Serializable]
        sealed class RuntimeAssemblyReferenceManifest
        {
            public RuntimeAssemblyReference[] references = Array.Empty<RuntimeAssemblyReference>();
        }

        [Serializable]
        sealed class RuntimeAssemblyReference
        {
            public string id = string.Empty;
            public string assembly_name = string.Empty;
            public string path = string.Empty;
            public long length;
        }

        [Serializable]
        sealed class RuntimeRestartResult
        {
            public int process_id;
            public string handoff_token = string.Empty;
        }

        sealed class CachedSnippet
        {
            public CachedSnippet(MethodInfo method, string displayName)
            {
                Method = method;
                DisplayName = displayName;
            }

            public MethodInfo Method { get; }
            public string DisplayName { get; }
        }
    }
}
