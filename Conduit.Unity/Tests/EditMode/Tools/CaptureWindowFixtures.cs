#nullable enable

using UnityEditor;
using UnityEngine;

sealed class ConduitWindowMatchAlphaWindow : EditorWindow
{
    void OnEnable() => titleContent = new("Conduit Window Match Alpha");

    void OnGUI() => GUILayout.Label("Conduit Window Match Alpha");
}

sealed class ConduitWindowMatchBetaWindow : EditorWindow
{
    void OnEnable() => titleContent = new("Conduit Window Match Beta");

    void OnGUI() => GUILayout.Label("Conduit Window Match Beta");
}

sealed class ConduitTypeMatchAlphaWindow : EditorWindow
{
    void OnEnable() => titleContent = new("Conduit Type Match Alpha");

    void OnGUI() => GUILayout.Label("Conduit Type Match Alpha");
}

sealed class ConduitTypeMatchBetaWindow : EditorWindow
{
    void OnEnable() => titleContent = new("Conduit Type Match Beta");

    void OnGUI() => GUILayout.Label("Conduit Type Match Beta");
}

sealed class ConduitCaptureProbeWindow : EditorWindow
{
    void OnEnable() => titleContent = new("Conduit Capture Probe");

    void OnGUI() => GUILayout.Label("Conduit Capture Probe");
}
