namespace Conduit;

readonly record struct UnityEditorProcessRuntimeInfo(
    int ProcessId,
    DateTimeOffset StartedAtUtc
);
