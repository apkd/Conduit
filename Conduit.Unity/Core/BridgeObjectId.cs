#nullable enable

using System;
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

        public static Object? Resolve(ulong objectId)
        {
#if UNITY_6000_4_OR_NEWER
            var entityId = EntityId.FromULong(objectId);
            return entityId.IsValid() ? Resources.EntityIdToObject(entityId) : null;
#else
            return Resources.InstanceIDToObject(unchecked((int)objectId));
#endif
        }

        public static string Format(Object target) => Format(Get(target));

        public static string Format(ulong objectId)
        {
#if UNITY_6000_4_OR_NEWER
            var length = CountDigits(objectId);
            return string.Create(Prefix.Length + length, objectId, (destination, value) =>
            {
                Prefix.AsSpan().CopyTo(destination);
                value.TryFormat(
                    destination.Slice(Prefix.Length),
                    out _,
                    provider: CultureInfo.InvariantCulture
                );
            });
#else
            var signedObjectId = unchecked((int)objectId);
            var length = CountDigits(signedObjectId);
            return string.Create(Prefix.Length + length, signedObjectId, (destination, value) =>
            {
                Prefix.AsSpan().CopyTo(destination);
                value.TryFormat(
                    destination.Slice(Prefix.Length),
                    out _,
                    provider: CultureInfo.InvariantCulture
                );
            });
#endif
        }

#if UNITY_6000_4_OR_NEWER
        static int CountDigits(ulong value)
#else
        static int CountDigits(int value)
#endif
        {
#if !UNITY_6000_4_OR_NEWER
            var negative = value < 0;
            var magnitude = negative ? unchecked((uint)-(long)value) : (uint)value;
#else
            var magnitude = value;
#endif
            var count = 1;
            while (magnitude >= 10)
            {
                magnitude /= 10;
                count++;
            }

#if !UNITY_6000_4_OR_NEWER
            if (negative)
                count++;
#endif
            return count;
        }

        public static bool TryParse(string value, out ulong objectId)
            => TryParse(value.AsSpan(), out objectId);

        public static bool TryParse(ReadOnlySpan<char> value, out ulong objectId)
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
