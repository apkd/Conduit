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
        const int MainWindowShowMode = 4;
        const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static readonly Type? DockAreaType = typeof(EditorWindow).Assembly.GetType("UnityEditor.DockArea");
        static readonly FieldInfo? parentField = typeof(EditorWindow).GetField("m_Parent", InstanceMembers);
        static readonly PropertyInfo? floatingWindowProperty = DockAreaType?.GetProperty(
            "floatingWindow",
            InstanceMembers
        );
        static readonly PropertyInfo? containerWindowProperty = typeof(EditorWindow).Assembly
            .GetType("UnityEditor.GUIView")
            ?.GetProperty("window", InstanceMembers);
        static readonly PropertyInfo? containerWindowShowModeProperty = typeof(EditorWindow).Assembly
            .GetType("UnityEditor.ContainerWindow")
            ?.GetProperty("showMode", InstanceMembers);
        static readonly MethodInfo? getMaximizedWindowMethod = typeof(EditorWindow).Assembly
            .GetType("UnityEditor.WindowLayout")
            ?.GetMethod(
                "GetMaximizedWindow",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
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

        internal static EditorWindow? GetMaximizedWindow()
            => getMaximizedWindowMethod?.Invoke(null, null) as EditorWindow;

        internal static void EnsureCanShow(EditorWindow window, string target)
        {
            var maximized = GetMaximizedWindow();
            if (maximized == null || ReferenceEquals(maximized, window))
                return;

            var targetContainer = GetContainerWindow(window);
            if (targetContainer == null
                || !ReferenceEquals(targetContainer, GetContainerWindow(maximized)))
                return;

            throw new InvalidOperationException(
                $"Cannot show '{target}' while editor window '{maximized.titleContent.text}' is maximized. Restore the editor layout first."
            );
        }

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

        internal static EditorWindow CreateDockedTab(Type windowType)
        {
            var maximized = GetMaximizedWindow();
            if (maximized != null && IsMainContainer(GetContainerWindow(maximized)))
                throw new InvalidOperationException(
                    $"Cannot show '{windowType.Name}' while editor window '{maximized.titleContent.text}' is maximized. Restore the editor layout first."
                );

            var target = FindPreferredMainDockTarget(windowType)
                         ?? throw new InvalidOperationException(
                             $"Could not find a docked main-editor window for '{windowType.Name}'."
                         );
            EnsureCanShow(target, windowType.Name);
            var window = ScriptableObject.CreateInstance(windowType) as EditorWindow
                         ?? throw new InvalidOperationException($"Could not create editor window '{windowType.Name}'.");
            try
            {
                // attach the new pane directly so Unity does not create a floating host first
                DockAsTab(window, target);
                return window;
            }
            catch
            {
                window.Close();
                throw;
            }
        }

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

        static object? GetContainerWindow(EditorWindow window)
            => parentField?.GetValue(window) is { } parent
                ? containerWindowProperty?.GetValue(parent)
                : null;

        static bool IsMainContainer(object? container)
            => container != null
               && Convert.ToInt32(
                   containerWindowShowModeProperty?.GetValue(container),
                   CultureInfo.InvariantCulture
               ) == MainWindowShowMode;
    }
}
