#nullable enable

using System;
#if MODULE_IMGUI
using System.Reflection;
using UnityEditor;
using UnityEngine;
#endif

namespace Conduit
{
    static class ConduitEditorInput
    {
        internal static IDisposable? Subscribe(Action onUserInput)
        {
#if MODULE_IMGUI
            // globalEventHandler misses events consumed by controls; this hook runs before either UI system
            var field = typeof(EditorWindow).Assembly.GetType("UnityEditor.GUIView")
                            ?.GetField("beforeEventProcessed", BindingFlags.Static | BindingFlags.NonPublic)
                        ?? typeof(GUIUtility).GetField("beforeEventProcessed", BindingFlags.Static | BindingFlags.NonPublic);
            return field?.FieldType == typeof(Action<EventType, KeyCode, EventModifiers>)
                ? new Subscription(field, onUserInput)
                : null;
#else
            return null;
#endif
        }

#if MODULE_IMGUI
        internal static bool IsUserInput(EventType type, KeyCode key, EventModifiers modifiers)
            => type switch
            {
                EventType.MouseDown or EventType.MouseDrag or EventType.ScrollWheel
                    or EventType.DragPerform or EventType.TouchDown or EventType.TouchMove => true,
                EventType.KeyDown when key is not (KeyCode.LeftShift or KeyCode.RightShift
                    or KeyCode.LeftControl or KeyCode.RightControl or KeyCode.LeftAlt or KeyCode.RightAlt
                    or KeyCode.LeftCommand or KeyCode.RightCommand or KeyCode.LeftWindows or KeyCode.RightWindows)
                    => key != KeyCode.Tab || (modifiers & (EventModifiers.Alt | EventModifiers.Command)) == 0,
                _ => false,
            };

        sealed class Subscription : IDisposable
        {
            readonly FieldInfo field;
            readonly Action<EventType, KeyCode, EventModifiers> handler;

            internal Subscription(FieldInfo field, Action onUserInput)
            {
                this.field = field;
                handler = (type, key, modifiers) =>
                {
                    if (IsUserInput(type, key, modifiers))
                        onUserInput();
                };
                field.SetValue(null, Delegate.Combine((Delegate?)field.GetValue(null), handler));
            }

            public void Dispose() => field.SetValue(null, Delegate.Remove((Delegate?)field.GetValue(null), handler));
        }
#endif
    }
}
