#nullable enable

using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Conduit
{
    static partial class ScreenshotTool
    {
        static async Task<string> CaptureCameraAsync(Camera sourceCamera, string prefix)
        {
            EnsureCanRenderScreenshot(prefix);
            var width = Mathf.Max(1, sourceCamera.pixelWidth);
            var height = Mathf.Max(1, sourceCamera.pixelHeight);
            if (width <= 1 || height <= 1)
                (width, height) = GetDefaultCaptureSize(sourceCamera.aspect);

            var renderTexture = GpuCapture.CreateStagingTexture(width, height, 24);
            var previousTargetTexture = sourceCamera.targetTexture;
            var previousActive = RenderTexture.active;
            try
            {
                sourceCamera.targetTexture = renderTexture;
                sourceCamera.Render();
                RenderTexture.active = renderTexture;
                return await SaveRenderTextureAsync(renderTexture, prefix);
            }
            finally
            {
                RenderTexture.active = previousActive;
                sourceCamera.targetTexture = previousTargetTexture;
                renderTexture.Release();
                Object.DestroyImmediate(renderTexture);
            }
        }

        static async Task<string> CaptureGameObjectPreviewAsync(GameObject previewSource, string prefix)
        {
            EnsureCanRenderScreenshot(prefix);
            var previewScene = EditorSceneManager.NewPreviewScene();
            GameObject? previewInstance = null;
            try
            {
                previewInstance = Object.Instantiate(previewSource);
                previewInstance.hideFlags = HideFlags.HideAndDontSave;
                previewInstance.SetActive(true);
                SceneManager.MoveGameObjectToScene(previewInstance, previewScene);

                if (!TryCalculateRendererBounds(previewInstance, out var bounds))
                    throw new InvalidOperationException($"Target '{previewSource.name}' has no non-trivial renderer bounds to preview.");

                return await CaptureSceneBoundsAsync(previewScene, bounds, prefix, topDown: false);
            }
            finally
            {
                if (previewInstance != null)
                    Object.DestroyImmediate(previewInstance);

                EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

        static async Task<string> CaptureAssetPreviewAsync(Object target, string prefix)
        {
            EnsureCanRenderScreenshot(prefix);
            var previewTexture = AssetPreview.GetAssetPreview(target);
            var deadlineUtc = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (previewTexture == null
                   && IsLoadingAssetPreview(target)
                   && DateTime.UtcNow < deadlineUtc)
            {
                await Task.Delay(100);
                previewTexture = AssetPreview.GetAssetPreview(target);
            }

            previewTexture ??= AssetPreview.GetMiniThumbnail(target);
            if (previewTexture == null)
                throw new InvalidOperationException($"Unity could not generate a preview image for '{target.name}'.");

            return await SaveTextureAsync(previewTexture, prefix);
        }

        static bool IsLoadingAssetPreview(Object target)
        {
#if UNITY_6000_4_OR_NEWER
            return AssetPreview.IsLoadingAssetPreview(target.GetEntityId());
#else
            return AssetPreview.IsLoadingAssetPreview(target.GetInstanceID());
#endif
        }

        static async Task<string> CaptureSceneBoundsAsync(Scene scene, Bounds bounds, string prefix, bool topDown)
        {
            EnsureCanRenderScreenshot(prefix);
            var (width, height) = GetDefaultCaptureSize(16f / 9f);
            var renderTexture = GpuCapture.CreateStagingTexture(width, height, 24);
            GameObject? cameraObject = null;
            GameObject? keyLightObject = null;
            GameObject? fillLightObject = null;
            var previousActive = RenderTexture.active;

            try
            {
                cameraObject = CreateSceneObject("__ConduitScreenshotCamera", scene);
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.cameraType = CameraType.Preview;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new(0.18f, 0.18f, 0.18f, 1f);
                camera.allowHDR = false;
                camera.allowMSAA = true;
                camera.targetTexture = renderTexture;
                camera.overrideSceneCullingMask = EditorSceneManager.GetSceneCullingMask(scene);

                if (topDown)
                    ConfigureTopDownCamera(camera, bounds, (float)width / height);
                else
                    ConfigurePreviewCamera(camera, bounds);

                keyLightObject = CreateDirectionalLight(scene, "__ConduitScreenshotKeyLight", new(50f, 330f, 0f), 1.2f);
                fillLightObject = CreateDirectionalLight(scene, "__ConduitScreenshotFillLight", new(340f, 35f, 0f), 0.55f);

                camera.Render();
                RenderTexture.active = renderTexture;
                return await SaveRenderTextureAsync(renderTexture, prefix);
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (cameraObject != null)
                    Object.DestroyImmediate(cameraObject);

                if (keyLightObject != null)
                    Object.DestroyImmediate(keyLightObject);

                if (fillLightObject != null)
                    Object.DestroyImmediate(fillLightObject);

                renderTexture.Release();
                Object.DestroyImmediate(renderTexture);
            }
        }

        static void ConfigureTopDownCamera(Camera camera, Bounds bounds, float aspect)
        {
            var orthographicSize = Mathf.Max(bounds.extents.z, bounds.extents.x / Mathf.Max(aspect, 0.01f));
            orthographicSize = Mathf.Max(orthographicSize * 1.15f, 1f);

            var elevation = Mathf.Max(bounds.size.y + orthographicSize * 2.5f, 10f);
            camera.orthographic = true;
            camera.orthographicSize = orthographicSize;
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = elevation + bounds.extents.y * 4f + 100f;
            camera.transform.position = bounds.center + Vector3.up * elevation;
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        static void ConfigurePreviewCamera(Camera camera, Bounds bounds)
        {
            // unity's built-in quads and sprites face -z; view them from that side and from above
            var viewDirection = new Vector3(-1f, -0.75f, 1f).normalized;
            var radius = Mathf.Max(bounds.extents.magnitude, 0.5f);
            const float fieldOfView = 30f;
            var distance = radius / Mathf.Sin(fieldOfView * 0.5f * Mathf.Deg2Rad);
            distance = Mathf.Max(distance * 1.15f, 2f);

            camera.orthographic = false;
            camera.fieldOfView = fieldOfView;
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = distance + radius * 4f + 50f;
            camera.transform.position = bounds.center - viewDirection * distance;
            camera.transform.rotation = Quaternion.LookRotation(viewDirection, Vector3.up);
        }

        static GameObject CreateDirectionalLight(Scene scene, string name, Vector3 eulerAngles, float intensity)
        {
            var lightObject = CreateSceneObject(name, scene);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = intensity;
            light.color = Color.white;
            light.transform.rotation = Quaternion.Euler(eulerAngles);
            return lightObject;
        }

        static GameObject CreateSceneObject(string name, Scene scene)
        {
            var gameObject = new GameObject(name)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };

            SceneManager.MoveGameObjectToScene(gameObject, scene);
            return gameObject;
        }

        static bool TryGetSceneCamera(Object target, out Camera camera)
        {
            switch (target)
            {
                case Camera sceneCamera when !EditorUtility.IsPersistent(sceneCamera):
                {
                    camera = sceneCamera;
                    return true;
                }
                case GameObject gameObject when !EditorUtility.IsPersistent(gameObject) && gameObject.TryGetComponent(out Camera sceneCameraOnGameObject):
                {
                    camera = sceneCameraOnGameObject;
                    return true;
                }
                default:
                {
                    camera = null!;
                    return false;
                }
            }
        }

        static bool TryGetPreviewSource(Object target, out GameObject previewSource)
        {
            switch (target)
            {
                case GameObject gameObject:
                {
                    previewSource = gameObject;
                    return true;
                }
                case Component component:
                {
                    previewSource = component.gameObject;
                    return true;
                }
                default:
                {
                    previewSource = null!;
                    return false;
                }
            }
        }

        static bool TryCalculateSceneBounds(Scene scene, out Bounds bounds)
        {
            bounds = default;
            bool hasBounds = false;
            using var pooledRoots = ConduitPool.GetPooledList<GameObject>(out var roots);
            using var pooledRenderers = ConduitPool.GetPooledList<Renderer>(out var renderers);
            scene.GetRootGameObjects(roots);

            foreach (var root in roots)
            {
                if (root == null)
                    continue;

                renderers.Clear();
                root.GetComponentsInChildren(true, renderers);
                foreach (var renderer in renderers)
                {
                    if (renderer == null)
                        continue;

                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
                        continue;

                    if (!TryGetMeaningfulBounds(renderer.bounds, out var rendererBounds))
                        continue;

                    if (hasBounds)
                    {
                        bounds.Encapsulate(rendererBounds);
                    }
                    else
                    {
                        bounds = rendererBounds;
                        hasBounds = true;
                    }
                }
            }

            return hasBounds;
        }

        static bool TryCalculateRendererBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            bool hasBounds = false;
            using var handle = ConduitPool.GetPooledList<Renderer>(out var renderers);
            root.GetComponentsInChildren(true, renderers);
            foreach (var renderer in renderers)
            {
                if (renderer == null)
                    continue;

                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;

                if (!TryGetMeaningfulBounds(renderer.bounds, out var rendererBounds))
                    continue;

                if (hasBounds)
                {
                    bounds.Encapsulate(rendererBounds);
                }
                else
                {
                    bounds = rendererBounds;
                    hasBounds = true;
                }
            }

            return hasBounds;
        }

        static bool TryGetMeaningfulBounds(Bounds candidate, out Bounds bounds)
        {
            bounds = candidate;
            var size = candidate.size;
            var center = candidate.center;
            if (!float.IsFinite(size.x)
                || !float.IsFinite(size.y)
                || !float.IsFinite(size.z)
                || !float.IsFinite(center.x)
                || !float.IsFinite(center.y)
                || !float.IsFinite(center.z))
                return false;

            float squaredSize = size.sqrMagnitude;
            // flat renderers such as quads and sprites remain valid when their total size is useful
            return float.IsFinite(squaredSize)
                   && squaredSize > BoundsSizeEpsilon * BoundsSizeEpsilon;
        }

    }
}
