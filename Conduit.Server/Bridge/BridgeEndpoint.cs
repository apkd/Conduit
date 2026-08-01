namespace Conduit;

readonly record struct PlayerSelector(int ProcessId)
{
    const string Prefix = "player:";

    public static bool TryParse(string? value, out PlayerSelector selector)
    {
        selector = default;
        if (value is null
            || !value.StartsWith(Prefix, StringComparison.Ordinal)
            || !int.TryParse(value.AsSpan(Prefix.Length), out var processId)
            || processId <= 0)
            return false;

        selector = new(processId);
        return true;
    }

    public static string Format(int processId) => Prefix + processId;

    public override string ToString() => Format(ProcessId);
}

static class BridgeTarget
{
    public static string Normalize(string? value) =>
        PlayerSelector.TryParse(value, out var player)
            ? player.ToString()
            : ProjectPathNormalizer.Normalize(value);
}
