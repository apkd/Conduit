#nullable enable

namespace Conduit
{
    static class ProjectSettingsTool
    {
        internal static string Execute(PendingOperationState operation)
            => ProjectSettingsOperations.Execute(operation);

        internal static string Execute(
            PendingOperationState operation,
            ProjectSettingsRegistry registry)
            => ProjectSettingsOperations.Execute(operation, registry);
    }
}
