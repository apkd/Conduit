namespace Conduit;

internal readonly record struct UnityWindowTitleSignal(
    string Title,
    bool IsFocused,
    string Source
);
