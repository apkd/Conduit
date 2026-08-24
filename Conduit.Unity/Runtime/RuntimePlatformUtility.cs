#nullable enable

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Conduit.Runtime
{
    static class RuntimePlatformUtility
    {
        internal static bool IsWine()
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer)
                return false;

            try
            {
                return wine_get_version() != IntPtr.Zero;
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }

        [DllImport("ntdll.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr wine_get_version();
    }
}
