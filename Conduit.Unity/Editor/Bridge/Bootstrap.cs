#nullable enable

using UnityEditor;

namespace Conduit
{
    [InitializeOnLoad]
    static class Bootstrap
    {
        static Bootstrap()
        {
            BridgeLogs.Configure(ConduitSettings.instance.IncludeBackgroundLogs, UnityEngine.Application.consoleLogPath);
            ConduitToolUsage.CompleteRestartFromEnvironment();
            ConduitToolRunner.Initialize();
            ConduitConnection.EnsureStarted();
        }
    }
}
