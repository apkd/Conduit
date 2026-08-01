#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;

namespace Conduit
{
    sealed class EditorModeTransition
    {
        // play mode flags can lag for a frame or two; a short grace period avoids false busy failures
        static readonly TimeSpan enterPlayModeBusyWaitTimeout = TimeSpan.FromSeconds(1);
        readonly Func<BridgeCommandResult, Task> complete;
        BridgeCommandKind commandKind;
        bool hooksInstalled;
        bool restoredOperation;
        bool enterPlayModeRequested;
        bool exitPlayModeRequested;
        double enterPlayModeBusyWaitDeadline;

        public EditorModeTransition(Func<BridgeCommandResult, Task> complete)
            => this.complete = complete;

        public void Start(BridgeCommandKind commandKind, bool restoredOperation)
        {
            this.commandKind = commandKind;
            this.restoredOperation = restoredOperation;
            InstallHooks();
            ResetState();
            TryAdvance();
        }

        public void Stop()
        {
            if (hooksInstalled)
            {
                hooksInstalled = false;
                EditorApplication.update -= OnUpdate;
            }

            commandKind = BridgeCommandKind.Unknown;
            restoredOperation = false;
            ResetState();
        }

        public static bool ShouldWaitToEnterPlayMode(bool isCompiling, bool isUpdating, bool isPlayingOrWillChangePlaymode)
            => isCompiling || isUpdating || isPlayingOrWillChangePlaymode;

        public static bool ShouldFailEnterPlayForCompileErrors(bool scriptCompilationFailed)
            => scriptCompilationFailed;

        public static string BuildEnterPlayBusyDiagnostic(bool isCompiling, bool isUpdating, bool isPlayingOrWillChangePlaymode)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.Append("Cannot enter play mode while Unity is busy:");

            var appendedReason = false;
            AppendReason("compiling scripts", isCompiling);
            AppendReason("updating assets", isUpdating);
            AppendReason("changing play mode", isPlayingOrWillChangePlaymode);
            builder.Append('.');
            return builder.ToString();

            void AppendReason(string reason, bool shouldAppend)
            {
                if (!shouldAppend)
                    return;

                builder.Append(appendedReason ? ", " : " ");
                builder.Append(reason);
                appendedReason = true;
            }
        }

        public static string BuildEnterPlayCompileErrorDiagnostic()
            => "Cannot enter play mode because the project has compilation errors.";

        public static string BuildCompletionDiagnostic(bool targetPlayMode, bool changedMode, bool isPaused)
            => ((targetPlayMode, changedMode) switch
            {
                (true, true)   => $"Entered play mode. Paused: {(isPaused ? "yes" : "no")}.",
                (true, false)  => $"Already in play mode. Paused: {(isPaused ? "yes" : "no")}.",
                (false, true)  => "Entered edit mode.",
                (false, false) => "Already in edit mode.",
            }) + DetourEditorLifecycle.BuildCompletionSuffix(targetPlayMode);

        void InstallHooks()
        {
            if (hooksInstalled)
                return;

            hooksInstalled = true;
            EditorApplication.update += OnUpdate;
        }

        void OnUpdate() => TryAdvance();

        void TryAdvance()
        {
            if (!BridgeCommandKinds.IsEditorMode(commandKind))
                return;

            var enterPlayMode = commandKind == BridgeCommandKind.PlayMode;
            if (!enterPlayMode)
            {
                TryAdvanceExitPlayMode();
                return;
            }

            TryAdvanceEnterPlayMode();
        }

        void TryAdvanceExitPlayMode()
        {
            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                var changedMode = exitPlayModeRequested;
                ResetState();
                _ = complete(
                    new()
                    {
                        outcome = ToolOutcome.Success,
                        return_value = BuildCompletionDiagnostic(false, changedMode, false),
                    }
                );
                return;
            }

