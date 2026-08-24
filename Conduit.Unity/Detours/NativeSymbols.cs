#nullable enable

using System;
using System.Runtime.InteropServices;

namespace Conduit
{
    static class NativeSymbols
    {
        internal static T Resolve<T>(string name) where T : Delegate
        {
            // unity exports Mono symbols from the main process on Linux and several possible modules on Windows.
            var pointer = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? ResolveWindows(name)
                : dlsym(IntPtr.Zero, name);
            if (pointer == IntPtr.Zero)
                throw new EntryPointNotFoundException($"Unity Mono export '{name}' was not found.");
            return (T)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T));
        }

        static IntPtr ResolveWindows(string name)
        {
            var modules = new string?[]
            {
                "mono-2.0-bdwgc.dll",
                "mono.dll",
                "UnityPlayer.dll",
                null,
            };
            foreach (var moduleName in modules)
            {
                var module = GetModuleHandle(moduleName);
                if (module == IntPtr.Zero)
                    continue;
                var symbol = GetProcAddress(module, name);
                if (symbol != IntPtr.Zero)
                    return symbol;
            }

            return IntPtr.Zero;
        }

        [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr dlsym(IntPtr handle, string symbol);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string procedureName);
    }
}
