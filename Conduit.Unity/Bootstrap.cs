#nullable enable

using UnityEditor;

namespace Conduit
{
    [InitializeOnLoad]
    static class Bootstrap
    {
        static Bootstrap()
        {
            ConduitToolUsage.CompleteRestartFromEnvironment();
            ConduitToolRunner.Initialize();
            ConduitConnection.EnsureStarted();
        }
    }
}
