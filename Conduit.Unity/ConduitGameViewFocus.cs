#nullable enable

using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Conduit
{
    [InitializeOnLoad]
    static class ConduitGameViewFocus
    {
        // session state survives the domain reload between exiting edit mode and entering play mode
        const string ActiveStateKey = "Conduit.GameViewFocus.Active";
        const string GameViewObjectIdStateKey = "Conduit.GameViewFocus.GameViewObjectId";
        const string PreviousBehaviorStateKey = "Conduit.GameViewFocus.PreviousBehavior";

        // unity 6 keeps the per-window play behavior and docking APIs internal, so failures must stay optional
        static readonly Type? playModeViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.PlayModeView");
        static readonly Type? testRunnerWindowType = typeof(TestRunnerApi).Assembly.GetType(
            "UnityEditor.TestTools.TestRunner.TestRunnerWindow"
        );
        static readonly PropertyInfo? enterPlayModeBehaviorProperty = playModeViewType?.GetProperty(
            "enterPlayModeBehavior",
            BindingFlags.Instance | BindingFlags.Public
        );
        static readonly FieldInfo? panesField = ConduitEditorWindowDocking.DockAreaType?.GetField(
            "m_Panes",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        static readonly FieldInfo? lastSelectedField = ConduitEditorWindowDocking.DockAreaType?.GetField(
            "m_LastSelected",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        static readonly PropertyInfo? selectedProperty = ConduitEditorWindowDocking.DockAreaType?.GetProperty(
            "selected",
            BindingFlags.Instance | BindingFlags.Public
        );

        static ConduitGameViewFocus()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += Restore;
        }

        internal static bool IsPrepared => SessionState.GetBool(ActiveStateKey, false);

        internal static void Prepare()
            => Prepare(ConduitSettings.instance.UnfocusedGameView);

        internal static void Prepare(bool enabled)
            => Prepare(enabled, ConduitGameView.IsOtherWindowMaximized());

        internal static void Prepare(bool enabled, bool isOtherWindowMaximized)
        {
            if (!enabled || IsPrepared || isOtherWindowMaximized)
                return;

            try
            {
                var gameView = ConduitGameView.FindOrOpen();
                var property = enterPlayModeBehaviorProperty
                               ?? throw new MissingMemberException("UnityEditor.PlayModeView.enterPlayModeBehavior");
                var previousBehavior = property.GetValue(gameView)
                                       ?? throw new InvalidOperationException(
                                           "The Game View play behavior was unavailable."
                                       );
                var playUnfocused = Enum.Parse(property.PropertyType, "PlayUnfocused");

                // persist restoration data first because entering play mode may reload the domain immediately
                SessionState.SetString(
                    GameViewObjectIdStateKey,
                    ConduitUtility.GetObjectId(gameView).ToString(CultureInfo.InvariantCulture)
                );
                SessionState.SetInt(PreviousBehaviorStateKey, Convert.ToInt32(previousBehavior));
                SessionState.SetBool(ActiveStateKey, true);

                property.SetValue(gameView, playUnfocused);
                CoverGameView(gameView);
            }
            catch (Exception exception)
            {
                Restore();
                ConduitDiagnostics.Warn($"Could not prepare the unfocused Game View: {exception.Message}");
            }
        }

        internal static void Restore()
        {
            if (!IsPrepared)
                return;

            try
            {
                var objectId = ulong.Parse(
                    SessionState.GetString(GameViewObjectIdStateKey, "0"),
                    CultureInfo.InvariantCulture
                );
                if (ConduitUtility.ResolveObjectId(objectId) is EditorWindow gameView
                    && enterPlayModeBehaviorProperty is { } property)
                {
                    var previousBehavior = Enum.ToObject(
                        property.PropertyType,
                        SessionState.GetInt(PreviousBehaviorStateKey, 0)
                    );
                    property.SetValue(gameView, previousBehavior);
                }
            }
            catch (Exception exception)
            {
                ConduitDiagnostics.Warn($"Could not restore the Game View play behavior: {exception.Message}");
            }
            finally
            {
                SessionState.EraseBool(ActiveStateKey);
                SessionState.EraseString(GameViewObjectIdStateKey);
                SessionState.EraseInt(PreviousBehaviorStateKey);
            }
        }

        internal static int GetCoverTabIndex(
            int gameViewIndex,
            int testRunnerIndex,
            int lastSelectedIndex,
            int paneCount
        )
        {
            if (testRunnerIndex >= 0)
                return testRunnerIndex;

            if (lastSelectedIndex >= 0 && lastSelectedIndex < paneCount && lastSelectedIndex != gameViewIndex)
                return lastSelectedIndex;

            for (int index = 0; index < paneCount; ++index)
                if (index != gameViewIndex)
                    return index;

            return -1;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // the override is transition-scoped; retaining it would silently change the user's window preference
            if (state is PlayModeStateChange.EnteredPlayMode or PlayModeStateChange.EnteredEditMode)
                Restore();
        }

        static void CoverGameView(EditorWindow gameView)
        {
            try
            {
                if (ConduitEditorWindowDocking.GetDockArea(gameView) is not { } dockArea
                    || panesField?.GetValue(dockArea) is not IList panes)
                    return;

                int gameViewIndex = IndexOf(panes, gameView);
                if (gameViewIndex < 0)
                    return;

                int testRunnerIndex = FindTestRunnerIndex(panes);
                int selectedIndex = selectedProperty?.GetValue(dockArea) as int? ?? -1;
                if (selectedIndex != gameViewIndex)
                    return;

                int lastSelectedIndex = lastSelectedField?.GetValue(dockArea) as int? ?? -1;
                int coverIndex = GetCoverTabIndex(
                    gameViewIndex,
                    testRunnerIndex,
                    lastSelectedIndex,
                    panes.Count
                );
                if (coverIndex >= 0)
                {
                    (panes[coverIndex] as EditorWindow)?.Focus();
                    return;
                }

                // a one-tab dock cannot be unfocused, and Test Runner remains useful after serving as its cover
                if (ConduitEditorWindowDocking.IsMainDockArea(dockArea))
                    AddTestRunnerTab(dockArea);
            }
            catch (Exception exception)
            {
                ConduitDiagnostics.Warn($"Could not cover the Game View tab: {exception.Message}");
            }

            static int IndexOf(IList panes, EditorWindow window)
            {
                for (int index = 0, count = panes.Count; index < count; ++index)
                    if (ReferenceEquals(panes[index], window))
                        return index;

                return -1;
            }

            static int FindTestRunnerIndex(IList panes)
            {
                if (testRunnerWindowType is not { } windowType)
                    return -1;

                for (int index = 0, count = panes.Count; index < count; ++index)
                    if (panes[index] is { } pane && windowType.IsInstanceOfType(pane))
                        return index;

                return -1;
            }

            static void AddTestRunnerTab(object dockArea)
            {
                if (testRunnerWindowType is not { } windowType)
                    throw new TypeLoadException("Unity Test Runner window");

                var testRunner = ScriptableObject.CreateInstance(windowType) as EditorWindow
                                 ?? throw new InvalidOperationException(
                                     "Could not create the Unity Test Runner window."
                                 );
                try
                {
                    ConduitEditorWindowDocking.AddTab(dockArea, testRunner);
                    testRunner.Focus();
                }
                catch
                {
                    testRunner.Close();
                    throw;
                }
            }
        }
    }
}
