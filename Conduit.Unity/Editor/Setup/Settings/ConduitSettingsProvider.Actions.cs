#nullable enable

#if MODULE_IMGUI
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Conduit
{
    sealed partial class ConduitSettingsProvider : SettingsProvider
    {
        SetupButtonModel EvaluatePackageUpdateButton()
        {
            static string ShortHash(string hash) => hash.Length > 8 ? hash[..8] : hash;

            if (runningAction == SetupActionKind.UpdatePackage)
                return new()
                {
                    State = SetupActionState.Running,
                    Label = "Updating the Unity package...",
                    Hint = "Unity Package Manager is updating Conduit from the release branch.",
                    IsOutdated = true,
                };

            if (HasError(SetupActionKind.UpdatePackage))
                return new()
                {
                    State = SetupActionState.Error,
                    Label = "Update the Unity package",
                    Hint = "Unity Package Manager could not update Conduit. Check the Console for the full error.",
                    IsOutdated = true,
                };

            return new()
            {
                State = SetupActionState.Enabled,
                Label = "Update the Unity package",
                Hint =
                    $"The installed package commit {ShortHash(packageUpdateStatus.InstalledHash)} is older than " +
                    $"the release commit {ShortHash(packageUpdateStatus.LatestHash)}. " +
                    "Update it through Unity Package Manager.",
                IsOutdated = true,
            };
        }

        void DrawButton(
            SetupActionKind actionKind,
            SetupButtonModel model,
            Func<System.Threading.Tasks.Task> callback
        )
        {
            EditorGUILayout.BeginVertical(GUI.skin.box);
            using (new EditorGUI.DisabledScope(model.State is not SetupActionState.Enabled))
            {
                var previousColor = GUI.backgroundColor;
                GUI.backgroundColor = model.State switch
                {
                    SetupActionState.Success => successColor,
                    SetupActionState.Error => errorColor,
                    SetupActionState.Enabled => enabledColor,
                    _ => GUI.backgroundColor,
                };
                if (GUILayout.Button(model.Label, GUILayout.Height(28f)))
                    RunAction(actionKind, callback);
                GUI.backgroundColor = previousColor;
            }

            if (model.Hint.Length > 0)
            {
                hintStyle ??= new(EditorStyles.wordWrappedMiniLabel) { richText = true };
                EditorGUILayout.LabelField(
                    ConduitManualSetupInstructions.FormatInlineCode(model.Hint),
                    hintStyle
                );
            }

            EditorGUILayout.EndVertical();
        }

        async void RunAction(
            SetupActionKind actionKind,
            Func<System.Threading.Tasks.Task> callback
        )
        {
            if (runningAction is not null)
                return;

            runningAction = actionKind;
            actionErrors.Remove(actionKind);
            SettingsService.RepaintAllSettingsWindow();

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
                RefreshInstalledEditors(EditorClientCatalog.GetEditorSpecs());
                SettingsService.RepaintAllSettingsWindow();
            }
        }

        async void CheckPackageUpdateAsync(int currentActivation)
        {
            var status = await ConduitPackageUpdater.CheckAsync();
            if (currentActivation != activationVersion)
                return;

            packageUpdateStatus = status;
            SettingsService.RepaintAllSettingsWindow();
        }

        bool HasError(SetupActionKind actionKind)
            => actionErrors.TryGetValue(actionKind, out var hasError) && hasError;

        void RefreshInstalledEditors(EditorClientSpec[] specs)
        {
            installedEditorIds.Clear();
            editorLabels.Clear();
            foreach (var spec in specs)
            {
                var installed = EditorConfigurationPaths.HasUserConfigurationFile(spec);
                if (installed)
                    installedEditorIds.Add(spec.Id);

                editorLabels[spec.Id] = installed
                    ? $"{spec.DisplayName} (installed)"
                    : spec.DisplayName;
            }
        }

    }
}
#endif
