#nullable enable

using System;
using System.Threading.Tasks;

namespace Conduit
{
    static partial class ConduitToolRunner
    {
        static Task ExecuteStatusAsync(
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
            return ConduitConnection.TrySendResultAsync(
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

        static async Task ExecuteRecordAsync(PendingOperationState operation)
        {
            try
            {
                await CompleteCurrentAsync(
                    CreateSuccessResult(await RecordTool.ExecuteAsync(operation.target, operation.args))
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

        static Task ExecuteViewBurstAsmAsync(PendingOperationState operation)
        {
            BridgeCommandResult result;
            try
            {
                var cpu = operation.args is { Length: > 0 } ? operation.args[0] : "x86";
                result = view_burst_asm.ViewBurstAsm(operation.target ?? string.Empty, cpu);
            }
            catch (Exception exception)
            {
                result = CreateExceptionResult(exception);
            }

            return CompleteCurrentAsync(result);
        }

        static Task ExecuteReflectAsync(PendingOperationState operation)
        {
            BridgeCommandResult result;
            try
            {
                result = reflect.Reflect(operation.args);
            }
            catch (Exception exception)
            {
                result = CreateExceptionResult(exception);
            }

            return CompleteCurrentAsync(result);
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

        static Task ExecuteProfilerOverviewAsync(PendingOperationState operation)
        {
            BridgeCommandResult result;
            try
            {
                result = profiler.Overview(operation.args);
            }
            catch (Exception exception)
            {
                result = CreateExceptionResult(exception);
            }

            return CompleteCurrentAsync(result);
        }

        static Task ExecuteProfilerBrowseAsync(PendingOperationState operation)
        {
            BridgeCommandResult result;
            try
            {
                result = profiler.Browse(operation.args);
            }
            catch (Exception exception)
            {
                result = CreateExceptionResult(exception);
            }

            return CompleteCurrentAsync(result);
        }

        static Task ExecuteProfilerHasMarkerAsync(PendingOperationState operation)
        {
            BridgeCommandResult result;
            try
            {
                var markerName = operation.args.Length > 0
                    ? operation.args[0]
                    : string.Empty;
                result = CreateSuccessResult(
                    profiler.HasMarker(markerName) ? "true" : "false"
                );
            }
            catch (Exception exception)
            {
                result = CreateExceptionResult(exception);
            }

            return CompleteCurrentAsync(result);
        }

        static Task ExecuteCommandAsync(Func<string> getResult)
        {
            BridgeCommandResult result;
            try
            {
                result = CreateSuccessResult(getResult());
            }
            catch (Exception exception)
            {
                result = CreateExceptionResult(exception);
            }

            return CompleteCurrentAsync(result);
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
