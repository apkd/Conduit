using System.Security.Cryptography;
using System.Text;

namespace Conduit;

readonly record struct UnityProjectIdentity(
    string CloudProjectId,
    string FallbackId,
    string CompanyName,
    string ProductName)
{
    public bool Matches(BridgeEndpointDescriptor player) =>
        string.IsNullOrWhiteSpace(player.CloudProjectId)
            ? string.Equals(FallbackId, CreateFallbackId(player.CompanyName, player.ProductName), StringComparison.Ordinal)
            : string.Equals(Normalize(player.CloudProjectId), CloudProjectId, StringComparison.Ordinal);

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
        if (File.Exists(settingsPath))
            foreach (var line in File.ReadLines(settingsPath))
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

        return new(
            Normalize(cloudProjectId),
            CreateFallbackId(companyName, productName),
            companyName,
            productName
        );
    }

    public static string CreateFallbackId(string? companyName, string? productName)
    {
        var identity = $"conduit-project-identity-v1\0{Normalize(companyName)}\0{Normalize(productName)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    static bool TryReadValue(string line, string key, out string value)
    {
        var trimmed = line.AsSpan().TrimStart();
        var prefix = key + ':';
        if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        value = trimmed[prefix.Length..].Trim().ToString();
        if (value.Length >= 2
            && value[0] is '"' or '\''
            && value[^1] == value[0])
            value = value[1..^1];

        return true;
    }

    static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;
}
