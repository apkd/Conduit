using JetBrains.Annotations;

namespace Conduit;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class StackTraceSimplificationTests
{
    [Test]
    public async Task SimplifyStackTraceRestoresAsyncLocalFunction()
    {
        const string stackTrace = """
            UnityEngine.Logger:Log
            HK.Analytics/<<EnsureInitialized>g__InitializeAsync|17_0>d:MoveNext ()
            System.Runtime.CompilerServices.AsyncVoidMethodBuilder:Start<HK.Analytics/<<EnsureInitialized>g__InitializeAsync|17_0>d>
            HK.Analytics:<EnsureInitialized>g__InitializeAsync|17_0
            HK.Analytics:EnsureInitialized ()
            HK.Analytics:Bootstrap ()
            """;

        const string expected = """
            UnityEngine.Logger:Log
            HK.Analytics:EnsureInitialized.InitializeAsync
            HK.Analytics:EnsureInitialized
            HK.Analytics:Bootstrap
            """;

        await Assert.That(ConduitUtility.SimplifyStackTrace(stackTrace)).IsEqualTo(expected);
    }

    [Test]
    public async Task SimplifyStackTracePreservesRecursionAndUnknownGeneratedFrames()
    {
        const string stackTrace = """
            Game.Loader/<Load>d__4:MoveNext () [0x00000] in /project/Loader.cs:line 42
            at System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<System.Boolean>:Start<Game.Loader/<Load>d__4>
            Game.Loader:Load ()
            Game.Loader:Load ()
            Game.Loader:<Load>g__Validate|4_1 ()
            Game.Loader/<>c/<<Load>b__4_0>d:MoveNext ()
            """;

        const string expected = """
            Game.Loader:Load (Loader.cs:42)
            Game.Loader:Load
            Game.Loader:Load.Validate
            Game.Loader/<>c/<<Load>b__4_0>d:MoveNext
            """;

        await Assert.That(ConduitUtility.SimplifyStackTrace(stackTrace)).IsEqualTo(expected);
    }
}
