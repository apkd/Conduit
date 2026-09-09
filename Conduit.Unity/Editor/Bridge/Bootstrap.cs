#nullable enable

using System;
using UnityEditor;

namespace Conduit
{
    [InitializeOnLoad]
    static class Bootstrap
    {
        static Bootstrap()
        {
            // restart tracking consumes this launch marker, so establish ownership first
            ConduitEditorIdleClose.Initialize(
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ConduitToolUsage.RestartStartedUtcTicksEnvironmentVariable))
                    ? EditorIdleOwnership.Agent
                    : EditorIdleOwnership.User
            );
            ConduitToolUsage.CompleteRestartFromEnvironment();
            ConduitToolRunner.Initialize();
            ConduitConnection.EnsureStarted();
        }
    }
}
