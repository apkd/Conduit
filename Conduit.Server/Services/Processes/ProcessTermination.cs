using System.Diagnostics;

namespace Conduit;

static class ProcessTermination
{
    internal static void TryKillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch { }
    }
}
