#nullable enable

using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{
    static class ConduitObjectId
    {
        internal static ulong GetObjectId(Object target) => BridgeObjectId.Get(target);

        /// <summary>
        /// Formats an object identifier for display in tool output.
        /// </summary>
        internal static string FormatObjectId(ulong objectId)
            => BridgeObjectId.Format(objectId);

        /// <summary>Resolves an object identifier produced by <see cref="GetObjectId(Object)"/>.</summary>
        internal static Object? ResolveObjectId(ulong objectId)
        {
#if UNITY_6000_4_OR_NEWER
            var entityId = EntityId.FromULong(objectId);
            return entityId.IsValid()
                ? UnityEditor.EditorUtility.EntityIdToObject(entityId)
                : null;
#elif UNITY_6000_3_OR_NEWER
            var entityId = (EntityId)unchecked((int)objectId);
            return entityId.IsValid()
                ? UnityEditor.EditorUtility.EntityIdToObject(entityId)
                : null;
#elif UNITY_6000_2_OR_NEWER
            var entityId = (EntityId)unchecked((int)objectId);
            return entityId.IsValid()
                ? UnityEditor.EditorUtility.InstanceIDToObject(unchecked((int)objectId))
                : null;
#else
            return UnityEditor.EditorUtility.InstanceIDToObject(unchecked((int)objectId));
#endif
        }

        /// <summary>
        /// Formats the identifier of a Unity object for display in tool output.
        /// </summary>
        internal static string FormatObjectId(Object target) => FormatObjectId(GetObjectId(target));

    }
}
