#nullable enable

using System;
using System.Collections;
using Conduit;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

public sealed partial class ConduitMcpToolsTests
{
    [Test]
    public void IdleClose_WaitsForAgentUseAndHonorsFractionalMinutes()
    {
        var state = new EditorIdleCloseState(EditorIdleOwnership.User, now: 0);
        Assert.That(state.ShouldClose(1000, 0.5, EditorIdleAvailability.Idle), Is.False);

        state.RecordAgentInteraction(1000);
        Assert.That(state.ShouldClose(1029, 0.5, EditorIdleAvailability.Idle), Is.False);
        Assert.That(state.ShouldClose(1030, 0.5, EditorIdleAvailability.Idle), Is.True);
    }

    [Test]
    public void IdleClose_UserProtectionSurvivesReloadUntilAnotherAgentInteraction()
    {
        var state = new EditorIdleCloseState(EditorIdleOwnership.Agent, now: 0);
        state.RecordUserInteraction();
        state = new(state.Ownership, now: 100);
        Assert.That(state.ShouldClose(1000, 0.5, EditorIdleAvailability.Idle), Is.False);

        state.RecordAgentInteraction(1000);
        Assert.That(state.ShouldClose(1030, 0.5, EditorIdleAvailability.Idle), Is.True);
        state.RecordUserInteraction();
        Assert.That(state.ShouldClose(2000, 0.5, EditorIdleAvailability.Idle), Is.False);
    }

    [Test]
    public void IdleClose_BusyWorkRestartsTheIdleInterval()
    {
        var state = new EditorIdleCloseState(EditorIdleOwnership.Agent, now: 0);
        Assert.That(state.ShouldClose(1000, 0.5, EditorIdleAvailability.Busy), Is.False);
        Assert.That(state.ShouldClose(1029, 0.5, EditorIdleAvailability.Idle), Is.False);
        Assert.That(state.ShouldClose(1030, 0.5, EditorIdleAvailability.Idle), Is.True);
    }

    [Test]
    public void IdleClose_IncidentalEventsDoNotProtectTheEditor()
    {
        var state = new EditorIdleCloseState(EditorIdleOwnership.Agent, now: 0);
        foreach (var type in new[] { EventType.MouseMove, EventType.MouseEnterWindow, EventType.MouseLeaveWindow, EventType.Layout, EventType.Repaint })
            if (ConduitEditorInput.IsUserInput(type, KeyCode.None, EventModifiers.None))
                state.RecordUserInteraction();
        if (ConduitEditorInput.IsUserInput(EventType.KeyDown, KeyCode.LeftAlt, EventModifiers.Alt)
            || ConduitEditorInput.IsUserInput(EventType.KeyDown, KeyCode.Tab, EventModifiers.Alt))
            state.RecordUserInteraction();

        Assert.That(state.ShouldClose(30, 0.5, EditorIdleAvailability.Idle), Is.True);
    }

    [TestCase(EventType.MouseDown, KeyCode.None)]
    [TestCase(EventType.MouseDrag, KeyCode.None)]
    [TestCase(EventType.ScrollWheel, KeyCode.None)]
    [TestCase(EventType.KeyDown, KeyCode.A)]
    [TestCase(EventType.DragPerform, KeyCode.None)]
    public void IdleClose_UserInputPreventsClosing(EventType type, KeyCode key)
    {
        var state = new EditorIdleCloseState(EditorIdleOwnership.Agent, now: 0);
        if (ConduitEditorInput.IsUserInput(type, key, EventModifiers.None))
            state.RecordUserInteraction();

        Assert.That(state.ShouldClose(1000, 0.5, EditorIdleAvailability.Idle), Is.False);
    }

    [UnityTest]
    public IEnumerator IdleClose_InputHookSeesConsumedIMGUIEvents() => CheckConsumedInput(IdleInputBackend.Imgui);

    [UnityTest]
    public IEnumerator IdleClose_InputHookSeesConsumedUIToolkitEvents() => CheckConsumedInput(IdleInputBackend.UiToolkit);

