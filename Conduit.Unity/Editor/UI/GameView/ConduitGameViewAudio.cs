#nullable enable

using UnityEditor;

namespace Conduit
{
    [InitializeOnLoad]
    static class ConduitGameViewAudio
    {
        // session state survives the domain reloads on both sides of play mode
        const string ActiveStateKey = "Conduit.GameViewAudio.Active";
        const string PreviousMuteStateKey = "Conduit.GameViewAudio.PreviousMute";

        static ConduitGameViewAudio()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += Restore;
        }

        internal static bool IsPrepared => SessionState.GetBool(ActiveStateKey, false);

        internal static void Prepare()
            => Prepare(ConduitSettings.instance.MuteAudioInPlayMode);

        internal static void Prepare(bool enabled)
        {
            if (!enabled || IsPrepared)
                return;

            SessionState.SetBool(PreviousMuteStateKey, EditorUtility.audioMasterMute);
            SessionState.SetBool(ActiveStateKey, true);
            EditorUtility.audioMasterMute = true;
        }

        internal static void RestoreIfInEditMode()
        {
            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
                Restore();
        }

        internal static void Restore()
        {
            if (!IsPrepared)
                return;

            try
            {
                EditorUtility.audioMasterMute = SessionState.GetBool(PreviousMuteStateKey, false);
            }
            finally
            {
                SessionState.EraseBool(ActiveStateKey);
                SessionState.EraseBool(PreviousMuteStateKey);
            }
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
                Restore();
        }
    }
}
