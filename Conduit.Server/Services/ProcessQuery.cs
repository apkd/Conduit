using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes;

namespace Conduit;

static class ProcessQuery
{
    const uint ProcessVmRead = 0x0010;
    const uint ProcessQueryLimitedInformation = 0x1000;

    public static bool TryQueryProcessesByName(string processName, out UnityProjectProcessInfo[] processes)
    {
#if CONDUIT_WINDOWS
        return TryQueryProcessesByNameWindows(processName, out processes);
#elif CONDUIT_LINUX
        return TryQueryProcessesByNameLinux(processName, out processes);
#else
        processes = [];
        return false;
#endif
    }

#if CONDUIT_WINDOWS
    static bool TryQueryProcessesByNameWindows(string processName, out UnityProjectProcessInfo[] processes)
    {
        processes = [];
        if (!OperatingSystem.IsWindows())
            return false;

        Process[] snapshot;
        try
        {
            snapshot = Process.GetProcessesByName(processName);
        }
        catch
        {
            return false;
        }

        try
        {
            if (snapshot.Length == 0)
            {
                processes = [];
                return true;
            }

            var results = new List<UnityProjectProcessInfo>(snapshot.Length);
            foreach (var process in snapshot)
            {
                try
                {
                    if (process.HasExited)
                        continue;

                    using var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, process.Id);
                    if (handle.IsInvalid)
                        continue;

                    results.Add(
                        new(
                            process.Id,
                            TryReadExecutablePath(handle),
                            TryReadCommandLine(handle)
                        )
                    );
                }
                catch { }
            }

            processes = results.Count == 0 ? [] : results.ToArray();
            return true;
        }
        finally
        {
            foreach (var process in snapshot)
                process.Dispose();
        }
    }

    static string? TryReadExecutablePath(SafeProcessHandle handle)
    {
        var capacity = 260u;
        while (capacity <= 32768)
        {
            var builder = new StringBuilder((int)capacity);
            var actualLength = capacity;
            if (QueryFullProcessImageName(handle, 0, builder, ref actualLength))
                return actualLength == 0 ? null : builder.ToString(0, (int)actualLength);

            if (Marshal.GetLastWin32Error() != 122)
                return null;

            capacity *= 2;
        }

        return null;
    }

    static string? TryReadCommandLine(SafeProcessHandle handle)
    {
        if (NtQueryInformationProcess(
                handle,
                0,
                out ProcessBasicInformation basicInformation,
                Marshal.SizeOf<ProcessBasicInformation>(),
                out _
            )
            != 0)
            return null;

        if (!TryReadStruct(handle, basicInformation.PebBaseAddress, out Peb peb) || peb.ProcessParameters == IntPtr.Zero)
            return null;

        if (!TryReadStruct(handle, peb.ProcessParameters, out RtlUserProcessParameters processParameters))
            return null;

        return TryReadUnicodeString(handle, processParameters.CommandLine);
    }

    static string? TryReadUnicodeString(SafeProcessHandle handle, UnicodeString value)
    {
        if (value.Length == 0 || value.Buffer == IntPtr.Zero)
            return string.Empty;

        var buffer = new byte[value.Length];
        if (!ReadProcessMemory(handle, value.Buffer, buffer, buffer.Length, out var bytesRead)
            || bytesRead.ToInt64() < buffer.Length)
            return null;

        return Encoding.Unicode.GetString(buffer);
    }

    static bool TryReadStruct<
        [DynamicallyAccessedMembers(PublicConstructors | NonPublicConstructors)]
        T>(SafeProcessHandle handle, IntPtr address, out T value)
        where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = new byte[size];
        if (!ReadProcessMemory(handle, address, buffer, size, out var bytesRead) || bytesRead.ToInt64() < size)
        {
            value = default;
            return false;
        }

        var pinnedBuffer = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            value = Marshal.PtrToStructure<T>(pinnedBuffer.AddrOfPinnedObject());
            return true;
        }
        finally
        {
            pinnedBuffer.Free();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        StringBuilder executablePath,
        ref uint size
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ReadProcessMemory(
        SafeProcessHandle process,
        IntPtr baseAddress,
        byte[] buffer,
        int size,
        out IntPtr bytesRead
    );

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(
        SafeProcessHandle process,
        int processInformationClass,
        out ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength
    );

    [StructLayout(LayoutKind.Sequential)]
    readonly struct ProcessBasicInformation
    {
        readonly IntPtr reserved1;
        public readonly IntPtr PebBaseAddress;
        readonly IntPtr reserved2_0;
        readonly IntPtr reserved2_1;
        readonly IntPtr uniqueProcessId;
        readonly IntPtr inheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    readonly struct Peb
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        readonly byte[] reserved1;

        readonly byte beingDebugged;
        readonly byte reserved2;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        readonly IntPtr[] reserved3;

        readonly IntPtr ldr;
        public readonly IntPtr ProcessParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    readonly struct RtlUserProcessParameters
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        readonly byte[] reserved1;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
        readonly IntPtr[] reserved2;

        readonly UnicodeString imagePathName;
        public readonly UnicodeString CommandLine;
    }

    [StructLayout(LayoutKind.Sequential)]
    readonly struct UnicodeString
    {
        public readonly ushort Length;
        readonly ushort maximumLength;
        public readonly IntPtr Buffer;
    }
#endif

#if CONDUIT_LINUX
    static bool TryQueryProcessesByNameLinux(string processName, out UnityProjectProcessInfo[] processes)
    {
        processes = [];
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(processName) || !Directory.Exists("/proc"))
            return false;

        try
        {
            var results = new List<UnityProjectProcessInfo>();
            foreach (var directoryPath in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directoryPath), out var processId))
                    continue;

                if (!TryReadProcessInfo(directoryPath, processId, processName, out var processInfo))
                    continue;

                results.Add(processInfo);
            }

            processes = results.Count == 0 ? [] : results.ToArray();
            return true;
        }
        catch
        {
            return false;
        }
    }

    static bool TryReadProcessInfo(string directoryPath, int processId, string processName, out UnityProjectProcessInfo processInfo)
    {
        processInfo = null!;
        var executablePath = TryReadExecutablePath(directoryPath);
        if (!MatchesProcessName(processName, TryReadComm(directoryPath), executablePath))
            return false;

        processInfo = new(processId, executablePath, TryReadCommandLine(directoryPath));
        return true;
    }

    static bool MatchesProcessName(string processName, string? comm, string? executablePath)
    {
        if (MatchesProcessName(processName, comm))
            return true;

        if (MatchesProcessName(processName, Path.GetFileNameWithoutExtension(comm)))
            return true;

        return MatchesProcessName(processName, Path.GetFileName(executablePath))
               || MatchesProcessName(processName, Path.GetFileNameWithoutExtension(executablePath));
    }

    static bool MatchesProcessName(string processName, string? candidate)
        => !string.IsNullOrWhiteSpace(candidate)
           && string.Equals(candidate, processName, StringComparison.OrdinalIgnoreCase);

    static string? TryReadExecutablePath(string directoryPath)
    {
        try
        {
            var target = File.ResolveLinkTarget(Path.Combine(directoryPath, "exe"), true);
            return target?.FullName;
        }
        catch
        {
            return null;
        }
    }

    static string? TryReadComm(string directoryPath)
    {
        try
        {
            var processName = File.ReadAllText(Path.Combine(directoryPath, "comm")).Trim();
            return processName.Length == 0 ? null : processName;
        }
        catch
        {
            return null;
        }
    }

    static string? TryReadCommandLine(string directoryPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(directoryPath, "cmdline"));
            if (bytes.Length == 0)
                return null;

            var builder = new StringBuilder(bytes.Length);
            var argumentStart = 0;
            for (var index = 0; index < bytes.Length; index++)
            {
                if (bytes[index] != 0)
                    continue;

                AppendArgument(bytes.AsSpan(argumentStart, index - argumentStart), builder);
                argumentStart = index + 1;
            }

            if (argumentStart < bytes.Length)
                AppendArgument(bytes.AsSpan(argumentStart), builder);

            return builder.Length == 0 ? null : builder.ToString();
        }
        catch
        {
            return null;
        }
    }

    static void AppendArgument(ReadOnlySpan<byte> argumentBytes, StringBuilder builder)
    {
        if (argumentBytes.Length == 0)
            return;

        if (builder.Length > 0)
            builder.Append(' ');

        var argument = Encoding.UTF8.GetString(argumentBytes);
        if (!RequiresQuoting(argument))
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        foreach (var character in argument)
        {
            if (character is '\\' or '"')
                builder.Append('\\');

            builder.Append(character);
        }

        builder.Append('"');
    }

    static bool RequiresQuoting(string value)
    {
        foreach (var character in value)
            if (char.IsWhiteSpace(character) || character is '"' or '\\')
                return true;

        return value.Length == 0;
    }
#endif
}
