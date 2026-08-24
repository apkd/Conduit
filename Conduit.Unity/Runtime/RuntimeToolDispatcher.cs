#nullable enable

using System.Threading;
using System.Threading.Tasks;

namespace Conduit.Runtime
{
    static class RuntimeToolDispatcher
    {
        internal static async Task<BridgeCommandResult> ExecuteAsync(
            BridgeCommand command,
            CancellationToken ct)
        {
            using var logs = new BridgeLogCapture();
            var result = command.command_type switch
            {
                BridgeCommandTypes.Status => BridgeCommandResult.Success(RuntimeCommandHandlers.BuildStatus()),
                BridgeCommandTypes.Help => BridgeCommandResult.Success(RuntimeCommandHandlers.BuildHelp()),
                BridgeCommandTypes.Search => BridgeCommandResult.Success(
                    RuntimeInspectionCommands.Search(command.target)
                ),
                BridgeCommandTypes.Show => BridgeCommandResult.Success(RuntimeInspectionCommands.Show(command.target)),
                BridgeCommandTypes.ToJson => BridgeCommandResult.Success(
                    RuntimeInspectionCommands.ToJson(command.target)
                ),
                BridgeCommandTypes.FromJsonOverwrite => BridgeCommandResult.Success(
                    RuntimeInspectionCommands.FromJsonOverwrite(command.target, command.snippet)
                ),
                BridgeCommandTypes.Screenshot => await RuntimeScreenshotCommand.ExecuteAsync(command.target, ct),
                BridgeCommandTypes.Reflect => ReflectionTool.Reflect(command.args),
                BridgeCommandTypes.ExecuteCode => await RuntimeCommandHandlers.ExecuteCodeAsync(command, ct),
                BridgeCommandTypes.Detour => RuntimeCommandHandlers.Detour(command),
                BridgeCommandTypes.CompilationReferences => AssemblyReferences.GetManifest(),
                BridgeCommandTypes.AssemblyBlob => AssemblyReferences.GetAssemblyBlobs(command.args),
                BridgeCommandTypes.Restart => RuntimeCommandHandlers.Restart(),
                BridgeCommandTypes.QuitPlayer => RuntimeCommandHandlers.QuitPlayer(),
                _ => BridgeCommandResult.EditorOnly(command.command_type),
            };

            result.logs = logs.Drain();
            return result;
        }
    }
}
