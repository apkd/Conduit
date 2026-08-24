#nullable enable

using System;

namespace Conduit
{
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
