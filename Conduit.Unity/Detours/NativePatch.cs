#nullable enable

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading;

namespace Conduit
{
    static unsafe class NativePatch
    {
        internal static PatchPlan Plan(JitCode target, IntPtr replacement, byte[]? original = null)
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new PlatformNotSupportedException("Runtime method detouring supports x64 processes only.");
            if (target.Size < 5)
                throw new NotSupportedException(
                    $"The target JIT body is {target.Size} bytes; at least 5 bytes are required."
                );

            int span = target.Size >= 14 ? 14 : target.Size >= 8 ? 8 : 5;
            var saved = original ?? Read(target.Start, span);
            if (saved.Length != span)
                throw new InvalidOperationException("The saved target prefix no longer matches the JIT body size.");
            var desired = (byte[])saved.Clone();
            long displacement = replacement.ToInt64() - (target.Start.ToInt64() + 5L);
            if (displacement is >= int.MinValue and <= int.MaxValue)
            {
                desired[0] = 0xe9;
                BinaryPrimitives.WriteInt32LittleEndian(desired.AsSpan(1, 4), (int)displacement);
                return new(target.Start, saved, desired, PatchKind.Relative);
            }

            if (span < 14)
                throw new NotSupportedException(
                    $"The replacement is outside the ±2 GiB relative-jump range and the {target.Size}-byte target is too short for a 14-byte absolute jump."
                );
            desired[0] = 0xff;
            desired[1] = 0x25;
            desired.AsSpan(2, 4).Clear();
            BinaryPrimitives.WriteInt64LittleEndian(desired.AsSpan(6, 8), replacement.ToInt64());
            return new(target.Start, saved, desired, PatchKind.Absolute);
        }

        internal static void Install(PatchPlan previous, PatchPlan next)
            => Write(previous.Address, previous.Installed, next.Installed);

        internal static void Restore(PatchPlan plan)
            => Write(plan.Address, plan.Installed, plan.Original);

        internal static bool IsInstalled(PatchPlan plan)
            => Read(plan.Address, plan.Installed.Length).AsSpan().SequenceEqual(plan.Installed);

        static void Write(IntPtr address, byte[] expected, byte[] desired)
        {
            if (expected.Length != desired.Length)
                throw new InvalidOperationException("Patch prefixes have different lengths.");
            if (!Read(address, expected.Length).AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException("The target code changed after the detour was prepared.");

            MemoryProtection.MakeWritable(
                address,
                desired.Length,
                () =>
                {
                    if (!Read(address, expected.Length).AsSpan().SequenceEqual(expected))
                        throw new InvalidOperationException("The target code changed while installing the detour.");

                    if (CanWriteAtomically(address, expected, desired))
                    {
                        // the common aligned relative jump fits in one compare-exchange.
                        long expectedWord = BinaryPrimitives.ReadInt64LittleEndian(expected);
                        long desiredWord = BinaryPrimitives.ReadInt64LittleEndian(desired);
                        ref var location = ref *(long*)address;
                        if (Interlocked.CompareExchange(ref location, desiredWord, expectedWord) != expectedWord)
                            throw new InvalidOperationException("The target code changed during the atomic detour write.");
                    }
                    else
                        // unaligned and absolute patches require a multi-byte write; callers must
                        // avoid invoking the target concurrently while applying or restoring it.
                        Marshal.Copy(desired, 0, address, desired.Length);

                    Thread.MemoryBarrier();
                    MemoryProtection.Flush(address, desired.Length);
                }
            );
        }

        static bool CanWriteAtomically(IntPtr address, byte[] expected, byte[] desired)
        {
            if ((address.ToInt64() & 7) != 0 || expected.Length < 8)
                return false;
            for (int index = 8; index < expected.Length; ++index)
                if (expected[index] != desired[index])
                    return false;
            return true;
        }

        static byte[] Read(IntPtr address, int length)
        {
            var bytes = new byte[length];
            Marshal.Copy(address, bytes, 0, length);
            return bytes;
        }
    }
}
