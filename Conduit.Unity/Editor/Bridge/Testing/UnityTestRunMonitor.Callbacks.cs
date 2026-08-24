#nullable enable

using System;
using UnityEditor.TestTools.TestRunner.Api;

namespace Conduit
{
    sealed partial class UnityTestRunMonitor
    {
        sealed class TestRunCallbacks : IErrorCallbacks
        {
            readonly UnityTestRunMonitor owner;

            public TestRunCallbacks(UnityTestRunMonitor owner)
                => this.owner = owner;

            public void RunStarted(ITestAdaptor testsToRun) { }
            public void RunFinished(ITestResultAdaptor result) => owner.HandleRunFinished(result);
            public void TestStarted(ITestAdaptor test) => owner.HandleTestStarted(test);
            public void TestFinished(ITestResultAdaptor result) => owner.HandleTestFinished(result);
            public void OnError(string message) => owner.HandleRunError(message);
        }

        sealed class PlayerTestRunSettings : ITestRunSettings
        {
            const string TestPlayerEnvironmentVariable = "CONDUIT_TEST_PLAYER";
            const string VideoDriverEnvironmentVariable = "SDL_VIDEODRIVER";
            const string XInput2EnvironmentVariable = "SDL_VIDEO_X11_XINPUT2";
            readonly bool configureLinuxDisplay;
            string? previousTestPlayer;
            string? previousVideoDriver;
            string? previousXInput2;

            public PlayerTestRunSettings(bool configureLinuxDisplay)
                => this.configureLinuxDisplay = configureLinuxDisplay;

            public void Apply()
            {
                previousTestPlayer = Environment.GetEnvironmentVariable(TestPlayerEnvironmentVariable);
                // inherited by the launched process so the server never shuts down an unrelated development player
                Environment.SetEnvironmentVariable(TestPlayerEnvironmentVariable, "1");
                if (!configureLinuxDisplay)
                    return;

                previousVideoDriver = Environment.GetEnvironmentVariable(VideoDriverEnvironmentVariable);
                previousXInput2 = Environment.GetEnvironmentVariable(XInput2EnvironmentVariable);
                // Unity's bundled SDL crashes in the XInput2 touch path under XWayland; prefer native Wayland there.
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                    Environment.SetEnvironmentVariable(VideoDriverEnvironmentVariable, "wayland");
                Environment.SetEnvironmentVariable(XInput2EnvironmentVariable, "0");
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(TestPlayerEnvironmentVariable, previousTestPlayer);
                if (!configureLinuxDisplay)
                    return;

                Environment.SetEnvironmentVariable(VideoDriverEnvironmentVariable, previousVideoDriver);
                Environment.SetEnvironmentVariable(XInput2EnvironmentVariable, previousXInput2);
            }
        }
    }
}
