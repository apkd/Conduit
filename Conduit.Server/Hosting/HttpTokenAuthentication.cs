using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Conduit;

static class HttpTokenAuthentication
{
    static readonly SearchValues<char> TokenCharacters = SearchValues.Create(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~+/"
    );

    /// <summary>Rejects browser origins and checks every request when a token or fingerprint is configured.</summary>
    public static Func<HttpContext, RequestDelegate, Task> Load(string? tokenFile, string? tokenSha256File = null)
    {
        var expectedHash = LoadHash(tokenFile, tokenSha256File);
        return AuthenticateAsync;

        Task AuthenticateAsync(HttpContext context, RequestDelegate next)
        {
            // native MCP clients need no browser access; reject browser origins rather than enabling CORS.
            if (context.Request.Headers.ContainsKey("Origin"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            if (expectedHash is null)
                return next(context);

            var authorization = context.Request.Headers.Authorization;
            if (authorization.Count == 1
                && AuthenticationHeaderValue.TryParse(authorization[0], out var header)
                && string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                && header.Parameter is { } token
                && MatchesToken(token, expectedHash))
                return next(context);

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return Task.CompletedTask;
        }
    }

    static byte[]? LoadHash(string? tokenFile, string? tokenSha256File)
    {
        if (tokenFile is null && tokenSha256File is null)
            return null;

        if (tokenFile is not null && tokenSha256File is not null)
            throw new ArgumentException("Use only one of --token-file or --token-sha256-file.");

        if (tokenFile is not null)
        {
            var token = File.ReadAllText(tokenFile).TrimEnd('\r', '\n');
            if (!IsValidToken(token))
                throw new ArgumentException(
                    "The token file must contain a single random bearer token (32–1024 ASCII characters). " +
                    "Use 32 random bytes encoded as hex or base64."
                );

            return SHA256.HashData(Encoding.ASCII.GetBytes(token));
        }

        var hash = File.ReadAllText(tokenSha256File!).TrimEnd('\r', '\n');
        if (hash.Length != SHA256.HashSizeInBytes * 2
            || hash.AsSpan().ContainsAnyExcept("0123456789abcdefABCDEF"))
            throw new ArgumentException("The SHA-256 file must contain exactly 64 hexadecimal characters.");

        return Convert.FromHexString(hash);
    }

    static bool IsValidToken(string token)
    {
        var unpadded = token.AsSpan().TrimEnd('=');
        return unpadded.Length >= 32 && token.Length <= 1024
            && !unpadded.ContainsAnyExcept(TokenCharacters);
    }

    static bool MatchesToken(string token, ReadOnlySpan<byte> expectedHash)
    {
        if (!IsValidToken(token))
            return false;

        Span<byte> bytes = stackalloc byte[token.Length];
        Encoding.ASCII.GetBytes(token, bytes);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);
        return CryptographicOperations.FixedTimeEquals(hash, expectedHash);
    }
}
