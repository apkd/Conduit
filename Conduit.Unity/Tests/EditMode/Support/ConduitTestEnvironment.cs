#nullable enable

using UnityEngine;
using UnityEngine.Rendering;

static class ConduitTestEnvironment
{
    internal static bool SupportsRenderedScreenshots
        => !Application.isBatchMode
           && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
}
