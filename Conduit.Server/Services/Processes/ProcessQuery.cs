namespace Conduit;

static class ProcessQuery
{
    internal static bool TryQueryProcessesByName(string processName, out UnityProjectProcessInfo[] processes)
    {
#if CONDUIT_WINDOWS
        return WindowsProcessQuery.TryQueryProcessesByName(processName, out processes);
#elif CONDUIT_LINUX
        return LinuxProcessQuery.TryQueryProcessesByName(processName, out processes);
#else
        processes = [];
        return false;
#endif
    }
}
