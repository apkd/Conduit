#nullable enable

using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Conduit.Runtime
{
    static class RuntimeIpcPaths
    {
        internal static string GetRoot(bool wine)
        {
            if (Environment.GetEnvironmentVariable("CONDUIT_IPC_ROOT") is { Length: > 0 } configured)
                return wine ? ToWinePath(configured) : configured;

            if (wine)
            {
                var home = Environment.GetEnvironmentVariable("WINE_HOST_HOME")
                           ?? Environment.GetEnvironmentVariable("HOME")
                           ?? string.Empty;
                if (home.Length == 0)
                    throw new InvalidOperationException(
                        "Conduit could not resolve the Wine host home directory. Set CONDUIT_IPC_ROOT."
                    );

                return Path.Combine(ToWinePath(home), ".local", "state", "conduit", "ipc", "v1");
            }

            if (Application.platform == RuntimePlatform.WindowsPlayer)
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Conduit",
                    "ipc",
                    "v1"
                );

            if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtimeDirectory)
                return Path.Combine(runtimeDirectory, "conduit", "v1");

            return Path.Combine(Path.GetTempPath(), $"conduit-{getuid()}", "v1");
        }

        internal static void TryRestrictDirectory(string path)
        {
            if (Application.platform == RuntimePlatform.WindowsPlayer)
                return;

            try
            {
                chmod(path, 0x1c0); // 0700
            }
            catch (Exception) { }
        }

        static string ToWinePath(string path)
        {
            if (path.Length >= 2 && path[1] == ':')
                return path;

            var fallback = "Z:" + path.Replace('/', '\\');
            var converted = IntPtr.Zero;
            try
            {
                // respect custom Wine drive mappings instead of assuming that Z: exposes the host root
                converted = wine_get_dos_file_name(path);
                return converted == IntPtr.Zero
                    ? fallback
                    : Marshal.PtrToStringUni(converted) ?? fallback;
            }
            catch (Exception exception) when (
                exception is DllNotFoundException or EntryPointNotFoundException
            )
            {
                return fallback;
            }
            finally
            {
                if (converted != IntPtr.Zero)
                    HeapFree(GetProcessHeap(), 0, converted); // allocated on Wine's process heap
            }
        }

        [DllImport("kernel32.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr wine_get_dos_file_name(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string unixPath
        );

        [DllImport("kernel32.dll")]
        static extern IntPtr GetProcessHeap();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool HeapFree(IntPtr heap, uint flags, IntPtr memory);

        [DllImport("libc")]
        static extern uint getuid();

        [DllImport("libc")]
        static extern int chmod(string path, uint mode);
    }
}
