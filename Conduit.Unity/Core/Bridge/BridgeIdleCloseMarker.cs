#nullable enable

using System.IO;

namespace Conduit
{
    static class BridgeIdleCloseMarker
    {
        internal const string Diagnostic = "Unity was closed after a long idle period. Use the `restart` tool and continue.";

        internal static bool Exists(string projectPath) => File.Exists(GetPath(projectPath));

        internal static void Write(string projectPath)
        {
            string path = GetPath(projectPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
        }

        internal static void Clear(string projectPath) => File.Delete(GetPath(projectPath));

        // store the marker in Library so it survives editor exits and stays outside source control
        static string GetPath(string projectPath) => Path.Combine(projectPath, "Library", "Conduit", "idle-close");
    }
}
