#nullable enable

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Conduit.Runtime
{
    static class RuntimeCommandHandlers
    {
        internal static string BuildStatus()
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

        internal static string BuildHelp() =>
            "Player searches inspect loaded objects only.\n"
            + $"Use {BridgeObjectId.Prefix}12345 for an exact object ID, /Root/Child for a hierarchy path,\n"
            + "t:Camera for a loaded type, or a case-insensitive object/type name fragment.";

        internal static Task<BridgeCommandResult> ExecuteCodeAsync(
            BridgeCommand command,
            CancellationToken ct)
            => CompiledSnippetRunner.ExecuteAsync(
                command.artifacts,
                command.target,
                command.display_name,
                static artifact => artifact.ReadVerified(),
                ct
            );

        internal static BridgeCommandResult Detour(BridgeCommand command)
            => DetourCommandRunner.Execute(
                command.args,
                command.artifacts,
                command.target,
                command.display_name,
                static artifact => artifact.ReadVerified()
            );

        internal static BridgeCommandResult Restart()
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

        internal static BridgeCommandResult QuitPlayer()
        {
            RuntimeBridgeBootstrap.RequestQuit();
            return BridgeCommandResult.Success();
        }

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
