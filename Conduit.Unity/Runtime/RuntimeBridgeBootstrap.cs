#nullable enable

#if MODULE_SCREENCAPTURE
using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
#endif
using Unity.Profiling;
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

        public static void RequestQuit()
        {
            if (behaviour != null)
                behaviour.RequestQuit();
        }

#if MODULE_SCREENCAPTURE
        public static Task<Texture2D> CaptureScreenshotAsync(CancellationToken ct) =>
            behaviour != null
                ? behaviour.CaptureScreenshotAsync(ct)
                : Task.FromException<Texture2D>(
                    new InvalidOperationException("The Conduit player bridge is not initialized.")
                );
#endif
    }

    sealed class RuntimeBridgeBehaviour : MonoBehaviour
    {
        RuntimeBridgeEndpoint? endpoint;
        ProfilerMarker marker;
        float quitAt;

        void Awake()
        {
            // bridge requests must keep advancing when the Development player is unfocused
            Application.runInBackground = true;
            endpoint = new();
            marker = new($"Conduit.Player.{endpoint.SessionInstanceId}");
            endpoint.Start();
        }

        void Update()
        {
            marker.Begin();
            marker.End();
            RuntimeBridgeDispatcher.Pump();
            if (quitAt > 0 && Time.realtimeSinceStartup >= quitAt)
                Application.Quit();
        }

        void OnDestroy()
        {
            endpoint?.Dispose();
            endpoint = null;
        }

        public void RequestQuit() => quitAt = Time.realtimeSinceStartup + 0.5f;

#if MODULE_SCREENCAPTURE
        public Task<Texture2D> CaptureScreenshotAsync(CancellationToken ct)
        {
            var completion = new TaskCompletionSource<Texture2D>();
            StartCoroutine(CaptureScreenshotAtEndOfFrame(completion, ct));
            return completion.Task;
        }

        static IEnumerator CaptureScreenshotAtEndOfFrame(
            TaskCompletionSource<Texture2D> completion,
            CancellationToken ct)
        {
            yield return new WaitForEndOfFrame();
            if (ct.IsCancellationRequested)
            {
                completion.TrySetCanceled();
                yield break;
            }

            try
            {
                Texture2D? texture = null;
                if (!Application.isBatchMode)
                    texture = ScreenCapture.CaptureScreenshotAsTexture();
                if (texture == null)
                {
                    var camera = Camera.main
                                 ?? UnityEngine.Object.FindAnyObjectByType<Camera>();
                    if (camera == null)
                        throw new InvalidOperationException(
                            "Unity did not produce a player screenshot texture and no camera is available."
                        );
                    texture = RuntimeToolDispatcher.CaptureCamera(camera);
                }
                completion.TrySetResult(texture);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }
#endif
    }
}
