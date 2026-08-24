namespace Conduit;

public sealed partial class UnityProjectEnvironmentProbeTests
{
    [Test]
    [Arguments("Enter Safe Mode?", true)]
    [Arguments("UNITY - SAFE MODE", true)]
    [Arguments("Exit Safe Mode", true)]
    [Arguments("Unity", false)]
    [Arguments("", false)]
    public async Task SafeModeWindowTitleRecognizesSafeModeText(string title, bool expected)
    {
        await Assert.That(SafeModeWindowProbe.IsSafeModeWindowTitle(title)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("The following open scene(s) have been changed on disk:\nAssets/Scenes/Foo.unity\n\nDo you want to reload the scene(s)?", true)]
    [Arguments("The open scene(s) have been modified externally. Reload?", true)]
    [Arguments("Reload assemblies?", false)]
    [Arguments("Open project", false)]
    [Arguments("", false)]
    public async Task SceneReloadPromptTextRecognizesChangedOpenSceneDialog(string text, bool expected)
    {
        await Assert.That(UnitySceneReloadPromptRecovery.IsSceneReloadPromptText(text)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("Importing", true)]
    [Arguments("Importing (iteration 1) Assets", true)]
    [Arguments("Reloading Domain", true)]
    [Arguments("Hold on...", true)]
    [Arguments("Running managed callbacks", true)]
    [Arguments("Script preprocess", true)]
    [Arguments("Script preprocessing assemblies", true)]
    [Arguments("Compiling shader", true)]
    [Arguments("Compiling Scripts", true)]
    [Arguments("Package Manager", true)]
    [Arguments("Running BuildProgram", true)]
    [Arguments("Compiling C# scripts", true)]
    [Arguments("Postprocessing IL assemblies", true)]
    [Arguments("Opening scene", true)]
    [Arguments("Importing Assets", true)]
    [Arguments("Reload assemblies?", false)]
    [Arguments("", false)]
    public async Task ProgressWindowTitleRecognizesTemporaryEditorFreezeTitles(string title, bool expected)
    {
        await Assert.That(UnityWindowTitleClassifier.IsProgressTitle(title)).IsEqualTo(expected);
    }

    [Test]
    public async Task HyprlandSafeModeSignalMatchesTargetPidTitle()
    {
        const string json =
            """
            [
              { "pid": 1234, "class": "Unity", "title": "Unity", "initialTitle": "Unity" },
              { "pid": 1234, "class": "Unity", "title": "Enter Safe Mode?", "initialTitle": "Enter Safe Mode?" },
              { "pid": 5678, "class": "Unity", "title": "Enter Safe Mode?", "initialTitle": "Enter Safe Mode?" }
            ]
            """;

        var title = SafeModeWindowProbe.TryReadHyprlandClientsSafeModeWindowSignal(json, 1234);

        await Assert.That(title).IsEqualTo("Enter Safe Mode?");
    }

    [Test]
    public async Task HyprlandSafeModeSignalIgnoresOtherPids()
    {
        const string json =
            """
            [
              { "pid": 5678, "class": "Unity", "title": "Enter Safe Mode?", "initialTitle": "Enter Safe Mode?" }
            ]
            """;

        var title = SafeModeWindowProbe.TryReadHyprlandClientsSafeModeWindowSignal(json, 1234);

        await Assert.That(title).IsNull();
    }

    [Test]
    public async Task HyprlandSafeModeSignalUsesInitialTitle()
    {
        const string json =
            """
            [
              { "pid": 1234, "class": "Unity", "title": "Unity", "initialTitle": "Enter Safe Mode?" }
            ]
            """;

        var title = SafeModeWindowProbe.TryReadHyprlandClientsSafeModeWindowSignal(json, 1234);

        await Assert.That(title).IsEqualTo("Enter Safe Mode?");
    }

    [Test]
    public async Task SwaySafeModeSignalMatchesTargetPidName()
    {
        const string json =
            """
            {
              "nodes": [
                { "pid": 5678, "name": "Enter Safe Mode?" },
                {
                  "nodes": [
                    { "pid": 1234, "name": "Enter Safe Mode?" }
                  ]
                }
              ]
            }
            """;

        var title = SafeModeWindowProbe.TryReadSwayTreeSafeModeWindowSignal(json, 1234);

        await Assert.That(title).IsEqualTo("Enter Safe Mode?");
    }

    [Test]
    public async Task SwaySafeModeSignalMatchesFloatingWindowPropertyTitle()
    {
        const string json =
            """
            {
              "nodes": [],
              "floating_nodes": [
                {
                  "pid": 1234,
                  "name": "Unity",
                  "window_properties": {
                    "title": "Enter Safe Mode?",
                    "class": "Unity"
                  }
                }
              ]
            }
            """;

        var title = SafeModeWindowProbe.TryReadSwayTreeSafeModeWindowSignal(json, 1234);

        await Assert.That(title).IsEqualTo("Enter Safe Mode?");
    }

    [Test]
    public async Task SwaySafeModeSignalIgnoresOtherPids()
    {
        const string json =
            """
            {
              "nodes": [
                { "pid": 5678, "name": "Enter Safe Mode?" }
              ]
            }
            """;

        var title = SafeModeWindowProbe.TryReadSwayTreeSafeModeWindowSignal(json, 1234);

        await Assert.That(title).IsNull();
    }

    [Test]
    public async Task NiriSafeModeSignalMatchesTargetPidTitle()
    {
        const string json =
            """
            [
              { "id": 1, "pid": 5678, "title": "Enter Safe Mode?", "app_id": "Unity" },
              { "id": 2, "pid": 1234, "title": "Enter Safe Mode?", "app_id": "Unity" }
            ]
            """;

        var title = SafeModeWindowProbe.TryReadNiriWindowsSafeModeWindowSignal(json, 1234);

        await Assert.That(title).IsEqualTo("Enter Safe Mode?");
    }

    [Test]
    public async Task NiriSafeModeSignalHandlesRawSocketResponse()
    {
        const string json =
            """
            {
              "Ok": {
                "Windows": [
                  { "id": 2, "pid": 1234, "title": "Enter Safe Mode?", "app_id": "Unity" }
                ]
              }
            }
            """;

        var title = SafeModeWindowProbe.TryReadNiriWindowsSafeModeWindowSignal(json, 1234);

        await Assert.That(title).IsEqualTo("Enter Safe Mode?");
    }

    [Test]
    public async Task NiriSafeModeSignalIgnoresOtherPids()
    {
        const string json =
            """
            [
              { "id": 1, "pid": 5678, "title": "Enter Safe Mode?", "app_id": "Unity" }
            ]
            """;

        var title = SafeModeWindowProbe.TryReadNiriWindowsSafeModeWindowSignal(json, 1234);

        await Assert.That(title).IsNull();
    }

}