    static IEnumerator CheckConsumedInput(IdleInputBackend backend)
    {
        RequireInteractiveEditorWindows();
        var previousFocus = EditorWindow.focusedWindow;
        ConduitIdleInputProbeWindow window = backend == IdleInputBackend.Imgui
            ? ScriptableObject.CreateInstance<ConduitIdleImguiProbeWindow>()
            : ScriptableObject.CreateInstance<ConduitIdleToolkitProbeWindow>();
        window.position = new Rect(100, 100, 400, 300);
        window.Show();
        int observed = 0;
        using var input = ConduitEditorInput.Subscribe(() => observed++);
        try
        {
            Assert.That(input, Is.Not.Null);
            yield return null;
            window.Focus();
            if (backend == IdleInputBackend.UiToolkit)
                window.rootVisualElement[0].Focus();
            yield return null;
            observed = 0;

            var mousePosition = window.rootVisualElement.worldBound.center;
            window.SendEvent(new Event { type = EventType.MouseMove, mousePosition = mousePosition });
            Assert.That(observed, Is.Zero);
            window.SendEvent(new Event { type = EventType.MouseDown, mousePosition = mousePosition, button = 0 });
            window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.A, character = 'a' });
            window.SendEvent(new Event { type = EventType.ScrollWheel, mousePosition = mousePosition, delta = Vector2.down });

