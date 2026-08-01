#nullable enable

using System.Globalization;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{
    /// <summary>Formats and parses Unity object identifiers consistently in the Editor and player.</summary>
    static class BridgeObjectId
    {
#if UNITY_6000_2_OR_NEWER
        public const string Prefix = "eid:";
#else
        public const string Prefix = "id:";
#endif

        public static ulong Get(Object target)
        {
#if UNITY_6000_4_OR_NEWER
            return EntityId.ToULong(target.GetEntityId());
#elif UNITY_6000_2_OR_NEWER
            return unchecked((uint)(int)target.GetEntityId());
#else
            return unchecked((uint)target.GetInstanceID());
#endif
        }

        public static string Format(Object target) => Format(Get(target));

        public static string Format(ulong objectId)
        {
#if UNITY_6000_4_OR_NEWER
            return Prefix + objectId.ToString(CultureInfo.InvariantCulture);
#else
            return Prefix + unchecked((int)objectId).ToString(CultureInfo.InvariantCulture);
#endif
        }

        public static bool TryParse(string value, out ulong objectId)
        {
#if UNITY_6000_4_OR_NEWER
            return ulong.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out objectId
            );
#else
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed))
            {
                objectId = unchecked((uint)signed);
                return true;
            }

            if (uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsigned))
            {
                objectId = unsigned;
                return true;
            }

            objectId = 0;
            return false;
#endif
        }
    }
}
