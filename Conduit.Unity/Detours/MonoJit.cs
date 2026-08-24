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
}
