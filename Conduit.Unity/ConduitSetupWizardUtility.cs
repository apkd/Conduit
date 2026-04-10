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
            DownloadServer,
            ConfigureEditor,
            ConfigureCodexPermissions,
        }

        internal enum ConfigFormat
        {
            Json,
            Toml,
        }

        internal sealed class EditorSpec
        {
            internal string Id = string.Empty;
            internal string DisplayName = string.Empty;
            internal bool CreateMissingConfig;
            internal ConfigFormat Format;
            internal string BodyPath = string.Empty;
            internal string? TypeValue;
            internal bool? EnabledValue;
            internal bool? DisabledValue;
            internal bool UseCommandArray;
            internal string[] RemoveKeys = Array.Empty<string>();
            internal Func<PathContext, string?> ResolveConfigPath = static _ => null;
            internal Func<PathContext, string?>? ResolveGlobalConfigPath;
        }

        internal struct ButtonModel
        {
            public ActionState State;
            public string Label;
            public string Hint;
        }

        internal struct PathContext
        {
            public string ProjectRoot;
            public string UserHome;
            public string AppData;
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
                        : StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }
    }
}
