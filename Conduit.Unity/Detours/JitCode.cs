#nullable enable

using System;

namespace Conduit
{
    /// <summary>Bounds a compiled Mono method so a patch cannot extend into a neighboring native body.</summary>
    readonly struct JitCode
    {
        internal JitCode(IntPtr start, int size)
        {
            Start = start;
            Size = size;
        }

        internal IntPtr Start { get; }
        internal int Size { get; }
    }
}
