#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NUnit.Framework;
using Conduit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public sealed partial class ConduitMcpToolsTests
{
    [Test]
    public void ClientWorkSnapshot_TracksOutstandingActiveAndQueuedOperations()
    {
        var activeOperation = new PendingOperationState
        {
            CommandType = BridgeCommandTypes.ExecuteCode,
            ClientID = 17,
        };
        var queuedOperation = new PendingOperationState
        {
            CommandType = BridgeCommandTypes.Show,
            ClientID = 23,
        };

        var snapshot = ClientWorkSnapshot.Create(
            activeOperation,
            new() { queuedOperation },
            hasPendingResult: false
        );

        Assert.That(snapshot.ActiveCommandType, Is.EqualTo(BridgeCommandTypes.ExecuteCode));
        Assert.That(snapshot.HasOutstandingClientWork(17), Is.True);
        Assert.That(snapshot.HasOutstandingClientWork(23), Is.True);
        Assert.That(snapshot.HasOutstandingClientWork(0), Is.False);
        Assert.That(snapshot.HasOutstandingClientWork(99), Is.False);
    }

    [Test]
    public void ClientWorkSnapshot_TracksReconnectableDisconnectedOperationsAndPendingResults()
    {
        var disconnectedActiveOperation = new PendingOperationState
        {
            CommandType = BridgeCommandTypes.RefreshAssetDatabase,
            ClientID = 0,
        };
        var disconnectedQueuedOperation = new PendingOperationState
        {
            CommandType = BridgeCommandTypes.Show,
            ClientID = 0,
        };

        Assert.That(
            ClientWorkSnapshot.Create(disconnectedActiveOperation, new(), false).HasReconnectableWorkForAnyClient(),
            Is.True
        );
        Assert.That(
            ClientWorkSnapshot.Create(null, new() { disconnectedQueuedOperation }, false).HasReconnectableWorkForAnyClient(),
            Is.True
        );
        Assert.That(
            ClientWorkSnapshot.Create(null, new(), true).HasReconnectableWorkForAnyClient(),
            Is.True
        );
        Assert.That(
            ClientWorkSnapshot.Create(null, new(), false).HasReconnectableWorkForAnyClient(),
            Is.False
        );
    }

    [Test]
    public void UnfocusedGameView_CoverTabSelectionPrefersTestRunnerThenExistingTabs()
    {
        Assert.That(ConduitGameViewFocus.GetCoverTabIndex(1, 2, 0, 3), Is.EqualTo(2));
        Assert.That(ConduitGameViewFocus.GetCoverTabIndex(1, -1, 2, 3), Is.EqualTo(2));
        Assert.That(ConduitGameViewFocus.GetCoverTabIndex(1, -1, -1, 3), Is.EqualTo(0));
        Assert.That(ConduitGameViewFocus.GetCoverTabIndex(0, -1, 0, 1), Is.EqualTo(-1));
    }

    [Test]
    public void GameViewDockTarget_UsesPreferredMainWindowOrder()
    {
        var editorAssembly = typeof(EditorWindow).Assembly;
        var windowTypes = new[]
        {
            typeof(SceneView),
            editorAssembly.GetType("UnityEditor.PreferenceSettingsWindow"),
            editorAssembly.GetType("UnityEditor.ProjectSettingsWindow"),
            editorAssembly.GetType("UnityEditor.PackageManager.UI.PackageManagerWindow"),
            editorAssembly.GetType("UnityEditor.ProfilerWindow")
        };

        Assert.That(windowTypes, Has.None.Null);
        for (int index = 0; index < windowTypes.Length; ++index)
            Assert.That(
                ConduitEditorWindowDocking.GetTargetPriority(windowTypes[index]!),
                Is.EqualTo(index)
            );
    }

    [UnityTest]
    public IEnumerator GameViewDocking_AttachesNewTabsToExistingMainWindow()
    {
        RequireInteractiveEditorWindows();

        var gameViewType = ConduitGameView.GameViewType
                           ?? throw new TypeLoadException("UnityEditor.GameView");
        var target = ConduitEditorWindowDocking.FindPreferredMainDockTarget(gameViewType);
        Assert.That(target, Is.Not.Null);
        var probe = ScriptableObject.CreateInstance<ConduitCaptureProbeWindow>();

        try
        {
            ConduitEditorWindowDocking.DockAsTab(probe, target!);
            yield return null;

            Assert.That(ConduitEditorWindowDocking.IsDockedInMainWindow(probe), Is.True);
            Assert.That(
                ConduitEditorWindowDocking.GetDockArea(probe),
                Is.SameAs(ConduitEditorWindowDocking.GetDockArea(target!))
            );
        }
        finally
        {
            probe.Close();
        }

        yield return null;
    }

    [Test]
    public void UnfocusedGameView_PrepareSkipsWhenAnotherWindowIsMaximized()
    {
        ConduitGameViewFocus.Restore();
        try
        {
            ConduitGameViewFocus.Prepare(true, true);
            Assert.That(ConduitGameViewFocus.IsPrepared, Is.False);
        }
        finally
        {
            ConduitGameViewFocus.Restore();
        }
    }

    [UnityTest]
    public IEnumerator UnfocusedGameView_PrepareAndRestorePreservesPlayBehavior()
    {
        RequireInteractiveEditorWindows();

        var editorAssembly = typeof(EditorWindow).Assembly;
        var gameViewType = editorAssembly.GetType("UnityEditor.GameView")
                           ?? throw new TypeLoadException("UnityEditor.GameView");
        var playModeViewType = editorAssembly.GetType("UnityEditor.PlayModeView")
                               ?? throw new TypeLoadException("UnityEditor.PlayModeView");
        var testRunnerWindowType = typeof(UnityEditor.TestTools.TestRunner.Api.TestRunnerApi).Assembly.GetType(
            "UnityEditor.TestTools.TestRunner.TestRunnerWindow"
        ) ?? throw new TypeLoadException("UnityEditor.TestTools.TestRunner.TestRunnerWindow");
        var behaviorProperty = playModeViewType.GetProperty("enterPlayModeBehavior")
                               ?? throw new MissingMemberException("UnityEditor.PlayModeView.enterPlayModeBehavior");
        var existingGameViews = GetWindowIds(gameViewType);
        var existingTestRunners = GetWindowIds(testRunnerWindowType);
        var previouslyFocusedWindow = EditorWindow.focusedWindow;
        var gameView = ConduitGameView.FindOrOpen();
        yield return null;

        var originalBehavior = behaviorProperty.GetValue(gameView);
        var playFocused = Enum.Parse(behaviorProperty.PropertyType, "PlayFocused");

        try
        {
            ConduitGameViewFocus.Restore();
            behaviorProperty.SetValue(gameView, playFocused);

            ConduitGameViewFocus.Prepare(true);
            yield return null;

            Assert.That(ConduitGameViewFocus.IsPrepared, Is.True);
            Assert.That(behaviorProperty.GetValue(gameView)?.ToString(), Is.EqualTo("PlayUnfocused"));
            Assert.That(ConduitEditorWindowDocking.IsDockedInMainWindow(gameView), Is.True);
            foreach (var candidate in Resources.FindObjectsOfTypeAll(testRunnerWindowType))
                if (candidate is EditorWindow testRunner
                    && !existingTestRunners.Contains(ConduitObjectId.GetObjectId(testRunner)))
                    Assert.That(
                        ConduitEditorWindowDocking.IsDockedInMainWindow(testRunner),
                        Is.True
                    );

            ConduitGameViewFocus.Restore();

            Assert.That(ConduitGameViewFocus.IsPrepared, Is.False);
            Assert.That(behaviorProperty.GetValue(gameView)?.ToString(), Is.EqualTo("PlayFocused"));
        }
        finally
        {
            ConduitGameViewFocus.Restore();
            behaviorProperty.SetValue(gameView, originalBehavior);
            CloseNewWindows(testRunnerWindowType, existingTestRunners);
            CloseNewWindows(gameViewType, existingGameViews);
            previouslyFocusedWindow?.Focus();
        }

        yield return null;

        HashSet<ulong> GetWindowIds(Type windowType)
        {
            var ids = new HashSet<ulong>();
            foreach (var candidate in Resources.FindObjectsOfTypeAll(windowType))
                if (candidate is EditorWindow window)
                    ids.Add(ConduitObjectId.GetObjectId(window));

            return ids;
        }

        void CloseNewWindows(Type windowType, HashSet<ulong> existingWindowIds)
        {
            foreach (var candidate in Resources.FindObjectsOfTypeAll(windowType))
                if (candidate is EditorWindow window
                    && !existingWindowIds.Contains(ConduitObjectId.GetObjectId(window)))
                    window.Close();
        }
    }

    [UnityTest]
    public IEnumerator LowResolutionGameView_PrepareAndRestorePreservesSelectedSize()
    {
        RequireInteractiveEditorWindows();

        var editorAssembly = typeof(EditorWindow).Assembly;
        var gameViewType = editorAssembly.GetType("UnityEditor.GameView")
                           ?? throw new TypeLoadException("UnityEditor.GameView");
        var gameViewSizesType = editorAssembly.GetType("UnityEditor.GameViewSizes")
                                ?? throw new TypeLoadException("UnityEditor.GameViewSizes");
        var selectedSizeIndexProperty = gameViewType.GetProperty("selectedSizeIndex")
                                        ?? throw new MissingMemberException(
                                            "UnityEditor.GameView.selectedSizeIndex"
                                        );
        var sizes = gameViewSizesType.GetProperty(
                        "instance",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy
                    )?.GetValue(null)
                    ?? throw new MissingMemberException("UnityEditor.GameViewSizes.instance");
        var group = gameViewSizesType.GetProperty("currentGroup")?.GetValue(sizes)
                    ?? throw new MissingMemberException("UnityEditor.GameViewSizes.currentGroup");
        var getTotalCount = group.GetType().GetMethod("GetTotalCount")
                            ?? throw new MissingMemberException(
                                "UnityEditor.GameViewSizeGroup.GetTotalCount"
                            );
        var getGameViewSize = group.GetType().GetMethod("GetGameViewSize")
                              ?? throw new MissingMemberException(
                                  "UnityEditor.GameViewSizeGroup.GetGameViewSize"
                              );
        var existingGameViews = new HashSet<ulong>();
        foreach (var candidate in Resources.FindObjectsOfTypeAll(gameViewType))
            if (candidate is EditorWindow window)
                existingGameViews.Add(ConduitObjectId.GetObjectId(window));
        var previouslyFocusedWindow = EditorWindow.focusedWindow;
        var gameView = ConduitGameView.FindOrOpen();
        yield return null;

        int originalSizeIndex = Convert.ToInt32(selectedSizeIndexProperty.GetValue(gameView));
        int originalTargetSizeIndex = FindTargetSizeIndex();
        int previousSizeIndex = originalTargetSizeIndex == 0 ? 1 : 0;

        try
        {
            ConduitGameViewResolution.Restore();
            selectedSizeIndexProperty.SetValue(gameView, previousSizeIndex);

            ConduitGameViewResolution.Prepare(true);

            int targetSizeIndex = FindTargetSizeIndex();
            Assert.That(ConduitGameViewResolution.IsPrepared, Is.True);
            Assert.That(targetSizeIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(
                Convert.ToInt32(selectedSizeIndexProperty.GetValue(gameView)),
                Is.EqualTo(targetSizeIndex)
            );

            ConduitGameViewResolution.Restore();

            Assert.That(ConduitGameViewResolution.IsPrepared, Is.False);
            Assert.That(
                Convert.ToInt32(selectedSizeIndexProperty.GetValue(gameView)),
                Is.EqualTo(previousSizeIndex)
            );
        }
        finally
        {
            ConduitGameViewResolution.Restore();
            selectedSizeIndexProperty.SetValue(gameView, originalSizeIndex);
            if (originalTargetSizeIndex < 0)
                RemoveTargetSize();

            foreach (var candidate in Resources.FindObjectsOfTypeAll(gameViewType))
                if (candidate is EditorWindow window
                    && !existingGameViews.Contains(ConduitObjectId.GetObjectId(window)))
                    window.Close();

            previouslyFocusedWindow?.Focus();
        }

        yield return null;

        int FindTargetSizeIndex()
        {
            int sizeCount = Convert.ToInt32(getTotalCount.Invoke(group, null));
            for (int index = 0; index < sizeCount; ++index)
            {
                var size = getGameViewSize.Invoke(group, new object[] { index })
                           ?? throw new InvalidOperationException("A Game View size was unavailable.");
                var sizeType = size.GetType();
                string? typeName = sizeType.GetProperty("sizeType")?.GetValue(size)?.ToString();
                int width = Convert.ToInt32(sizeType.GetProperty("width")?.GetValue(size));
                int height = Convert.ToInt32(sizeType.GetProperty("height")?.GetValue(size));
                if (typeName == "FixedResolution"
                    && width == ConduitGameViewResolution.TargetWidth
                    && height == ConduitGameViewResolution.TargetHeight)
                    return index;
            }

            return -1;
        }

        void RemoveTargetSize()
        {
            int targetSizeIndex = FindTargetSizeIndex();
            if (targetSizeIndex < 0)
                return;

            var totalIndexToCustomIndex = group.GetType().GetMethod("TotalIndexToCustomIndex")
                                          ?? throw new MissingMemberException(
                                              "UnityEditor.GameViewSizeGroup.TotalIndexToCustomIndex"
                                          );
            var removeCustomSize = group.GetType().GetMethod("RemoveCustomSize")
                                   ?? throw new MissingMemberException(
                                       "UnityEditor.GameViewSizeGroup.RemoveCustomSize"
                                   );
            int customSizeIndex = Convert.ToInt32(
                totalIndexToCustomIndex.Invoke(group, new object[] { targetSizeIndex })
            );
            removeCustomSize.Invoke(
                group,
                new object[] { customSizeIndex }
            );
            gameViewSizesType.GetMethod("Changed")?.Invoke(sizes, null);
            gameViewSizesType.GetMethod("SaveToHDD")?.Invoke(sizes, null);
        }
    }

    [Test]
    public void GameViewAudio_PrepareAndRestorePreservesMuteState()
    {
        bool originalMute = EditorUtility.audioMasterMute;
        try
        {
            ConduitGameViewAudio.Restore();
            EditorUtility.audioMasterMute = false;

            ConduitGameViewAudio.Prepare(true);

            Assert.That(ConduitGameViewAudio.IsPrepared, Is.True);
            Assert.That(EditorUtility.audioMasterMute, Is.True);

            ConduitGameViewAudio.Restore();

            Assert.That(ConduitGameViewAudio.IsPrepared, Is.False);
            Assert.That(EditorUtility.audioMasterMute, Is.False);
        }
        finally
        {
            ConduitGameViewAudio.Restore();
            EditorUtility.audioMasterMute = originalMute;
        }
    }

    [Test]
    public void PlayModePersistedOperation_RestoresCommand()
    {
        var pendingOperation = new PendingOperationState
        {
            RequestID = "play-restore-test",
            CommandType = BridgeCommandTypes.PlayMode,
            ToolUsageStartedUtcTicks = 123456789L,
        };

        try
        {
            OperationPersistence.ClearActiveOperation();
            OperationPersistence.SaveActiveOperation(pendingOperation, BridgeCommandKind.PlayMode);

            var restoredOperation = OperationPersistence.RestoreActiveOperation();
            Assert.That(restoredOperation, Is.Not.Null);
            Assert.That(restoredOperation!.CommandType, Is.EqualTo(BridgeCommandTypes.PlayMode));
            Assert.That(restoredOperation.IsRestored, Is.EqualTo(true));
            Assert.That(restoredOperation.ToolUsageStartedUtcTicks, Is.EqualTo(123456789L));
        }
        finally
        {
            OperationPersistence.ClearActiveOperation();
        }
    }

    [Test]
    public void PendingResultPersistence_RestoresCompletedResult()
    {
        var pendingResult = new PersistedPendingResultState
        {
            RequestID = "refresh-restore-test",
            CommandType = BridgeCommandTypes.RefreshAssetDatabase,
            Result = new() { outcome = ToolOutcome.Success },
        };

        try
        {
            OperationPersistence.ClearPendingResult();
            OperationPersistence.SavePendingResult(pendingResult);

            var restoredResult = OperationPersistence.RestorePendingResult();
            Assert.That(restoredResult, Is.Not.Null);
            Assert.That(restoredResult!.RequestID, Is.EqualTo("refresh-restore-test"));
            Assert.That(restoredResult.CommandType, Is.EqualTo(BridgeCommandTypes.RefreshAssetDatabase));
            Assert.That(restoredResult.Result.outcome, Is.EqualTo(ToolOutcome.Success));
        }
        finally
        {
            OperationPersistence.ClearPendingResult();
        }
    }

    [TestCase(BridgeCommandTypes.PlayMode, true)]
    [TestCase(BridgeCommandTypes.RefreshAssetDatabase, true)]
    [TestCase(BridgeCommandTypes.RunTestsEditMode, true)]
    [TestCase(BridgeCommandTypes.ProjectSettings, true)]
    [TestCase(BridgeCommandTypes.ExecuteCode, false)]
    [TestCase(BridgeCommandTypes.Show, false)]
    public void AssemblyReloadRecovery_OnlyRestoresExplicitEditorWork(
        string commandType,
        bool expected
    )
        => Assert.That(
            OperationPersistence.CanRestore(ConduitToolRunner.ParseIncomingCommand(commandType)),
            Is.EqualTo(expected)
        );

    [Test]
    public void AssemblyReloadInterruptionDiagnostic_WarnsAboutUnknownSideEffects()
    {
        var diagnostic = ConduitToolRunner.BuildAssemblyReloadInterruptionDiagnostic(
            BridgeCommandTypes.ExecuteCode
        );

        Assert.That(diagnostic, Does.Contain(BridgeCommandTypes.ExecuteCode));
        Assert.That(diagnostic, Does.Contain("domain reload"));
        Assert.That(diagnostic, Does.Contain("side effects may have occurred"));
        Assert.That(diagnostic, Does.Contain("not re-executed"));
    }

    [Test]
    public void RestoredRefreshCompileErrorDiagnostic_IsStable()
    {
        Assert.That(
            ConduitToolRunner.BuildRestoredReimportCompileErrorDiagnostic(),
            Is.EqualTo("Asset refresh completed, but the project has compilation errors."));
    }
}
