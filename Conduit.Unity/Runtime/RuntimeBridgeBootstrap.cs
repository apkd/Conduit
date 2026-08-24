#nullable enable

#if MODULE_SCREENCAPTURE
using System;
using System.Threading;
using System.Threading.Tasks;
#endif
using UnityEngine;

namespace Conduit.Runtime
{
    static class RuntimeBridgeBootstrap
    {
        static RuntimeBridgeBehaviour? behaviour;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Initialize()
        {
            if (Application.isEditor || behaviour != null)
                return;

            var gameObject = new GameObject("Conduit Player Bridge")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
            behaviour = gameObject.AddComponent<RuntimeBridgeBehaviour>();
        }

        internal static void RequestQuit()
        {
            if (behaviour != null)
                behaviour.RequestQuit();
        }

#if MODULE_SCREENCAPTURE
        internal static Task<Texture2D> CaptureScreenshotAsync(CancellationToken ct) =>
            behaviour != null
                ? behaviour.CaptureScreenshotAsync(ct)
                : Task.FromException<Texture2D>(
                    new InvalidOperationException("The Conduit player bridge is not initialized.")
                );
#endif
    }
}
