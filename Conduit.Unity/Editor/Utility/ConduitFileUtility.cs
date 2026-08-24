#nullable enable

using System.IO;

namespace Conduit
{
    static class ConduitFileUtility
    {
        internal static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch { }
        }
    }
}
