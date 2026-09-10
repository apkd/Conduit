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
        [SerializeField] bool lowResolutionPlayMode;
        [SerializeField] bool muteAudioInPlayMode;
        [SerializeField] bool includeBackgroundLogs;
        [SerializeField] bool automaticallyCloseWhenIdle;
        [SerializeField] float idleCloseMinutes = 15f;
        [SerializeField] string selectedEditorId = string.Empty;
        [SerializeField] string serverExecutablePath = string.Empty;
        [SerializeField] SetupConfigurationLocation configurationLocation;

        internal bool UnfocusedGameView => unfocusedGameView;
        internal bool LowResolutionPlayMode => lowResolutionPlayMode;
        internal bool MuteAudioInPlayMode => muteAudioInPlayMode;
        internal bool IncludeBackgroundLogs => includeBackgroundLogs;
        internal bool AutomaticallyCloseWhenIdle => automaticallyCloseWhenIdle;
        internal float IdleCloseMinutes => idleCloseMinutes;
        internal string SelectedEditorId => selectedEditorId;
        internal string ServerExecutablePath => serverExecutablePath;
        internal SetupConfigurationLocation ConfigurationLocation
            => configurationLocation;

        internal void SetUnfocusedGameView(bool value)
        {
            if (unfocusedGameView == value)
                return;

            unfocusedGameView = value;
            Save(true);
        }

        internal void SetIncludeBackgroundLogs(bool value)
        {
            if (includeBackgroundLogs == value)
                return;

            includeBackgroundLogs = value;
            BridgeLogs.Configure(value);
            Save(true);
        }

        internal void SetLowResolutionPlayMode(bool value)
        {
            if (lowResolutionPlayMode == value)
                return;

            lowResolutionPlayMode = value;
            Save(true);
        }

        internal void SetMuteAudioInPlayMode(bool value)
        {
            if (muteAudioInPlayMode == value)
                return;

            muteAudioInPlayMode = value;
            Save(true);
        }

        internal void SetSelectedEditorId(string value)
        {
            if (selectedEditorId == value)
                return;

            selectedEditorId = value;
            Save(true);
        }

        internal void SetAutomaticallyCloseWhenIdle(bool value)
        {
            if (automaticallyCloseWhenIdle == value)
                return;

            automaticallyCloseWhenIdle = value;
            Save(true);
        }

        internal void SetIdleCloseMinutes(float value)
        {
            if (!(value > 0f) || float.IsInfinity(value) || idleCloseMinutes == value)
                return;

            idleCloseMinutes = value;
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
            SetupConfigurationLocation value
        )
        {
            if (configurationLocation == value)
                return;

            configurationLocation = value;
            Save(true);
        }
    }
}
