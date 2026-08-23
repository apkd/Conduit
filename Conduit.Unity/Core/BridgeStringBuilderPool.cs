#nullable enable

using System;
using System.Text;

namespace Conduit
{
    static class BridgeStringBuilderPool
    {
        const int MaximumRetainedCapacity = 256 * 1024;
        [ThreadStatic] static StringBuilder? cachedBuilder;

        internal static StringBuilderHandle Rent(
            out StringBuilder builder,
            int minimumCapacity = 0)
        {
            builder = cachedBuilder ?? new();
            cachedBuilder = null;
            builder.Clear();
            if (minimumCapacity > builder.Capacity)
                builder.EnsureCapacity(minimumCapacity);
            return new(builder);
        }

        static void Return(StringBuilder builder)
        {
            builder.Clear();
            // one buffer per thread avoids synchronization and bounds retained response memory
            if (builder.Capacity <= MaximumRetainedCapacity && cachedBuilder == null)
                cachedBuilder = builder;
        }

        internal struct StringBuilderHandle : IDisposable
        {
            StringBuilder? builder;

            internal StringBuilderHandle(StringBuilder builder) => this.builder = builder;

            public void Dispose()
            {
                if (builder == null)
                    return;

                var rented = builder;
                builder = null;
                Return(rented);
            }
        }
    }
}
