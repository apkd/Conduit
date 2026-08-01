#nullable enable

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Conduit
{
    static class MonoJit
    {
        static readonly Lazy<Exports> exports = new(CreateExports);

        internal static JitCode GetCode(MethodBase method)
        {
            if (method.ContainsGenericParameters)
                throw new NotSupportedException("Generic method code cannot be detoured.");

            RuntimeHelpers.PrepareMethod(method.MethodHandle);
            var pointer = method.MethodHandle.GetFunctionPointer();
            if (pointer == IntPtr.Zero)
                throw new InvalidOperationException($"Mono did not return JIT code for '{method}'.");

            var api = exports.Value;
            var domain = api.DomainGet();
            var info = api.JitInfoTableFind(domain, pointer);
            if (info == IntPtr.Zero)
                throw new InvalidOperationException($"Mono JIT metadata was not found for '{method}'.");

            var start = api.JitInfoGetCodeStart(info);
            var size = checked((int)api.JitInfoGetCodeSize(info));
            if (start != pointer)
                throw new NotSupportedException(
                    $"Mono returned an alternate entry thunk for '{method}'; this call path cannot be patched safely."
                );
            if (size <= 0)
                throw new InvalidOperationException($"Mono reported an invalid JIT body size for '{method}'.");
            return new(start, size);
        }

        static Exports CreateExports() =>
            new(
                NativeSymbols.Resolve<MonoDomainGet>("mono_domain_get"),
                NativeSymbols.Resolve<MonoJitInfoTableFind>("mono_jit_info_table_find"),
                NativeSymbols.Resolve<MonoJitInfoGetCodeStart>("mono_jit_info_get_code_start"),
                NativeSymbols.Resolve<MonoJitInfoGetCodeSize>("mono_jit_info_get_code_size")
            );

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr MonoDomainGet();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr MonoJitInfoTableFind(IntPtr domain, IntPtr address);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr MonoJitInfoGetCodeStart(IntPtr info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate uint MonoJitInfoGetCodeSize(IntPtr info);

        sealed class Exports
        {
            internal Exports(
                MonoDomainGet domainGet,
                MonoJitInfoTableFind jitInfoTableFind,
                MonoJitInfoGetCodeStart jitInfoGetCodeStart,
                MonoJitInfoGetCodeSize jitInfoGetCodeSize)
            {
                DomainGet = domainGet;
                JitInfoTableFind = jitInfoTableFind;
                JitInfoGetCodeStart = jitInfoGetCodeStart;
                JitInfoGetCodeSize = jitInfoGetCodeSize;
            }

            internal MonoDomainGet DomainGet { get; }
            internal MonoJitInfoTableFind JitInfoTableFind { get; }
            internal MonoJitInfoGetCodeStart JitInfoGetCodeStart { get; }
            internal MonoJitInfoGetCodeSize JitInfoGetCodeSize { get; }
        }
    }

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
