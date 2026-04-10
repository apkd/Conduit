#nullable enable

using UnityEditor;
using UnityEngine;

namespace Conduit
{
    [FilePath("UserSettings/ConduitSetupWizardState.asset", FilePathAttribute.Location.ProjectFolder)]
    sealed class ConduitSetupWizardState : ScriptableSingleton<ConduitSetupWizardState>
    {
        [SerializeField] string selectedEditorId = string.Empty;
        [SerializeField] string serverExecutablePath = string.Empty;

        public string SelectedEditorId => selectedEditorId;
        public string ServerExecutablePath => serverExecutablePath;

        public void SetSelectedEditorId(string value)
        {
            if (selectedEditorId == value)
                return;

            selectedEditorId = value;
            Save(true);
        }

        public void SetServerExecutablePath(string value)
        {
            if (serverExecutablePath == value)
                return;

            serverExecutablePath = value;
            Save(true);
        }
    }
}
