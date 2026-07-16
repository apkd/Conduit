#nullable enable

using UnityEditor;
using UnityEngine;

namespace Conduit
{
    // setup choices are per developer and must never create ProjectSettings or source-control changes
    [FilePath("Conduit/Settings.asset", FilePathAttribute.Location.PreferencesFolder)]
    sealed class ConduitSettings : ScriptableSingleton<ConduitSettings>
    {
        [SerializeField] bool unfocusedGameView;
        [SerializeField] string selectedEditorId = string.Empty;
        [SerializeField] string serverExecutablePath = string.Empty;
        [SerializeField] ConduitSetupWizardUtility.ConfigurationLocation configurationLocation;

        internal bool UnfocusedGameView => unfocusedGameView;
        internal string SelectedEditorId => selectedEditorId;
        internal string ServerExecutablePath => serverExecutablePath;
        internal ConduitSetupWizardUtility.ConfigurationLocation ConfigurationLocation
            => configurationLocation;

        internal void SetUnfocusedGameView(bool value)
        {
            if (unfocusedGameView == value)
                return;

            unfocusedGameView = value;
            Save(true);
        }

        internal void SetSelectedEditorId(string value)
        {
            if (selectedEditorId == value)
                return;

            selectedEditorId = value;
            Save(true);
        }

        internal void SetServerExecutablePath(string value)
        {
            if (serverExecutablePath == value)
                return;

            serverExecutablePath = value;
            Save(true);
        }

        internal void SetConfigurationLocation(
            ConduitSetupWizardUtility.ConfigurationLocation value
        )
        {
            if (configurationLocation == value)
                return;

            configurationLocation = value;
            Save(true);
        }
    }
}
