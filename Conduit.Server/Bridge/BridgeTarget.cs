namespace Conduit;

static class BridgeTarget
{
    public static string Normalize(string? value)
    {
        if (PlayerSelector.TryParse(value, out var player))
            return player.ToString();

        if (value?.StartsWith("player:", StringComparison.Ordinal) == true)
            throw new ArgumentException(
                $"Player selector '{value}' is malformed; player process IDs are positive integers."
            );

        return ProjectPathNormalizer.Normalize(value);
    }
}
