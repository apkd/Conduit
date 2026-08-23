using System.Collections.Concurrent;

namespace Conduit;

readonly record struct UnityProjectIdentity(
    string CloudProjectId,
    string CompanyName,
    string ProductName)
{
    const int IdentityCachePruneThreshold = 64;
    static readonly ConcurrentDictionary<string, CachedProjectIdentity> identityCache =
        new(StringComparer.OrdinalIgnoreCase);

    public bool Matches(BridgeEndpointDescriptor player) =>
        string.IsNullOrWhiteSpace(player.CloudProjectId)
            ? NormalizedEquals(player.CompanyName, CompanyName)
              && NormalizedEquals(player.ProductName, ProductName)
            : NormalizedEquals(player.CloudProjectId, CloudProjectId);

    public static UnityProjectIdentity Read(string projectPath)
    {
        var settingsPath = Path.Combine(
            ProjectPathNormalizer.ToPlatformPath(projectPath),
            "ProjectSettings",
            "ProjectSettings.asset"
        );

        var companyName = string.Empty;
        var productName = string.Empty;
        var cloudProjectId = string.Empty;
        try
        {
            var file = new FileInfo(settingsPath);
            if (!file.Exists)
            {
                identityCache.TryRemove(settingsPath, out _);
                return Create(companyName, productName, cloudProjectId);
            }

            var length = file.Length;
            var lastWriteUtc = file.LastWriteTimeUtc;
            if (identityCache.TryGetValue(settingsPath, out var cached)
                && cached.Length == length
                && cached.LastWriteUtc == lastWriteUtc)
                return cached.Identity;

            var contents = File.ReadAllText(settingsPath);
            foreach (var line in contents.AsSpan().EnumerateLines())
            {
                if (TryReadValue(line, "companyName", out var company))
                    companyName = company;
                else if (TryReadValue(line, "productName", out var product))
                    productName = product;
                else if (TryReadValue(line, "cloudProjectId", out var cloud))
                    cloudProjectId = cloud;

                if (companyName.Length > 0 && productName.Length > 0 && cloudProjectId.Length > 0)
                    break;
            }

            var identity = Create(companyName, productName, cloudProjectId);
            file.Refresh();
            if (file.Exists && file.Length == length && file.LastWriteTimeUtc == lastWriteUtc)
            {
                identityCache[settingsPath] = new(length, lastWriteUtc, identity);
                if (identityCache.Count > IdentityCachePruneThreshold)
                    foreach (var path in identityCache.Keys)
                        if (!File.Exists(path))
                            identityCache.TryRemove(path, out _);
            }
            return identity;
        }
        catch
        {
            return Create(companyName, productName, cloudProjectId);
        }
    }

    static UnityProjectIdentity Create(string companyName, string productName, string cloudProjectId)
    {
        var normalizedCompanyName = Normalize(companyName);
        var normalizedProductName = Normalize(productName);
        return new(
            Normalize(cloudProjectId),
            normalizedCompanyName,
            normalizedProductName
        );
    }

    static bool TryReadValue(ReadOnlySpan<char> line, ReadOnlySpan<char> key, out string value)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length <= key.Length
            || trimmed[key.Length] != ':'
            || !trimmed[..key.Length].SequenceEqual(key))
        {
            value = string.Empty;
            return false;
        }

        value = trimmed[(key.Length + 1)..].Trim().ToString();
        if (value.Length >= 2
            && value[0] is '"' or '\''
            && value[^1] == value[0])
            value = value[1..^1];

        return true;
    }

    static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    static bool NormalizedEquals(string? value, string normalized)
    {
        if (value is null)
            return normalized.Length == 0;

        var candidate = value.AsSpan().Trim();
        if (candidate.Length != normalized.Length)
            return false;

        for (var index = 0; index < candidate.Length; index++)
        {
            var character = candidate[index];
            if (character > 0x7f)
                return string.Equals(
                    candidate.ToString().ToLowerInvariant(),
                    normalized,
                    StringComparison.Ordinal
                );

            if (character is >= 'A' and <= 'Z')
                character = (char)(character + ('a' - 'A'));
            if (character != normalized[index])
                return false;
        }

        return true;
    }

    readonly record struct CachedProjectIdentity(
        long Length,
        DateTime LastWriteUtc,
        UnityProjectIdentity Identity);
}
