#nullable enable

using UnityEngine;

namespace Conduit
{
    static class BridgeObjectExtensions
    {
        internal static GameObject? AsGameObject(this Object target)
            => target switch
            {
                GameObject gameObject => gameObject,
                Component component => component.gameObject,
                _ => null,
            };
    }
}
