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

        internal AssetImportMonitor(Func<BridgeCommandResult, Task> complete)
            => this.complete = complete;

        internal void Start(PendingOperationState operation, BridgeCommandKind commandKind)
        {
            this.operation = operation;
            this.commandKind = commandKind;
            ClearCompilerMessages();

            if (TryCompletePlayModeBlock(operation.CommandType)
                || TryCompleteOpenSceneDiskChangeBlock(operation.CommandType)
                || commandKind == BridgeCommandKind.ReimportAssets && !TryPrepareReimportAssets(operation))
                return;

            InstallHooks();
            ResetSettlementState();
            if (commandKind == BridgeCommandKind.ReimportAssets)
                ReimportResolvedAssets(operation.ReimportAssetPaths);
            else
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            refreshReturned = true;
            TryFinish(countIdleUpdate: false);
        }

        internal void ResumeRestored(PendingOperationState operation, BridgeCommandKind commandKind)
        {
            this.operation = operation;
            this.commandKind = commandKind;
            ClearCompilerMessages();
            InstallHooks();
            refreshReturned = true;
            idleUpdateCount = IdleSettleUpdates;
            TryFinish(countIdleUpdate: false);
        }

        internal void Stop()
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

        internal void ClearCompilerMessages()
            => compilerMessageBuffer.Clear();

        internal static bool ShouldBlockForPlayMode(bool isPlaying)
            => isPlaying;

        internal static string BuildPlayModeDiagnostic(string commandType = BridgeCommandTypes.RefreshAssetDatabase)
            => $"Cannot run '{commandType}' while Unity is in play mode. Use 'editmode' to return to edit mode first.";

        internal static bool ShouldWaitForIdle(bool refreshReturned, bool isCompiling, bool isUpdating, int idleUpdateCount)
            => !refreshReturned
               || isCompiling
               || isUpdating
               || idleUpdateCount < IdleSettleUpdates;

        internal static string BuildRestoredCompileErrorDiagnostic()
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

        internal static string FormatReimportedAssetFilenames(string? assetPathPayload)
            => FormatReimportedAssetFilenames(SplitReimportAssetPaths(assetPathPayload));

        internal static string FormatReimportedAssetFilenames(IReadOnlyList<string>? assetPaths)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
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
                return builder.ToTrimmedString();

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
            var assetPaths = ConduitSearchUtility.ResolveAssetPaths(operation.Target ?? string.Empty);
            if (assetPaths.Length == 0)
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

            operation.ReimportAssetPaths = assetPaths;
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

            var compilerMessages = compilerMessageBuffer.Trim().ToString();
            bool hasCompilerMessages = compilerMessages.Length > 0;
            // restored refreshes miss compilation callbacks that fired before domain reload
            var hasRestoredCompileFailure = operation.IsRestored
                                            && !hasCompilerMessages
                                            && EditorUtility.scriptCompilationFailed;
            _ = complete(
                new()
                {
                    outcome = !hasCompilerMessages && !hasRestoredCompileFailure
                        ? ToolOutcome.Success
                        : ToolOutcome.CompileError,
                    return_value = commandKind == BridgeCommandKind.ReimportAssets
                        ? FormatReimportedAssetFilenames(operation.ReimportAssetPaths)
                        : null,
                    diagnostic = !hasCompilerMessages
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
