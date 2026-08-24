namespace Conduit;

readonly record struct UnityProjectProcessInfo(
    int ProcessId,
    string? ExecutablePath,
    string? CommandLine
);
