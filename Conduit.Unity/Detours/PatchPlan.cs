#nullable enable

using System;

namespace Conduit
{
    /// <summary>Pairs the bytes to restore with the exact installed prefix expected at a native method entry.</summary>
    /// <remarks>Original remains unchanged across replacement updates. Installed is compared before writing
    /// so restoration cannot silently overwrite another patch owner's changes.</remarks>
    sealed class PatchPlan
    {
        internal PatchPlan(IntPtr address, byte[] original, byte[] installed, PatchKind kind)
        {
            Address = address;
            Original = original;
            Installed = installed;
            Kind = kind;
        }

        internal IntPtr Address { get; }
        internal byte[] Original { get; }
        internal byte[] Installed { get; }
        internal PatchKind Kind { get; }
    }

    enum PatchKind : byte
    {
        Relative,
        Absolute,
    }
}
