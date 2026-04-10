using static Conduit.BridgeCommandKind;

namespace Conduit;

static class UnityToolTimeouts
{
    public static readonly TimeSpan StatusCommand = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan StatusWithoutKnownProcess = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan RefreshAssetDatabaseActivation = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RefreshAssetDatabaseRecoveryPollInterval = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan RestartShutdownGracePeriod = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RestartShutdownKillWait = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RestartStartupWindow = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan RestartStartupMax = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan RestartStartupPollInterval = TimeSpan.FromSeconds(2);

    public static TimeSpan ForCommand(BridgeCommandKind commandKind) =>
        commandKind switch
        {
            Play                 => TimeSpan.FromSeconds(60),
            Screenshot           => TimeSpan.FromSeconds(90),
            GetDependencies      => TimeSpan.FromMinutes(1),
            FindReferencesTo     => TimeSpan.FromMinutes(10),
            FindMissingScripts   => TimeSpan.FromMinutes(10),
            Show                 => TimeSpan.FromSeconds(90),
            Search               => TimeSpan.FromSeconds(90),
            ToJson               => TimeSpan.FromSeconds(15),
            FromJsonOverwrite    => TimeSpan.FromSeconds(15),
            SaveScenes           => TimeSpan.FromSeconds(90),
            DiscardScenes        => TimeSpan.FromSeconds(40),
            RefreshAssetDatabase => TimeSpan.FromMinutes(10),
            ExecuteCode          => TimeSpan.FromMinutes(10),
            RunTestsEditMode     => TimeSpan.FromMinutes(10),
            RunTestsPlayMode     => TimeSpan.FromMinutes(20),
            RunTestsPlayer       => TimeSpan.FromMinutes(30),
            _                    => StatusCommand,
        };
}
