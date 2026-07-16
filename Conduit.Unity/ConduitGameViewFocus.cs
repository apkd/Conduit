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
        const string GameViewEntityIdStateKey = "Conduit.GameViewFocus.GameViewEntityId";
        const string PreviousBehaviorStateKey = "Conduit.GameViewFocus.PreviousBehavior";

        // unity 6 keeps the per-window play behavior and docking APIs internal, so failures must stay optional
        static readonly Type? gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
        static readonly Type? playModeViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.PlayModeView");
        static readonly Type? dockAreaType = typeof(EditorWindow).Assembly.GetType("UnityEditor.DockArea");
        static readonly Type? testRunnerWindowType = typeof(TestRunnerApi).Assembly.GetType(
            "UnityEditor.TestTools.TestRunner.TestRunnerWindow"
        );
        static readonly PropertyInfo? enterPlayModeBehaviorProperty = playModeViewType?.GetProperty(
            "enterPlayModeBehavior",
            BindingFlags.Instance | BindingFlags.Public
        );
        static readonly MethodInfo? getMainPlayModeViewMethod = playModeViewType?.GetMethod(
            "GetMainPlayModeView",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        static readonly FieldInfo? parentField = typeof(EditorWindow).GetField(
            "m_Parent",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        static readonly FieldInfo? panesField = dockAreaType?.GetField(
            "m_Panes",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        static readonly FieldInfo? lastSelectedField = dockAreaType?.GetField(
            "m_LastSelected",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        static readonly PropertyInfo? selectedProperty = dockAreaType?.GetProperty(
            "selected",
            BindingFlags.Instance | BindingFlags.Public
        );
        static readonly MethodInfo? addTabMethod = dockAreaType?.GetMethod(
            "AddTab",
            BindingFlags.Instance | BindingFlags.Public,
            null,
            new[] { typeof(EditorWindow), typeof(bool) },
            null
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
        {
            if (!enabled || IsPrepared)
                return;

            try
            {
                var gameView = FindOrOpenGameView();
                var property = enterPlayModeBehaviorProperty
                               ?? throw new MissingMemberException("UnityEditor.PlayModeView.enterPlayModeBehavior");
                var previousBehavior = property.GetValue(gameView)
                                       ?? throw new InvalidOperationException(
                                           "The Game View play behavior was unavailable."
                                       );
                var playUnfocused = Enum.Parse(property.PropertyType, "PlayUnfocused");

                // persist restoration data first because entering play mode may reload the domain immediately
                SessionState.SetString(
                    GameViewEntityIdStateKey,
                    EntityId.ToULong(gameView.GetEntityId()).ToString(CultureInfo.InvariantCulture)
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
                var entityId = EntityId.FromULong(ulong.Parse(
                    SessionState.GetString(GameViewEntityIdStateKey, "0"),
                    CultureInfo.InvariantCulture
                ));
                if (EditorUtility.EntityIdToObject(entityId) is EditorWindow gameView
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
                SessionState.EraseString(GameViewEntityIdStateKey);
                SessionState.EraseInt(PreviousBehaviorStateKey);
            }
        }

        internal static int GetCoverTabIndex(
            int selectedIndex,
            int gameViewIndex,
            int testRunnerIndex,
            int lastSelectedIndex,
            int paneCount
        )
        {
            if (testRunnerIndex >= 0)
                return testRunnerIndex;

            if (selectedIndex >= 0 && selectedIndex < paneCount && selectedIndex != gameViewIndex)
                return selectedIndex;

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

        static EditorWindow FindOrOpenGameView()
        {
            if (gameViewType is null)
                throw new TypeLoadException("UnityEditor.GameView");

            if (getMainPlayModeViewMethod?.Invoke(null, null) is EditorWindow mainGameView
                && gameViewType.IsInstanceOfType(mainGameView))
                return mainGameView;

            foreach (var candidate in Resources.FindObjectsOfTypeAll(gameViewType))
                if (candidate is EditorWindow gameView)
                    return gameView;

            return EditorWindow.GetWindow(gameViewType, false, "Game", false);
        }

        static void CoverGameView(EditorWindow gameView)
        {
            try
            {
                if (parentField?.GetValue(gameView) is not { } dockArea
                    || dockAreaType is null
                    || !dockAreaType.IsInstanceOfType(dockArea)
                    || panesField?.GetValue(dockArea) is not IList panes)
                    return;

                int gameViewIndex = IndexOf(panes, gameView);
                if (gameViewIndex < 0)
                    return;

                int testRunnerIndex = FindTestRunnerIndex(panes);
                int selectedIndex = selectedProperty?.GetValue(dockArea) as int? ?? -1;
                int lastSelectedIndex = lastSelectedField?.GetValue(dockArea) as int? ?? -1;
                int coverIndex = GetCoverTabIndex(
                    selectedIndex,
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
                if (testRunnerWindowType is not { } windowType || addTabMethod is not { } addTab)
                    throw new MissingMemberException("Unity Test Runner window docking API");

                var testRunner = ScriptableObject.CreateInstance(windowType) as EditorWindow
                                 ?? throw new InvalidOperationException(
                                     "Could not create the Unity Test Runner window."
                                 );
                try
                {
                    addTab.Invoke(dockArea, new object[] { testRunner, true });
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
