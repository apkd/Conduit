#nullable enable

using System;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Conduit
{
    // attaches feature-created tabs to the existing editor layout without creating auxiliary containers
    static class ConduitEditorWindowDocking
    {
        const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static readonly Type? DockAreaType = typeof(EditorWindow).Assembly.GetType("UnityEditor.DockArea");
        static readonly FieldInfo? parentField = typeof(EditorWindow).GetField("m_Parent", InstanceMembers);
        static readonly PropertyInfo? floatingWindowProperty = DockAreaType?.GetProperty(
            "floatingWindow",
            InstanceMembers
        );
        static readonly MethodInfo? addTabMethod = DockAreaType?.GetMethod(
            "AddTab",
            InstanceMembers,
            null,
            new[] { typeof(EditorWindow), typeof(bool) },
            null
        );
        static readonly MethodInfo? removeTabMethod = DockAreaType?.GetMethod(
            "RemoveTab",
            InstanceMembers,
            null,
            new[] { typeof(EditorWindow), typeof(bool), typeof(bool) },
            null
        );

        internal static object? GetDockArea(EditorWindow window)
        {
            var parent = parentField?.GetValue(window);
            return parent is not null && DockAreaType?.IsInstanceOfType(parent) == true
                ? parent
                : null;
        }

        internal static bool IsMainDockArea(object dockArea)
            => DockAreaType?.IsInstanceOfType(dockArea) == true
               && floatingWindowProperty?.GetValue(dockArea) is false;

        internal static bool IsDockedInMainWindow(EditorWindow window)
            => GetDockArea(window) is { } dockArea && IsMainDockArea(dockArea);

        internal static EditorWindow? FindPreferredMainDockTarget(Type excludedWindowType)
        {
            EditorWindow? bestTarget = null;
            int bestPriority = int.MaxValue;
            foreach (var candidate in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (candidate == null
                    || excludedWindowType.IsInstanceOfType(candidate)
                    || !IsDockedInMainWindow(candidate))
                    continue;

                int priority = GetTargetPriority(candidate.GetType());
                if (priority >= bestPriority)
                    continue;

                bestTarget = candidate;
                bestPriority = priority;
            }

            return bestTarget;
        }

        internal static int GetTargetPriority(Type windowType)
            => windowType.FullName switch
            {
                "UnityEditor.SceneView" => 0,
                "UnityEditor.PreferenceSettingsWindow" => 1,
                "UnityEditor.ProjectSettingsWindow" => 2,
                "UnityEditor.PackageManager.UI.PackageManagerWindow" => 3,
                "UnityEditor.ProfilerWindow" => 4,
                "UnityEditor.ConsoleWindow" => 5,
                "UnityEditor.ProjectBrowser" => 6,
                "UnityEditor.InspectorWindow" => 7,
                "UnityEditor.SceneHierarchyWindow" => 8,
                _ => 9
            };

        internal static void DockAsTab(EditorWindow window, EditorWindow target)
        {
            var targetDockArea = GetDockArea(target);
            if (targetDockArea is null || !IsMainDockArea(targetDockArea))
                throw new InvalidOperationException("The target editor window is not docked in the main window.");

            var sourceDockArea = GetDockArea(window);
            if (ReferenceEquals(sourceDockArea, targetDockArea))
                return;

            // unity's AddTab does not detach a pane from its current DockArea
            if (sourceDockArea is not null)
                RemoveTab(sourceDockArea, window);

            AddTab(targetDockArea, window, sourceDockArea is null);
        }

        internal static void AddTab(object dockArea, EditorWindow window, bool sendPaneEvents = true)
        {
            if (!IsMainDockArea(dockArea) || addTabMethod is not { } addTab)
                throw new MissingMemberException("Unity main-window docking API");

            addTab.Invoke(dockArea, new object[] { window, sendPaneEvents });
        }

        static void RemoveTab(object dockArea, EditorWindow window)
        {
            if (removeTabMethod is not { } removeTab)
                throw new MissingMemberException("Unity tab removal API");

            removeTab.Invoke(dockArea, new object[] { window, true, false });
        }
    }

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
            var dockedGameView = createdGameView
                ? ScriptableObject.CreateInstance(GameViewType) as EditorWindow
                  ?? throw new InvalidOperationException("Could not create the Unity Game View.")
                : existingGameView;
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

    [InitializeOnLoad]
    static class ConduitGameViewResolution
    {
        internal const int TargetWidth = 480;
        internal const int TargetHeight = 320;

        // session state survives the domain reloads on both sides of play mode
        const string ActiveStateKey = "Conduit.GameViewResolution.Active";
        const string GameViewObjectIdStateKey = "Conduit.GameViewResolution.GameViewObjectId";
        const string PreviousSizeIndexStateKey = "Conduit.GameViewResolution.PreviousSizeIndex";
        const string SizeGroupTypeStateKey = "Conduit.GameViewResolution.SizeGroupType";
        const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // unity keeps Game View presets in internal editor types, so reflection failures remain optional
        static readonly Assembly editorAssembly = typeof(EditorWindow).Assembly;
        static readonly Type? gameViewSizesType = editorAssembly.GetType("UnityEditor.GameViewSizes");
        static readonly Type? gameViewSizeType = editorAssembly.GetType("UnityEditor.GameViewSize");
        static readonly Type? gameViewSizeTypeEnum = editorAssembly.GetType("UnityEditor.GameViewSizeType");
        static readonly PropertyInfo? selectedSizeIndexProperty = ConduitGameView.GameViewType?.GetProperty(
            "selectedSizeIndex",
            InstanceMembers
        );
        static readonly FieldInfo? selectedSizesField = ConduitGameView.GameViewType?.GetField(
            "m_SelectedSizes",
            InstanceMembers
        );
        static readonly PropertyInfo? gameViewSizesInstanceProperty = gameViewSizesType?.GetProperty(
            "instance",
            StaticMembers | BindingFlags.FlattenHierarchy
        );
        static readonly PropertyInfo? currentGroupProperty = gameViewSizesType?.GetProperty(
            "currentGroup",
            InstanceMembers
        );
        static readonly PropertyInfo? currentGroupTypeProperty = gameViewSizesType?.GetProperty(
            "currentGroupType",
            InstanceMembers
        );
        static readonly MethodInfo? changedMethod = gameViewSizesType?.GetMethod(
            "Changed",
            InstanceMembers
        );
        static readonly MethodInfo? saveToHddMethod = gameViewSizesType?.GetMethod(
            "SaveToHDD",
            InstanceMembers
        );
        static readonly PropertyInfo? sizeTypeProperty = gameViewSizeType?.GetProperty(
            "sizeType",
            InstanceMembers
        );
        static readonly PropertyInfo? widthProperty = gameViewSizeType?.GetProperty(
            "width",
            InstanceMembers
        );
        static readonly PropertyInfo? heightProperty = gameViewSizeType?.GetProperty(
            "height",
            InstanceMembers
        );
        static readonly ConstructorInfo? gameViewSizeConstructor = GetGameViewSizeConstructor();

        static ConduitGameViewResolution()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += Restore;
        }

        internal static bool IsPrepared => SessionState.GetBool(ActiveStateKey, false);

        internal static void Prepare()
            => Prepare(ConduitSettings.instance.LowResolutionPlayMode);

        internal static void Prepare(bool enabled)
        {
            if (!enabled || IsPrepared)
                return;

            try
            {
                var gameView = ConduitGameView.FindOrOpen();
                var selectedSize = selectedSizeIndexProperty
                                   ?? throw new MissingMemberException("UnityEditor.GameView.selectedSizeIndex");
                var sizes = GetGameViewSizes();
                int previousSizeIndex = Convert.ToInt32(selectedSize.GetValue(gameView));
                int sizeGroupType = Convert.ToInt32(
                    currentGroupTypeProperty?.GetValue(sizes)
                    ?? throw new MissingMemberException("UnityEditor.GameViewSizes.currentGroupType")
                );
                int targetSizeIndex = FindOrCreateTargetSize(sizes);

                // persist restoration data before the caller asks Unity to enter play mode
                SessionState.SetString(
                    GameViewObjectIdStateKey,
                    ConduitUtility.GetObjectId(gameView).ToString(CultureInfo.InvariantCulture)
                );
                SessionState.SetInt(PreviousSizeIndexStateKey, previousSizeIndex);
                SessionState.SetInt(SizeGroupTypeStateKey, sizeGroupType);
                SessionState.SetBool(ActiveStateKey, true);

                selectedSize.SetValue(gameView, targetSizeIndex);
                gameView.Repaint();
            }
            catch (Exception exception)
            {
                Restore();
                ConduitDiagnostics.Warn($"Could not lower the Game View resolution: {exception.Message}");
            }
        }

        internal static void RestoreIfInEditMode()
        {
            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
                Restore();
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
                if (ConduitUtility.ResolveObjectId(objectId) is not EditorWindow gameView)
                    return;

                int previousSizeIndex = SessionState.GetInt(PreviousSizeIndexStateKey, 0);
                int previousSizeGroupType = SessionState.GetInt(SizeGroupTypeStateKey, 0);
                int currentSizeGroupType = Convert.ToInt32(
                    currentGroupTypeProperty?.GetValue(GetGameViewSizes())
                    ?? throw new MissingMemberException("UnityEditor.GameViewSizes.currentGroupType")
                );

                if (currentSizeGroupType == previousSizeGroupType)
                {
                    var selectedSize = selectedSizeIndexProperty
                                       ?? throw new MissingMemberException(
                                           "UnityEditor.GameView.selectedSizeIndex"
                    );
                    selectedSize.SetValue(gameView, previousSizeIndex);
                }
                // each build-target group has its own selection slot
                else if (selectedSizesField?.GetValue(gameView) is int[] selectedSizes
                         && previousSizeGroupType >= 0
                         && previousSizeGroupType < selectedSizes.Length)
                    selectedSizes[previousSizeGroupType] = previousSizeIndex;
                else
                    throw new InvalidOperationException(
                        "The previous Game View size group was unavailable."
                    );

                gameView.Repaint();
            }
            catch (Exception exception)
            {
                ConduitDiagnostics.Warn($"Could not restore the Game View resolution: {exception.Message}");
            }
            finally
            {
                SessionState.EraseBool(ActiveStateKey);
                SessionState.EraseString(GameViewObjectIdStateKey);
                SessionState.EraseInt(PreviousSizeIndexStateKey);
                SessionState.EraseInt(SizeGroupTypeStateKey);
            }
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
                Restore();
        }

        static object GetGameViewSizes()
            => gameViewSizesInstanceProperty?.GetValue(null)
               ?? throw new MissingMemberException("UnityEditor.GameViewSizes.instance");

        static int FindOrCreateTargetSize(object sizes)
        {
            var group = currentGroupProperty?.GetValue(sizes)
                        ?? throw new MissingMemberException("UnityEditor.GameViewSizes.currentGroup");
            var groupType = group.GetType();
            var getTotalCount = groupType.GetMethod(
                                    "GetTotalCount",
                                    InstanceMembers
                                )
                                ?? throw new MissingMemberException(
                                    "UnityEditor.GameViewSizeGroup.GetTotalCount"
                                );
            var getGameViewSize = groupType.GetMethod(
                                      "GetGameViewSize",
                                      InstanceMembers
                                  )
                                  ?? throw new MissingMemberException(
                                      "UnityEditor.GameViewSizeGroup.GetGameViewSize"
                                  );
            int sizeCount = Convert.ToInt32(getTotalCount.Invoke(group, null));
            var sizeType = sizeTypeProperty
                           ?? throw new MissingMemberException("UnityEditor.GameViewSize.sizeType");
            var width = widthProperty
                        ?? throw new MissingMemberException("UnityEditor.GameViewSize.width");
            var height = heightProperty
                         ?? throw new MissingMemberException("UnityEditor.GameViewSize.height");
            var fixedResolution = gameViewSizeTypeEnum is null
                ? throw new TypeLoadException("UnityEditor.GameViewSizeType")
                : Enum.Parse(gameViewSizeTypeEnum, "FixedResolution");

            for (int index = 0; index < sizeCount; ++index)
            {
                var size = getGameViewSize.Invoke(group, new object[] { index });
                if (Equals(sizeType.GetValue(size), fixedResolution)
                    && Convert.ToInt32(width.GetValue(size)) == TargetWidth
                    && Convert.ToInt32(height.GetValue(size)) == TargetHeight)
                    return index;
            }

            var constructor = gameViewSizeConstructor
                              ?? throw new MissingMemberException("UnityEditor.GameViewSize constructor");
            var changed = changedMethod
                          ?? throw new MissingMemberException("UnityEditor.GameViewSizes.Changed");
            var saveToHdd = saveToHddMethod
                            ?? throw new MissingMemberException("UnityEditor.GameViewSizes.SaveToHDD");
            var addCustomSize = gameViewSizeType is null
                ? null
                : groupType.GetMethod(
                    "AddCustomSize",
                    InstanceMembers,
                    null,
                    new[] { gameViewSizeType },
                    null
                );
            if (addCustomSize is null)
                throw new MissingMemberException("UnityEditor.GameViewSizeGroup.AddCustomSize");

            var targetSize = constructor.Invoke(
                new[] { fixedResolution, TargetWidth, TargetHeight, string.Empty }
            );
            addCustomSize.Invoke(group, new[] { targetSize });
            changed.Invoke(sizes, null);
            saveToHdd.Invoke(sizes, null);
            return sizeCount;
        }

        static ConstructorInfo? GetGameViewSizeConstructor()
        {
            if (gameViewSizeType is null || gameViewSizeTypeEnum is null)
                return null;

            return gameViewSizeType.GetConstructor(
                InstanceMembers,
                null,
                new[] { gameViewSizeTypeEnum, typeof(int), typeof(int), typeof(string) },
                null
            );
        }
    }
}
