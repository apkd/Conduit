#nullable enable

using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Conduit
{
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
