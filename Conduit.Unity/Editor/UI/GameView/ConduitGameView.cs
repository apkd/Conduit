#nullable enable

using System;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Conduit
{
    static class ConduitGameView
    {
        const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        internal static readonly Type? GameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
        static readonly Type? playModeViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.PlayModeView");
        static readonly MethodInfo? getMainPlayModeViewMethod = playModeViewType?.GetMethod(
            "GetMainPlayModeView",
            StaticMembers
        );
        static readonly Func<EditorWindow?>? getMaximizedWindow = typeof(EditorWindow).Assembly
                .GetType("UnityEditor.WindowLayout")
                ?.GetMethod("GetMaximizedWindow", StaticMembers)
                ?.CreateDelegate(typeof(Func<EditorWindow>))
            as Func<EditorWindow>;

        internal static void PrepareForPlayMode()
        {
            ConduitGameViewFocus.Prepare();
            ConduitGameViewResolution.Prepare();
        }

        internal static bool IsOtherWindowMaximized()
            => getMaximizedWindow?.Invoke() is { } window
               && GameViewType?.IsInstanceOfType(window) != true;

        internal static EditorWindow FindOrOpen()
        {
            if (GameViewType is null)
                throw new TypeLoadException("UnityEditor.GameView");

            EditorWindow? existingGameView = null;
            if (getMainPlayModeViewMethod?.Invoke(null, null) is EditorWindow mainGameView
                && mainGameView != null
                && GameViewType.IsInstanceOfType(mainGameView))
                existingGameView = mainGameView;

            foreach (var candidate in Resources.FindObjectsOfTypeAll(GameViewType))
                if (candidate is EditorWindow gameView && gameView != null)
                {
                    if (ConduitEditorWindowDocking.IsDockedInMainWindow(gameView))
                        return gameView;

                    existingGameView ??= gameView;
                }

            var target = ConduitEditorWindowDocking.FindPreferredMainDockTarget(GameViewType);
            if (target is null)
                throw new InvalidOperationException(
                    "Could not find a docked main-editor window for the Game View."
                );

            bool createdGameView = existingGameView == null;
            var dockedGameView = existingGameView
                                 ?? ScriptableObject.CreateInstance(GameViewType) as EditorWindow
                                 ?? throw new InvalidOperationException("Could not create the Unity Game View.");
            try
            {
                ConduitEditorWindowDocking.DockAsTab(dockedGameView, target);
                return dockedGameView;
            }
            catch
            {
                if (createdGameView)
                    dockedGameView.Close();

                throw;
            }
        }
    }
}
