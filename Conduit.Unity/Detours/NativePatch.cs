#nullable enable

using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.IO;
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

    static class MemoryProtection
    {
        const uint PageExecuteReadWrite = 0x40;
        const int ProtRead = 1;
        const int ProtWrite = 2;
        const int ProtExecute = 4;

        internal static void MakeWritable(IntPtr address, int length, Action write)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (!VirtualProtect(address, (UIntPtr)(uint)length, PageExecuteReadWrite, out var oldProtection))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualProtect could not make Mono JIT code writable.");
                try
                {
                    write();
                }
                finally
                {
                    VirtualProtect(address, (UIntPtr)(uint)length, oldProtection, out _);
                }
                return;
            }

            int protection = ReadLinuxProtection(address);
            if ((protection & ProtWrite) != 0)
            {
                write();
                return;
            }

            int pageSize = Environment.SystemPageSize;
            long start = address.ToInt64() & -pageSize;
            long end = (address.ToInt64() + length + pageSize - 1) & -pageSize;
            ulong size = checked((ulong)(end - start));
            if (mprotect((IntPtr)start, (UIntPtr)size, ProtRead | ProtWrite | ProtExecute) != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "mprotect could not make Mono JIT code writable.");
            try
            {
                write();
            }
            finally
            {
                mprotect((IntPtr)start, (UIntPtr)size, protection);
            }
        }

        internal static void Flush(IntPtr address, int length)
        {
            // x64 Linux has coherent instruction and data caches; Windows exposes an explicit flush API.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;
            if (!FlushInstructionCache(GetCurrentProcess(), address, (UIntPtr)(uint)length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "FlushInstructionCache failed after the detour write.");
        }

        static int ReadLinuxProtection(IntPtr address)
        {
            ulong value = unchecked((ulong)address.ToInt64());
            foreach (var line in File.ReadLines("/proc/self/maps"))
            {
                int separator = line.IndexOf(' ');
                if (separator <= 0)
                    continue;
                var range = line.AsSpan(0, separator);
                int dash = range.IndexOf('-');
                if (dash <= 0
                    || !ulong.TryParse(range[..dash], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var start)
                    || !ulong.TryParse(range[(dash + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var end)
                    || value < start
                    || value >= end)
                    continue;
                var permissions = line.AsSpan(separator + 1);
                return (permissions.Length > 0 && permissions[0] == 'r' ? ProtRead : 0)
                       | (permissions.Length > 1 && permissions[1] == 'w' ? ProtWrite : 0)
                       | (permissions.Length > 2 && permissions[2] == 'x' ? ProtExecute : 0);
            }

            throw new InvalidOperationException("The Mono JIT memory mapping was not found in /proc/self/maps.");
        }

        [DllImport("libc", SetLastError = true)]
        static extern int mprotect(IntPtr address, UIntPtr length, int protection);

        [DllImport("kernel32", SetLastError = true)]
        static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtection, out uint oldProtection);

        [DllImport("kernel32")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32", SetLastError = true)]
        static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
    }
}
