#nullable enable

using System;

namespace Conduit
{
    enum SetupActionState
    {
        Disabled,
        Enabled,
        Success,
        Error,
        Running,
    }

    enum SetupActionKind
    {
        UpdatePackage,
        DownloadServer,
        ConfigureEditor,
        ConfigureCodexPermissions,
    }

    enum EditorConfigurationFormat
    {
        Json,
        Toml,
    }

    enum SetupConfigurationLocation
    {
        Project, // zero makes new and pre-existing preference files default to project scope
        User,
    }

    sealed class EditorClientSpec
    {
        internal string Id = string.Empty;
        internal string DisplayName = string.Empty;
        internal string ManualSetupSection = string.Empty;
        internal bool CreateMissingConfig;
        internal EditorConfigurationFormat Format;
        internal string BodyPath = string.Empty;
        internal string? TypeValue;
        internal bool? EnabledValue;
        internal bool? DisabledValue;
        internal bool UseCommandArray;
        // read compatibility accepts older omitted fields, while writes always emit the current schema
        internal bool TypeOptionalWhenReading;
        internal bool StateOptionalWhenReading;
        internal bool IncludeAllTools;
        // lossy formats and ambiguous client paths must stop auto-configuration rather than guess
        internal bool CreateOnlyConfig;
        internal bool RequireUnambiguousConfigPath;
        internal string[] RemoveKeys = Array.Empty<string>();
        internal Func<SetupPathContext, string?>? ResolveProjectConfigPath;
        internal Func<SetupPathContext, string?>? ResolveUserConfigPath;
        // ordered candidates preserve alternate existing files; new configurations use the canonical path
        internal Func<SetupPathContext, string?[]>? ResolveProjectConfigPaths;
        internal Func<SetupPathContext, string?[]>? ResolveUserConfigPaths;
    }

    struct SetupButtonModel
    {
        internal SetupActionState State { get; set; }
        internal string Label { get; set; }
        internal string Hint { get; set; }
        internal bool IsOutdated { get; set; }
    }

    readonly struct SetupPathContext
    {
        internal SetupPathContext(string projectRoot, string userHome, string appData)
        {
            ProjectRoot = projectRoot;
            UserHome = userHome;
            AppData = appData;
        }

        internal string ProjectRoot { get; }
        internal string UserHome { get; }
        internal string AppData { get; }
    }
}
