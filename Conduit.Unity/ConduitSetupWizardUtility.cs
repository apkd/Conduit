#nullable enable

using System;
using System.IO;
using UnityEngine;

namespace Conduit
{
    static partial class ConduitSetupWizardUtility
    {
        internal enum ActionState
        {
            Disabled,
            Enabled,
            Success,
            Error,
            Running,
        }

        internal enum ActionKind
        {
            UpdatePackage,
            DownloadServer,
            ConfigureEditor,
            ConfigureCodexPermissions,
        }

        internal enum ConfigFormat
        {
            Json,
            Toml,
        }

        internal enum ConfigurationLocation
        {
            Project, // zero makes new and pre-existing preference files default to project scope
            User,
        }

        internal sealed class EditorSpec
        {
            internal string Id = string.Empty;
            internal string DisplayName = string.Empty;
            internal string ManualSetupSection = string.Empty;
            internal bool CreateMissingConfig;
            internal ConfigFormat Format;
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
            internal Func<PathContext, string?>? ResolveProjectConfigPath;
            internal Func<PathContext, string?>? ResolveUserConfigPath;
            // ordered candidates preserve alternate existing files; new configurations use the canonical path
            internal Func<PathContext, string?[]>? ResolveProjectConfigPaths;
            internal Func<PathContext, string?[]>? ResolveUserConfigPaths;
        }

        internal struct ButtonModel
        {
            public ActionState State { get; set; }
            public string Label { get; set; }
            public string Hint { get; set; }
            public bool IsOutdated { get; set; }
        }

        internal struct PathContext
        {
            public string ProjectRoot { get; set; }
            public string UserHome { get; set; }
            public string AppData { get; set; }
        }

        internal static bool PathsEqual(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            try
            {
                return string.Equals(
                    Path.GetFullPath(left),
                    Path.GetFullPath(right),
                    Application.platform == RuntimePlatform.WindowsEditor
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal
                );
            }
            catch
            {
                return false;
            }
        }
    }
}
