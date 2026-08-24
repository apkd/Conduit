#nullable enable

using System;
using Object = UnityEngine.Object;

namespace Conduit
{
    readonly struct ResolvedObjectMatch
    {
        internal ResolvedObjectMatch(
            Object? target,
            string name,
            string location,
            string? assetPath,
            ulong objectId,
            ResolvedObjectMatchSource source)
        {
            Target = target;
            Name = name;
            Location = location;
            AssetPath = assetPath;
            ObjectId = objectId;
            Source = source;
        }

        internal Object? Target { get; }
        internal string Name { get; }
        internal string Location { get; }
        internal string? AssetPath { get; }
        internal ulong ObjectId { get; }
        internal ResolvedObjectMatchSource Source { get; }

        internal Object RequireTarget()
            => Target != null
                ? Target
                : throw new InvalidOperationException($"Match '{Name}' does not reference a Unity object.");
    }
}
