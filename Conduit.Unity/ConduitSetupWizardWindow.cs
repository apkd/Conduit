#nullable enable

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Conduit
{
    sealed class ConduitSetupWizardWindow : EditorWindow
    {
        static readonly Color successColor = new(0.45f, 0.8f, 0.45f);
        static readonly Color errorColor = new(0.85f, 0.45f, 0.45f);
        static readonly Color enabledColor = new(0.8f, 0.8f, 0.8f);

        readonly Dictionary<ConduitSetupWizardUtility.ActionKind, bool> actionErrors = new();
        ConduitSetupWizardUtility.ActionKind? runningAction;

        [MenuItem("Tools/Conduit/Setup MCP Server")]
        static void Open()
            => GetWindow<ConduitSetupWizardWindow>("Conduit Setup");

        void OnEnable()
        {
            minSize = new Vector2(480f, 280f);

            var state = ConduitSetupWizardState.instance;
            if (state.SelectedEditorId.Length > 0)
                return;

            var detectedEditorId = ConduitSetupWizardUtility.DetectInstalledEditorId();
            if (detectedEditorId.Length > 0)
                state.SetSelectedEditorId(detectedEditorId);
        }

        void OnGUI()
        {
            var state = ConduitSetupWizardState.instance;
            var specs = ConduitSetupWizardUtility.GetEditorSpecs();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Conduit MCP Setup", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Download the MCP server executable and configure your code editor to talk to Unity. " +
                "Green buttons are already applied. Red buttons failed on the previous attempt and logged the error to the Console.",
                MessageType.Info);

            DrawSelectedEditor(specs, state);
            DrawPaths(specs, state);
            DrawButtons(specs, state);
        }

        void DrawSelectedEditor(ConduitSetupWizardUtility.EditorSpec[] specs, ConduitSetupWizardState state)
        {
            var selectedIndex = 0;
            for (var index = 0; index < specs.Length; index++)
                if (specs[index].Id == state.SelectedEditorId)
                    selectedIndex = index + 1;

            var options = new string[specs.Length + 1];
            options[0] = "Select code editor...";
            for (var index = 0; index < specs.Length; index++)
                options[index + 1] = specs[index].DisplayName;

            EditorGUI.BeginChangeCheck();
            selectedIndex = EditorGUILayout.Popup("Code Editor", selectedIndex, options);
            if (!EditorGUI.EndChangeCheck())
                return;

            state.SetSelectedEditorId(selectedIndex == 0 ? string.Empty : specs[selectedIndex - 1].Id);
            actionErrors.Clear();
            Repaint();
        }

        void DrawPaths(ConduitSetupWizardUtility.EditorSpec[] specs, ConduitSetupWizardState state)
        {
            var configuredExecutablePath = GetConfiguredExecutablePath(specs, state.SelectedEditorId);
            var effectiveExecutablePath = ConduitSetupWizardUtility.GetEffectiveExecutablePath(state.ServerExecutablePath, configuredExecutablePath);
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.TextField("Server Executable", effectiveExecutablePath.Length == 0 ? "<not set>" : effectiveExecutablePath);

            if (state.SelectedEditorId.Length == 0)
                return;

            var spec = GetSelectedSpec(specs, state.SelectedEditorId);
            var configPath = ConduitSetupWizardUtility.GetDisplayConfigPath(spec) ?? "<unsupported on current OS>";
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.TextField("Config Path", configPath);
        }

        void DrawButtons(ConduitSetupWizardUtility.EditorSpec[] specs, ConduitSetupWizardState state)
        {
            var configuredExecutablePath = GetConfiguredExecutablePath(specs, state.SelectedEditorId);
            var effectiveExecutablePath = ConduitSetupWizardUtility.GetEffectiveExecutablePath(state.ServerExecutablePath, configuredExecutablePath);

            EditorGUILayout.Space();
            DrawButton(
                ConduitSetupWizardUtility.ActionKind.DownloadServer,
                ConduitSetupWizardUtility.EvaluateDownloadButton(
                    state.ServerExecutablePath,
                    configuredExecutablePath,
                    runningAction == ConduitSetupWizardUtility.ActionKind.DownloadServer,
                    HasError(ConduitSetupWizardUtility.ActionKind.DownloadServer)),
                async () =>
                {
                    var executablePath = await ConduitSetupWizardUtility.DownloadServerAsync();
                    state.SetServerExecutablePath(executablePath);
                });

            if (state.SelectedEditorId.Length == 0)
                return;

            var spec = GetSelectedSpec(specs, state.SelectedEditorId);

            DrawButton(
                ConduitSetupWizardUtility.ActionKind.ConfigureEditor,
                ConduitSetupWizardUtility.EvaluateConfigureButton(
                    spec,
                    effectiveExecutablePath,
                    runningAction == ConduitSetupWizardUtility.ActionKind.ConfigureEditor,
                    HasError(ConduitSetupWizardUtility.ActionKind.ConfigureEditor)),
                () =>
                {
                    ConduitSetupWizardUtility.ConfigureEditor(spec, effectiveExecutablePath);
                    return System.Threading.Tasks.Task.CompletedTask;
                });

            if (spec.Id != "codex")
                return;

            DrawButton(
                ConduitSetupWizardUtility.ActionKind.ConfigureCodexPermissions,
                ConduitSetupWizardUtility.EvaluateCodexPermissionsButton(
                    effectiveExecutablePath,
                    runningAction == ConduitSetupWizardUtility.ActionKind.ConfigureCodexPermissions,
                    HasError(ConduitSetupWizardUtility.ActionKind.ConfigureCodexPermissions)),
                () =>
                {
                    ConduitSetupWizardUtility.ConfigureCodexPermissions();
                    return System.Threading.Tasks.Task.CompletedTask;
                });
        }

        void DrawButton(ConduitSetupWizardUtility.ActionKind actionKind, ConduitSetupWizardUtility.ButtonModel model, Func<System.Threading.Tasks.Task> callback)
        {
            EditorGUILayout.BeginVertical(GUI.skin.box);
            using (new EditorGUI.DisabledScope(model.State is not ConduitSetupWizardUtility.ActionState.Enabled))
            {
                var previousColor = GUI.backgroundColor;
                GUI.backgroundColor = GetButtonColor(model.State);
                if (GUILayout.Button(model.Label, GUILayout.Height(28f)))
                    RunAction(actionKind, callback);
                GUI.backgroundColor = previousColor;
            }

            if (model.Hint.Length > 0)
                EditorGUILayout.LabelField(model.Hint, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.EndVertical();
        }

        async void RunAction(ConduitSetupWizardUtility.ActionKind actionKind, Func<System.Threading.Tasks.Task> callback)
        {
            if (runningAction != null)
                return;

            runningAction = actionKind;
            actionErrors.Remove(actionKind);
            Repaint();

            try
            {
                await callback();
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                actionErrors[actionKind] = true;
                Debug.LogException(exception);
            }
            finally
            {
                runningAction = null;
                Repaint();
            }
        }

        bool HasError(ConduitSetupWizardUtility.ActionKind actionKind)
            => actionErrors.TryGetValue(actionKind, out var hasError) && hasError;

        static string GetConfiguredExecutablePath(ConduitSetupWizardUtility.EditorSpec[] specs, string selectedEditorId)
        {
            if (selectedEditorId.Length == 0)
                return string.Empty;

            var spec = GetSelectedSpec(specs, selectedEditorId);
            return ConduitSetupWizardUtility.TryGetConfiguredExecutablePath(spec, out var executablePath, out _)
                ? executablePath
                : string.Empty;
        }

        static ConduitSetupWizardUtility.EditorSpec GetSelectedSpec(ConduitSetupWizardUtility.EditorSpec[] specs, string selectedId)
        {
            for (var index = 0; index < specs.Length; index++)
                if (specs[index].Id == selectedId)
                    return specs[index];

            throw new InvalidOperationException($"Unknown editor '{selectedId}'.");
        }

        static Color GetButtonColor(ConduitSetupWizardUtility.ActionState state)
            => state switch
            {
                ConduitSetupWizardUtility.ActionState.Success => successColor,
                ConduitSetupWizardUtility.ActionState.Error => errorColor,
                ConduitSetupWizardUtility.ActionState.Enabled => enabledColor,
                _ => GUI.backgroundColor,
            };
    }
}