            exitPlayModeRequested = true;
            EditorApplication.isPlaying = false;
        }

        void TryAdvanceEnterPlayMode()
        {
            if (EditorApplication.isPlaying)
            {
                var changedMode = enterPlayModeRequested;
                ResetState();
                _ = complete(
                    new()
                    {
                        outcome = ToolOutcome.Success,
                        return_value = BuildCompletionDiagnostic(true, changedMode, EditorApplication.isPaused),
                    }
                );
                return;
            }

            if (enterPlayModeRequested)
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    enterPlayModeRequested = false; // unity can reject the request after scripts finish compiling
                else
                    return;
            }

            if (restoredOperation && EditorApplication.isPlayingOrWillChangePlaymode)
            {
                // after reload, Unity may already be carrying out the persisted request
                enterPlayModeRequested = true;
                return;
            }

            if (ShouldWaitToEnterPlayMode(
                    EditorApplication.isCompiling,
                    EditorApplication.isUpdating,
                    EditorApplication.isPlayingOrWillChangePlaymode
                ))
            {
                EnsureBusyWaitDeadline();
                if (EditorApplication.timeSinceStartup < enterPlayModeBusyWaitDeadline)
                    return;

                _ = complete(
                    new()
                    {
                        outcome = ToolOutcome.Exception,
                        diagnostic = BuildEnterPlayBusyDiagnostic(
                            EditorApplication.isCompiling,
                            EditorApplication.isUpdating,
                            EditorApplication.isPlayingOrWillChangePlaymode
                        ),
                    }
                );
                return;
            }

            ResetBusyWaitDeadline();
            if (ShouldFailEnterPlayForCompileErrors(EditorUtility.scriptCompilationFailed))
            {
                _ = complete(
                    new()
                    {
                        outcome = ToolOutcome.CompileError,
                        diagnostic = BuildEnterPlayCompileErrorDiagnostic(),
                    }
                );
                return;
            }

            ConduitGameView.PrepareForPlayMode();
            enterPlayModeRequested = true;
            EditorApplication.isPlaying = true;
        }

        void EnsureBusyWaitDeadline()
        {
            if (enterPlayModeBusyWaitDeadline > 0)
                return;

            enterPlayModeBusyWaitDeadline = EditorApplication.timeSinceStartup + enterPlayModeBusyWaitTimeout.TotalSeconds;
        }

        void ResetBusyWaitDeadline()
            => enterPlayModeBusyWaitDeadline = 0;

        void ResetState()
        {
            ResetBusyWaitDeadline();
            enterPlayModeRequested = false;
            exitPlayModeRequested = false;
        }
    }

    sealed class AssetImportMonitor
    {
        // imports and compiler callbacks can finish a few updates after a synchronous refresh returns
        internal const int IdleSettleUpdates = 8;
        readonly StringBuilder compilerMessageBuffer = new();
        readonly Func<BridgeCommandResult, Task> complete;
        PendingOperationState? operation;
        BridgeCommandKind commandKind;
        bool hooksInstalled;
        bool refreshReturned;
        int idleUpdateCount;

        public AssetImportMonitor(Func<BridgeCommandResult, Task> complete)
            => this.complete = complete;

        public void Start(PendingOperationState operation, BridgeCommandKind commandKind)
        {
            this.operation = operation;
            this.commandKind = commandKind;
            ClearCompilerMessages();

            if (TryCompletePlayModeBlock(operation.command_type)
                || TryCompleteOpenSceneDiskChangeBlock(operation.command_type)
                || commandKind == BridgeCommandKind.ReimportAssets && !TryPrepareReimportAssets(operation))
                return;

            InstallHooks();
            ResetSettlementState();
            if (commandKind == BridgeCommandKind.ReimportAssets)
                ReimportResolvedAssets(operation.reimport_asset_paths);
            else
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            refreshReturned = true;
            TryFinish(countIdleUpdate: false);
        }

        public void ResumeRestored(PendingOperationState operation, BridgeCommandKind commandKind)
        {
            this.operation = operation;
            this.commandKind = commandKind;
            ClearCompilerMessages();
            InstallHooks();
            refreshReturned = true;
            idleUpdateCount = IdleSettleUpdates;
            TryFinish(countIdleUpdate: false);
        }

        public void Stop()
        {
            if (hooksInstalled)
            {
                hooksInstalled = false;
                CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
                EditorApplication.update -= OnUpdate;
            }

            operation = null;
            commandKind = BridgeCommandKind.Unknown;
            ResetSettlementState();
        }

        public void ClearCompilerMessages()
            => compilerMessageBuffer.Clear();

        public static bool ShouldBlockForPlayMode(bool isPlaying)
            => isPlaying;

        public static string BuildPlayModeDiagnostic(string commandType = BridgeCommandTypes.RefreshAssetDatabase)
            => $"Cannot run '{commandType}' while Unity is in play mode. Use 'editmode' to return to edit mode first.";

        public static bool ShouldWaitForIdle(bool refreshReturned, bool isCompiling, bool isUpdating, int idleUpdateCount)
            => !refreshReturned
               || isCompiling
               || isUpdating
               || idleUpdateCount < IdleSettleUpdates;

        public static string BuildRestoredCompileErrorDiagnostic()
            => "Asset refresh completed, but the project has compilation errors.";

        internal static bool IsCompilationInputAssetPath(string assetPath, bool isManagedAssembly)
        {
            var extension = Path.GetExtension(assetPath);
            return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                   || extension.Equals(".asmdef", StringComparison.OrdinalIgnoreCase)
                   || extension.Equals(".asmref", StringComparison.OrdinalIgnoreCase)
                   || extension.Equals(".rsp", StringComparison.OrdinalIgnoreCase)
                   || isManagedAssembly && extension.Equals(".dll", StringComparison.OrdinalIgnoreCase);
        }

        internal static string BuildCompilationInputReimportDiagnostic(string assetPath)
            => $"Cannot reimport script compilation input '{assetPath}' with '{BridgeCommandTypes.ReimportAssets}'. "
               + $"No assets were reimported. Use '{BridgeCommandTypes.RefreshAssetDatabase}' instead.";

        public static string FormatReimportedAssetFilenames(string? assetPathPayload)
            => FormatReimportedAssetFilenames(SplitReimportAssetPaths(assetPathPayload));

        public static string FormatReimportedAssetFilenames(IReadOnlyList<string>? assetPaths)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.AppendLine("Reimported assets:");
            var appendedAny = false;
            if (assetPaths != null)
            {
                foreach (var assetPath in assetPaths)
                {
                    builder.Append("- ");
                    builder.Append(GetAssetFileName(assetPath));
                    builder.Append('\n');
                    appendedAny = true;
                }
            }

            if (appendedAny)
                return builder.TrimEnd().ToString();

            return "No assets were reimported.";
        }

        bool TryCompletePlayModeBlock(string commandType)
        {
            if (!ShouldBlockForPlayMode(EditorApplication.isPlaying))
                return false;

            _ = complete(
                new()
                {
                    outcome = ToolOutcome.Exception,
                    diagnostic = BuildPlayModeDiagnostic(commandType),
                }
            );
            return true;
        }

        bool TryCompleteOpenSceneDiskChangeBlock(string commandType)
        {
            if (ConduitOpenSceneDiskChangeGuard.PrepareOpenScenesForAssetRefresh(commandType) is not { Length: > 0 } diagnostic)
                return false;

            _ = complete(
                new()
                {
                    outcome = ToolOutcome.DirtyScene,
                    diagnostic = diagnostic,
                }
            );
            return true;
        }

        bool TryPrepareReimportAssets(PendingOperationState operation)
        {
            var assetPaths = ConduitSearchUtility.ResolveAssetPaths(operation.target ?? string.Empty);
            if (assetPaths.Count == 0)
            {
                _ = complete(
                    new()
                    {
                        outcome = ToolOutcome.Success,
                        return_value = "No assets matched the query.",
                    }
                );
                return false;
            }

            // code imports can reload the domain before the bridge reports completion
            foreach (var assetPath in assetPaths)
            {
                var isManagedAssembly = Path.GetExtension(assetPath).Equals(".dll", StringComparison.OrdinalIgnoreCase)
                                        && AssetImporter.GetAtPath(assetPath) is PluginImporter { isNativePlugin: false };
                if (!IsCompilationInputAssetPath(assetPath, isManagedAssembly))
                    continue;

                _ = complete(
                    new()
                    {
                        outcome = ToolOutcome.Exception,
                        diagnostic = BuildCompilationInputReimportDiagnostic(assetPath),
                    }
                );
                return false;
            }

            operation.reimport_asset_paths = assetPaths.ToArray();
            // resolved paths survive reloads and avoid rescanning a query against changed project state
            OperationPersistence.SaveActiveOperation(operation, BridgeCommandKind.ReimportAssets);
            return true;
        }

        void ReimportResolvedAssets(IEnumerable<string> assetPaths)
        {
            foreach (var assetPath in assetPaths)
            {
                AssetDatabase.ImportAsset(
                    assetPath,
                    ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport
                );
            }
        }

        void InstallHooks()
        {
            if (hooksInstalled)
                return;

            hooksInstalled = true;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            EditorApplication.update += OnUpdate;
        }

        void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            foreach (var message in messages)
            {
                if (message.type != CompilerMessageType.Error)
                    continue;

                compilerMessageBuffer.AppendLine($"{assemblyPath}: {message.message} ({message.file}:{message.line})");
            }
        }

        void OnUpdate() => TryFinish(countIdleUpdate: true);

        void TryFinish(bool countIdleUpdate)
        {
            if (operation == null || !BridgeCommandKinds.IsAssetImport(commandKind) || !refreshReturned)
                return;

            var isCompiling = EditorApplication.isCompiling;
            var isUpdating = EditorApplication.isUpdating;
            if (isCompiling || isUpdating)
            {
                idleUpdateCount = 0;
                return;
            }

            if (countIdleUpdate && idleUpdateCount < IdleSettleUpdates)
                idleUpdateCount++;

            if (ShouldWaitForIdle(refreshReturned, isCompiling, isUpdating, idleUpdateCount))
                return;

            var compilerMessages = compilerMessageBuffer.ToString().Trim();
            // restored refreshes miss compilation callbacks that fired before domain reload
            var hasRestoredCompileFailure = operation.is_restored
                                            && string.IsNullOrWhiteSpace(compilerMessages)
                                            && EditorUtility.scriptCompilationFailed;
            _ = complete(
                new()
                {
                    outcome = string.IsNullOrWhiteSpace(compilerMessages) && !hasRestoredCompileFailure
                        ? ToolOutcome.Success
                        : ToolOutcome.CompileError,
                    return_value = commandKind == BridgeCommandKind.ReimportAssets
                        ? FormatReimportedAssetFilenames(operation.reimport_asset_paths)
                        : null,
                    diagnostic = string.IsNullOrWhiteSpace(compilerMessages)
                        ? hasRestoredCompileFailure ? BuildRestoredCompileErrorDiagnostic() : null
                        : compilerMessages,
                }
            );
        }

        void ResetSettlementState()
        {
            refreshReturned = false;
            idleUpdateCount = 0;
        }

        static string[] SplitReimportAssetPaths(string? assetPathPayload)
            => string.IsNullOrWhiteSpace(assetPathPayload)
                ? Array.Empty<string>()
                : assetPathPayload!.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

        static string GetAssetFileName(string assetPath)
        {
            var normalizedPath = assetPath.Replace('\\', '/').TrimEnd('/');
            var separatorIndex = normalizedPath.LastIndexOf('/');
            return separatorIndex < 0 ? normalizedPath : normalizedPath[(separatorIndex + 1)..];
        }
    }
}
