#nullable enable

#if MODULE_IMGUI
using System;

namespace Conduit
{
    static class ConduitSettingsSelection
    {
        internal static string GetConfiguredExecutablePath(
            EditorClientSpec[] specs,
            ConduitSettings settings
        )
        {
            var location = GetConfigurationLocation(specs, settings);
            if (settings.SelectedEditorId.Length > 0)
            {
                var spec = GetSelectedSpec(specs, settings.SelectedEditorId);
                if (EditorConfiguration.TryGetConfiguredExecutablePath(
                        spec,
                        location,
                        out var scopedExecutablePath,
                        out _
                    ))
                    return scopedExecutablePath;
            }

            return EditorConfiguration.TryGetAnyConfiguredExecutablePath(
                location,
                out var executablePath,
                out _
            )
                ? executablePath
                : string.Empty;
        }

        internal static EditorClientSpec GetSelectedSpec(
            EditorClientSpec[] specs,
            string selectedId
        )
        {
            foreach (var spec in specs)
                if (spec.Id == selectedId)
                    return spec;

            throw new InvalidOperationException($"Unknown editor '{selectedId}'.");
        }

        internal static SetupConfigurationLocation GetConfigurationLocation(
            EditorClientSpec spec,
            ConduitSettings settings
        )
            => EditorConfigurationPaths.SupportsProjectConfiguration(spec)
                ? settings.ConfigurationLocation
                : SetupConfigurationLocation.User;

        internal static SetupConfigurationLocation GetConfigurationLocation(
            EditorClientSpec[] specs,
            ConduitSettings settings
        )
            => settings.SelectedEditorId.Length == 0
                ? settings.ConfigurationLocation
                : GetConfigurationLocation(
                    GetSelectedSpec(specs, settings.SelectedEditorId),
                    settings
                );
    }
}
#endif
