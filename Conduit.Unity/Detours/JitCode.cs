#nullable enable

using System;

namespace Conduit
{
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