            Assert.That(window.Consumed, Is.EqualTo(3));
            Assert.That(observed, Is.EqualTo(window.Consumed));
        }
        finally
        {
            window.Close();
            previousFocus?.Focus();
        }
    }

    [Test]
    public void IdleClose_SavePersistsSceneChangesBeforeClosing()
    {
        string assetPath = ConduitTestAssets.GetTemporaryPath("UnitTests", $"IdleSave_{Guid.NewGuid():N}.unity");
        CreateTemporaryScreenshotSceneAsset(assetPath);
        var scene = EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
        string objectName = $"IdleSaved_{Guid.NewGuid():N}";
        try
        {
            var created = new GameObject(objectName);
            SceneManager.MoveGameObjectToScene(created, scene);
            EditorSceneManager.MarkSceneDirty(scene);
            Assert.That(ConduitEditorSaveUtility.TrySave(), Is.True);
            EditorSceneManager.CloseScene(scene, true);
            scene = EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
            Assert.That(Array.Exists(scene.GetRootGameObjects(), obj => obj.name == objectName), Is.True);
        }
        finally
        {
            EditorSceneManager.CloseScene(scene, true);
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void IdleClose_SaveRecoversUntitledScenes()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        string recoveryPath = string.Empty;
        string objectName = "Idle recovery";
        try
        {
            var created = new GameObject(objectName);
            SceneManager.MoveGameObjectToScene(created, scene);
            EditorSceneManager.MarkSceneDirty(scene);

            Assert.That(ConduitEditorSaveUtility.TrySave(), Is.True);
            recoveryPath = scene.path;
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(recoveryPath), Is.Not.Null);
            EditorSceneManager.CloseScene(scene, true);
            scene = EditorSceneManager.OpenScene(recoveryPath, OpenSceneMode.Additive);
            Assert.That(Array.Exists(scene.GetRootGameObjects(), obj => obj.name == objectName), Is.True);
        }
        finally
        {
            EditorSceneManager.CloseScene(scene, true);
            if (!string.IsNullOrEmpty(recoveryPath))
                DeleteTemporaryAsset(recoveryPath);
        }
    }

    [Test]
    public void IdleClose_SavePersistsPrefabStageChanges()
    {
        string assetPath = ConduitTestAssets.GetTemporaryPath("UnitTests", $"IdleSave_{Guid.NewGuid():N}.prefab");
        var source = new GameObject("Idle prefab");
        PrefabStage? stage = null;
        try
        {
            PrefabUtility.SaveAsPrefabAsset(source, assetPath);
            UnityEngine.Object.DestroyImmediate(source);
            stage = PrefabStageUtility.OpenPrefab(assetPath);
            var created = new GameObject("Saved child");
            created.transform.SetParent(stage.prefabContentsRoot.transform);
            EditorSceneManager.MarkSceneDirty(stage.scene);

            Assert.That(ConduitEditorSaveUtility.TrySave(), Is.True);
            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            Assert.That(saved.transform.Find(created.name), Is.Not.Null);
        }
        finally
        {
            if (stage != null)
            {
                // a failed assertion must not leave a modal save prompt for this temporary prefab
                typeof(EditorSceneManager)
                    .GetMethod("ClearSceneDirtiness", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(null, new object[] { stage.scene });
                StageUtility.GoToMainStage();
            }
            if (source != null)
                UnityEngine.Object.DestroyImmediate(source);
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void IdleClose_AssetSaveVetoPreventsClosing()
    {
        string assetPath = ConduitTestAssets.GetTemporaryPath("UnitTests", $"IdleSave_{Guid.NewGuid():N}.asset");
        var asset = ScriptableObject.CreateInstance<ProjectSettingsArrayFixture>();
        try
        {
            AssetDatabase.CreateAsset(asset, assetPath);
            AssetDatabase.SaveAssets();
            EditorUtility.SetDirty(asset);
            ConduitIdleSaveVeto.AssetPath = assetPath;

            Assert.That(ConduitEditorSaveUtility.TrySave(), Is.False);
            Assert.That(EditorUtility.IsDirty(asset), Is.True);

            ConduitIdleSaveVeto.AssetPath = null;
            Assert.That(ConduitEditorSaveUtility.TrySave(), Is.True);
            Assert.That(EditorUtility.IsDirty(asset), Is.False);
        }
        finally
        {
            ConduitIdleSaveVeto.AssetPath = null;
            DeleteTemporaryAsset(assetPath);
        }
    }

    [Test]
    public void IdleClose_UnsavedWindowPreventsSavingAndClosing()
    {
        var window = ScriptableObject.CreateInstance<ConduitIdleImguiProbeWindow>();
        try
        {
            window.UnsavedChanges = true;
            Assert.That(ConduitEditorSaveUtility.TrySave(), Is.False);
            Assert.That(window.hasUnsavedChanges, Is.True);
        }
        finally
        {
            window.UnsavedChanges = false;
            UnityEngine.Object.DestroyImmediate(window);
        }
    }

    [Test]
    public void IdleClose_OutstandingWorkIncludesQueuedAndDisconnectedCommands()
    {
        Assert.That(ClientWorkSnapshot.Empty.HasOutstandingWork, Is.False);
        Assert.That(ClientWorkSnapshot.Create(new() { ClientID = 0 }, new(), false).HasOutstandingWork, Is.True);
        Assert.That(ClientWorkSnapshot.Create(null, new() { new() { ClientID = 1 } }, false).HasOutstandingWork, Is.True);
        Assert.That(ClientWorkSnapshot.Create(null, new(), true).HasOutstandingWork, Is.True);
    }
}

enum IdleInputBackend { Imgui, UiToolkit }

sealed class ConduitIdleSaveVeto : AssetModificationProcessor
{
    internal static string? AssetPath;

    static string[] OnWillSaveAssets(string[] paths)
        => AssetPath == null ? paths : Array.FindAll(paths, path => path != AssetPath);
}

abstract class ConduitIdleInputProbeWindow : EditorWindow
{
    internal int Consumed;
    internal bool UnsavedChanges { set => hasUnsavedChanges = value; }
}

sealed class ConduitIdleToolkitProbeWindow : ConduitIdleInputProbeWindow
{
    void CreateGUI()
    {
        var target = new VisualElement { focusable = true };
        target.StretchToParentSize();
        target.RegisterCallback<PointerDownEvent>(Consume);
        target.RegisterCallback<KeyDownEvent>(Consume);
        target.RegisterCallback<WheelEvent>(Consume);
        rootVisualElement.Add(target);
    }

    void Consume<T>(T evt) where T : EventBase<T>, new()
    {
        Consumed++;
        evt.StopImmediatePropagation();
    }
}

sealed class ConduitIdleImguiProbeWindow : ConduitIdleInputProbeWindow
{
    void OnGUI()
    {
        if (Event.current.type is not (EventType.MouseDown or EventType.KeyDown or EventType.ScrollWheel))
            return;

        Consumed++;
        Event.current.Use();
    }
}
