using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Conduit;

static partial class ProcessProtection
{
    const int PrSetDumpable = 4;

    public static void Enable()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("--protect-process is only supported on Linux.");

        if (Environment.GetEnvironmentVariable("DOTNET_EnableDiagnostics") != "0")
            throw new ArgumentException("Start Conduit with DOTNET_EnableDiagnostics=0 when using --protect-process.");

        if (prctl(PrSetDumpable, 0, 0, 0, 0) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not protect Conduit process memory.");

        // The runtime has read this setting; programs we launch should use their own settings.
        Environment.SetEnvironmentVariable("DOTNET_EnableDiagnostics", null);
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);
}
