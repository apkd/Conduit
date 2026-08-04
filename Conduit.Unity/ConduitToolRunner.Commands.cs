#nullable enable

using System;
using System.Threading.Tasks;

namespace Conduit
{
    static partial class ConduitToolRunner
    {
        static async Task ExecuteStatusAsync(
            int clientId,
            string requestId,
            long usageStartedUtcTicks
        )
        {
            BridgeCommandResult result;
            try
            {
                result = new()
                {
                    outcome = ToolOutcome.Success,
                    return_value = status.Status(),
                };
            }
            catch (Exception exception)
            {
                result = CreateExceptionResult(exception);
            }

            ConduitToolUsage.CompleteCall(BridgeCommandTypes.Status, usageStartedUtcTicks);
            await ConduitConnection.TrySendResultAsync(
                clientId,
                requestId,
                result,
                BridgeCommandTypes.Status
            );
        }

        static Task ExecuteGetDependenciesAsync(PendingOperationState operation)
            => ExecuteCommandAsync(() => find_references_to.GetDependencies(operation.target ?? string.Empty));

        static Task ExecuteScreenshotAsync(PendingOperationState operation)
#if MODULE_IMAGECONVERSION && MODULE_SCREENCAPTURE
            => ExecuteCommandAsync(() => screenshot.CaptureAsync(operation.target ?? string.Empty));
#else
            => CompleteCurrentAsync(
                new()
                {
                    outcome = ToolOutcome.Exception,
                    diagnostic = screenshot.ModuleUnavailableDiagnostic,
                }
            );
#endif

        static Task ExecuteFindReferencesToAsync(PendingOperationState operation)
            => ExecuteCommandAsync(() => find_references_to.FindReferencesTo(operation.target ?? string.Empty, operation.rebuild_cache));

        static Task ExecuteFindMissingScriptsAsync(PendingOperationState operation)
            => ExecuteCommandAsync(() => find_missing_scripts.FindMissingScripts(operation.target ?? string.Empty));

        static Task ExecuteShowAsync(PendingOperationState operation)
            => ExecuteCommandAsync(() => show.Show(operation.target ?? string.Empty));

        static Task ExecuteSearchAsync(PendingOperationState operation)
            => ExecuteCommandAsync(() => ConduitSearchUtility.Search(operation.target ?? string.Empty));

        static Task ExecuteToJsonAsync(PendingOperationState operation)
            => ExecuteCommandAsync(() => ConduitObjectJsonUtility.ToJson(operation.target ?? string.Empty));

        static Task ExecuteFromJsonOverwriteAsync(PendingOperationState operation)
            => ExecuteCommandAsync(() => ConduitObjectJsonUtility.FromJsonOverwrite(operation.target ?? string.Empty, operation.snippet ?? string.Empty));

        static Task ExecuteSaveScenesAsync(PendingOperationState operation)
            => ExecuteCommandAsync(() => ConduitSceneCommandUtility.SaveScenes(operation.target));

        static Task ExecuteDiscardScenesAsync(PendingOperationState operation)
            => ExecuteCommandAsync(() => ConduitSceneCommandUtility.DiscardScenes(operation.target));

        static async Task ExecuteCodeAsync(PendingOperationState operation)
            => await CompleteCurrentAsync(await execute_code.ExecuteAsync(operation));

        static Task ExecuteDetourAsync(PendingOperationState operation)
            => CompleteCurrentAsync(detour.Execute(operation));

        static async Task ExecuteViewBurstAsmAsync(PendingOperationState operation)
        {
            try
            {
                await CompleteCurrentAsync(view_burst_asm.ViewBurstAsm(operation.target ?? string.Empty));
            }
            catch (Exception exception)
            {
                await CompleteCurrentAsync(CreateExceptionResult(exception));
            }
        }

        static async Task ExecuteReflectAsync(PendingOperationState operation)
        {
            try
            {
                await CompleteCurrentAsync(reflect.Reflect(operation.args));
            }
            catch (Exception exception)
            {
                await CompleteCurrentAsync(CreateExceptionResult(exception));
            }
        }

        static Task ExecuteProjectSettingsAsync(PendingOperationState operation)
            => ExecuteCommandAsync(() => ProjectSettingsTool.Execute(operation));

        static async Task ExecuteProfilerRecordAsync(PendingOperationState operation)
        {
            try
            {
                await CompleteCurrentAsync(await profiler.RecordAsync(operation.args));
            }
            catch (Exception exception)
            {
                await CompleteCurrentAsync(CreateExceptionResult(exception));
            }
        }

        static async Task ExecuteProfilerOverviewAsync(PendingOperationState operation)
        {
            try
            {
                await CompleteCurrentAsync(profiler.Overview(operation.args));
            }
            catch (Exception exception)
            {
                await CompleteCurrentAsync(CreateExceptionResult(exception));
            }
        }

        static async Task ExecuteProfilerBrowseAsync(PendingOperationState operation)
        {
            try
            {
                await CompleteCurrentAsync(profiler.Browse(operation.args));
            }
            catch (Exception exception)
            {
                await CompleteCurrentAsync(CreateExceptionResult(exception));
            }
        }

        static async Task ExecuteProfilerHasMarkerAsync(PendingOperationState operation)
        {
            try
            {
                var markerName = operation.args.Length > 0
                    ? operation.args[0]
                    : string.Empty;
                await CompleteCurrentAsync(
                    CreateSuccessResult(
                        profiler.HasMarker(markerName) ? "true" : "false"
                    )
                );
            }
            catch (Exception exception)
            {
                await CompleteCurrentAsync(CreateExceptionResult(exception));
            }
        }

        static async Task ExecuteCommandAsync(Func<string> getResult)
        {
            try
            {
                await CompleteCurrentAsync(CreateSuccessResult(getResult()));
            }
            catch (Exception exception)
            {
                await CompleteCurrentAsync(CreateExceptionResult(exception));
            }
        }

        static async Task ExecuteCommandAsync(Func<Task<string>> getResult)
        {
            try
            {
                await CompleteCurrentAsync(CreateSuccessResult(await getResult()));
            }
            catch (Exception exception)
            {
                await CompleteCurrentAsync(CreateExceptionResult(exception));
            }
        }

        static BridgeCommandResult CreateSuccessResult(string? returnValue)
            => new()
            {
                outcome = ToolOutcome.Success,
                return_value = returnValue,
            };

        static BridgeCommandResult CreateExceptionResult(Exception exception)
            => new()
            {
                outcome = ToolOutcome.Exception,
                exception = ToExceptionInfo(exception),
                diagnostic = exception.Message,
            };
    }
}
