#nullable enable

#if MODULE_IMGUI
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace Conduit
{
    sealed class ConduitToolUsageWindow : EditorWindow
    {
        const float CallsColumnWidth = 90f;
        const float DurationColumnWidth = 180f;
        const float RowHeight = 22f;

        static readonly GUIContent[] tabs =
        {
            new("This project"),
            new("All projects"),
        };

        DataScope scope;
        Vector2 scrollPosition;
        GUIStyle? rightAlignedLabel;
        GUIStyle? rightAlignedBoldLabel;

        internal static void Open()
        {
            var window = GetWindow<ConduitToolUsageWindow>();
            window.titleContent = new("Local tool usage");
            window.Show();
        }

        void OnEnable()
            => ConduitToolUsage.DataChanged += Repaint;

        void OnDisable()
            => ConduitToolUsage.DataChanged -= Repaint;

        void OnInspectorUpdate()
        {
            // other editor processes cannot signal changes to the shared all-project aggregate.
            if (scope == DataScope.AllProjects)
                Repaint();
        }

        void OnGUI()
        {
            EnsureStyles();
            scope = (DataScope)GUILayout.Toolbar(
                (int)scope,
                tabs,
                EditorStyles.miniButton
            );
            GUILayout.Space(8f);

            DrawTable(
                scope == DataScope.Project
                    ? ConduitToolUsage.GetProjectData()
                    : ConduitToolUsage.GetAllProjectsData()
            );

            GUILayout.Space(6f);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (EditorGUILayout.LinkButton("Delete all stored data")
                && EditorUtility.DisplayDialog(
                    "Delete local tool usage data?",
                    "Delete the stored totals for this project and for all projects?",
                    "Delete",
                    "Cancel"
                ))
                ConduitToolUsage.DeleteAllStoredData();
            EditorGUILayout.EndHorizontal();
        }

        void DrawTable(ToolUsageRecord[] records)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawRow(
                "MCP tool",
                "Calls",
                "Average Unity-side duration",
                EditorStyles.boldLabel,
                rightAlignedBoldLabel!,
                drawBackground: true
            );

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            for (int index = 0, count = records.Length; index < count; ++index)
            {
                var record = records[index];
                DrawRow(
                    record.ToolName,
                    record.CallCount.ToString("N0", CultureInfo.CurrentCulture),
                    FormatDuration(record),
                    EditorStyles.label,
                    rightAlignedLabel!,
                    drawBackground: (index & 1) != 0
                );
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        void DrawRow(
            string toolName,
            string calls,
            string duration,
            GUIStyle nameStyle,
            GUIStyle valueStyle,
            bool drawBackground
        )
        {
            var rect = EditorGUILayout.GetControlRect(false, RowHeight);
            if (drawBackground)
                EditorGUI.DrawRect(
                    rect,
                    EditorGUIUtility.isProSkin
                        ? new(1f, 1f, 1f, 0.045f)
                        : new(0f, 0f, 0f, 0.045f)
                );

            float durationWidth = Mathf.Min(DurationColumnWidth, rect.width * 0.4f);
            float callsWidth = Mathf.Min(CallsColumnWidth, rect.width * 0.2f);
            float durationX = rect.xMax - durationWidth;
            float callsX = durationX - callsWidth;
            GUI.Label(
                new(
                    rect.x + 4f,
                    rect.y,
                    Mathf.Max(0f, callsX - rect.x - 8f),
                    rect.height
                ),
                toolName,
                nameStyle
            );
            GUI.Label(
                new(callsX, rect.y, Mathf.Max(0f, callsWidth - 8f), rect.height),
                calls,
                valueStyle
            );
            GUI.Label(
                new(durationX, rect.y, Mathf.Max(0f, durationWidth - 4f), rect.height),
                duration,
                valueStyle
            );
        }

        static string FormatDuration(ToolUsageRecord record)
        {
            if (record.CallCount == 0L)
                return "—";

            double milliseconds = record.AverageDurationMilliseconds;
            if (milliseconds < 1d)
                return $"{milliseconds:0.###} ms";
            if (milliseconds < 1000d)
                return $"{milliseconds:0.0} ms";

            return $"{milliseconds / 1000d:0.00} s";
        }

        void EnsureStyles()
        {
            rightAlignedLabel ??= new(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleRight,
            };
            rightAlignedBoldLabel ??= new(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleRight,
            };
        }

        enum DataScope : byte
        {
            Project,
            AllProjects,
        }
    }
}
#endif
