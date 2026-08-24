using System.Text;

namespace Conduit;

static class WindowsPowerShell
{
    internal static string ExecutablePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe"
    );

    internal static string EncodeScript(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}
