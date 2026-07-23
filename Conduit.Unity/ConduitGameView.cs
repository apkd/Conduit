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

            if (getMainPlayModeViewMethod?.Invoke(null, null) is EditorWindow mainGameView
                && GameViewType.IsInstanceOfType(mainGameView))
                return mainGameView;

            foreach (var candidate in Resources.FindObjectsOfTypeAll(GameViewType))
                if (candidate is EditorWindow gameView)
                    return gameView;

            return EditorWindow.GetWindow(GameViewType, false, "Game", false);
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
