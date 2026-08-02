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
    public static string Normalize(string? value)
    {
        if (PlayerSelector.TryParse(value, out var player))
            return player.ToString();

        if (value?.StartsWith("player:", StringComparison.Ordinal) == true)
            throw new ArgumentException($"Player selector '{value}' is malformed; player process IDs are positive integers.");

        return ProjectPathNormalizer.Normalize(value);
    }
}
