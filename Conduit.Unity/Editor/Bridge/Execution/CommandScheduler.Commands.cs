#nullable enable

using System;
using System.Threading.Tasks;

namespace Conduit
{
    sealed partial class CommandScheduler
    {
        Task ExecuteStatusAsync(
            int clientId,
            string requestId,
            long usageStartedUtcTicks,
            bool includeBackgroundLogs
        )
        {
            var backgroundLogs = includeBackgroundLogs ? BridgeLogs.TakeBackground() : null;
            BridgeCommandResult result;
            try
            {
                result = new()
                {
                    outcome = ToolOutcome.Success,
                    return_value = StatusTool.Status(),
                };
            }
            catch (Exception exception)
            {
                result = CreateExceptionResult(exception);
            }

            result.background_logs = backgroundLogs;
            ConduitToolUsage.CompleteCall(BridgeCommandTypes.Status, usageStartedUtcTicks);
            return ConduitConnection.TrySendResultAsync(
                clientId,
                requestId,
                result,
                BridgeCommandTypes.Status
            );
        }

        Task ExecuteGetDependenciesAsync(PendingOperationState operation)
            => ExecuteCommandAsync(() => AssetReferencesTool.GetDependencies(operation.Target ?? string.Empty));

        Task ExecuteScreenshotAsync(PendingOperationState operation)
#if MODULE_IMAGECONVERSION && MODULE_SCREENCAPTURE
            => ExecuteCommandAsync(() => ScreenshotTool.CaptureAsync(operation.Target ?? string.Empty));
#else
            => CompleteCurrentAsync(
                new()
                {
                    outcome = ToolOutcome.Exception,
                    diagnostic = ScreenshotTool.ModuleUnavailableDiagnostic,
                }
            );
#endif

        async Task ExecuteRecordAsync(PendingOperationState operation)
        {
            try
            {
                await CompleteCurrentAsync(
                    BridgeCommandResult.Success(await RecordTool.ExecuteAsync(operation.Target, operation.Args))
                );
            }
            catch (RecordTool.WaitCancelledException)
            {
                await CompleteCurrentAsync(
                    new()
                    {
                        outcome = ToolOutcome.Cancelled,
                        diagnostic = "The recording wait was cancelled; recording continues in the background.",
                    }
                );
            }
            catch (Exception exception)
            {
                await CompleteCurrentAsync(CreateExceptionResult(exception));
            }
        }

        Task ExecuteFindReferencesToAsync(PendingOperationState operation)
            => ExecuteCommandAsync(() => AssetReferencesTool.FindReferencesTo(operation.Target ?? string.Empty, operation.RebuildCache));

        Task ExecuteFindMissingScriptsAsync(PendingOperationState operation)
            => ExecuteCommandAsync(() => FindMissingScriptsTool.FindMissingScripts(operation.Target ?? string.Empty));

        Task ExecuteShowAsync(PendingOperationState operation)
            => ExecuteCommandAsync(() => ShowTool.Show(operation.Target ?? string.Empty));

        Task ExecuteSearchAsync(PendingOperationState operation)
            => ExecuteCommandAsync(() => ConduitSearchUtility.Search(operation.Target ?? string.Empty));

        Task ExecuteToJsonAsync(PendingOperationState operation)
            => ExecuteCommandAsync(() => ConduitObjectJsonUtility.ToJson(operation.Target ?? string.Empty));

        Task ExecuteFromJsonOverwriteAsync(PendingOperationState operation)
            => ExecuteCommandAsync(() => ConduitObjectJsonUtility.FromJsonOverwrite(operation.Target ?? string.Empty, operation.Snippet ?? string.Empty));

        Task ExecuteSaveScenesAsync(PendingOperationState operation)
            => ExecuteCommandAsync(() => ConduitSceneCommandUtility.SaveScenes(operation.Target));

        Task ExecuteDiscardScenesAsync(PendingOperationState operation)
            => ExecuteCommandAsync(() => ConduitSceneCommandUtility.DiscardScenes(operation.Target));

        async Task ExecuteCodeAsync(PendingOperationState operation)
            => await CompleteCurrentAsync(await ExecuteCodeTool.ExecuteAsync(operation));

        Task ExecuteDetourAsync(PendingOperationState operation)
            => CompleteCurrentAsync(DetourTool.Execute(operation));

        Task ExecuteViewBurstAsmAsync(PendingOperationState operation)
        {
            BridgeCommandResult result;
            try
            {
                var cpu = operation.Args is { Length: > 0 } ? operation.Args[0] : "x86";
                result = ViewBurstAsmTool.ViewBurstAsm(operation.Target ?? string.Empty, cpu);
            }
            catch (Exception exception)
            {
                result = CreateExceptionResult(exception);
            }

            return CompleteCurrentAsync(result);
        }

        Task ExecuteReflectAsync(PendingOperationState operation)
        {
            BridgeCommandResult result;
            try
            {
                result = ReflectionTool.Reflect(operation.Args);
            }
            catch (Exception exception)
            {
                result = CreateExceptionResult(exception);
            }

            return CompleteCurrentAsync(result);
        }

        Task ExecuteProjectSettingsAsync(PendingOperationState operation)
            => ExecuteCommandAsync(() => ProjectSettingsTool.Execute(operation));

        async Task ExecuteProfilerRecordAsync(PendingOperationState operation)
        {
            try
            {
                await CompleteCurrentAsync(await ProfilerTool.RecordAsync(operation.Args));
            }
            catch (Exception exception)
            {
                await CompleteCurrentAsync(CreateExceptionResult(exception));
            }
        }

        Task ExecuteProfilerOverviewAsync(PendingOperationState operation)
        {
            BridgeCommandResult result;
            try
            {
                result = ProfilerTool.Overview(operation.Args);
            }
            catch (Exception exception)
            {
                result = CreateExceptionResult(exception);
            }

            return CompleteCurrentAsync(result);
        }

        Task ExecuteProfilerBrowseAsync(PendingOperationState operation)
        {
            BridgeCommandResult result;
            try
            {
                result = ProfilerTool.Browse(operation.Args);
            }
            catch (Exception exception)
            {
                result = CreateExceptionResult(exception);
            }

            return CompleteCurrentAsync(result);
        }

        Task ExecuteProfilerHasMarkerAsync(PendingOperationState operation)
        {
            BridgeCommandResult result;
            try
            {
                var markerName = operation.Args.Length > 0
                    ? operation.Args[0]
                    : string.Empty;
                result = BridgeCommandResult.Success(
                    ProfilerTool.HasMarker(markerName) ? "true" : "false"
                );
            }
            catch (Exception exception)
            {
                result = CreateExceptionResult(exception);
            }

            return CompleteCurrentAsync(result);
        }

        Task ExecuteCommandAsync(Func<string> getResult)
        {
            BridgeCommandResult result;
            try
            {
                result = BridgeCommandResult.Success(getResult());
            }
            catch (Exception exception)
            {
                result = CreateExceptionResult(exception);
            }

            return CompleteCurrentAsync(result);
        }

        async Task ExecuteCommandAsync(Func<Task<string>> getResult)
        {
            try
            {
                await CompleteCurrentAsync(BridgeCommandResult.Success(await getResult()));
            }
            catch (Exception exception)
            {
                await CompleteCurrentAsync(CreateExceptionResult(exception));
            }
        }

        BridgeCommandResult CreateExceptionResult(Exception exception)
            => new()
            {
                outcome = ToolOutcome.Exception,
                exception = BridgeExceptionFormatter.ToInfo(exception),
                diagnostic = exception.Message,
            };
    }
}
