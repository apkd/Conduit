#nullable enable

using UnityEditor;

namespace Conduit
{
    // snippet retention is a per-user choice and must not modify project settings
    static class ConduitSnippetStorage
    {
        internal const string PreservePreferenceKey = "Conduit.PreserveSnippets";

        internal static bool PreserveSnippets
        {
            get => EditorPrefs.GetBool(PreservePreferenceKey, false);
            set
            {
                if (PreserveSnippets == value)
                    return;

                EditorPrefs.SetBool(PreservePreferenceKey, value);
                ConduitConnection.RefreshClientHandshakes();
            }
        }
    }
}
