#nullable enable

using UnityEditor;

namespace Conduit
{
    [InitializeOnLoad]
    static class Bootstrap
    {
        static Bootstrap()
        {
            ConduitToolRunner.Initialize();
            ConduitConnection.EnsureStarted();
        }
    }
}
