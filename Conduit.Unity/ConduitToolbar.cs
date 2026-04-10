#nullable enable

#if UNITY_6000_3_OR_NEWER
using JetBrains.Annotations;
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;

namespace Conduit
{
    static class ConduitToolbar
    {
        const string toolbarElementPath = "Conduit/Bridge Status";
        static MainToolbarLabel? label;

        [UsedImplicitly]
        [MainToolbarElement(toolbarElementPath, defaultDockPosition = MainToolbarDockPosition.Left)]
        static MainToolbarElement CreateStatusLabel()
        {
            label ??= new(BuildContent());
            label.content = BuildContent();
            return label;
        }

        internal static void Refresh()
        {
            if (label != null)
                label.content = BuildContent();

            MainToolbar.Refresh(toolbarElementPath);
        }

        static MainToolbarContent BuildContent()
            => ConduitConnection.GetConnectionStatus() switch
            {
                ConduitConnectionStatus.Connected => new(
                    image: GetIcon("Collab.BuildSucceeded"),
                    tooltip: "Unity Conduit bridge is connected or was attached within the last hour."
                ),
                _ => new(
                    image: GetIcon("Collab.BuildFailed"),
                    tooltip: "Unity Conduit bridge waiting for an MCP client connection."
                ),
            };

        static Texture2D GetIcon(string iconName)
            => (Texture2D)EditorGUIUtility.IconContent(iconName).image;
    }
}
#endif
