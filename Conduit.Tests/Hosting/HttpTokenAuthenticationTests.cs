using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Conduit;

public sealed class HttpTokenAuthenticationTests
{
    [Test]
    [Arguments(null)]
    [Arguments("Bearer unused")]
    public async Task AllowsRequestsWithoutTokenConfiguration(string? authorization)
    {
        var context = CreateRequest(authorization);
        var called = false;

        await HttpTokenAuthentication.Load(null)(context, _ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await Assert.That(called).IsTrue();
        await Assert.That(context.Response.Headers.ContainsKey("WWW-Authenticate")).IsFalse();
    }

    [Test]
    public async Task RejectsBrowserOriginsWithoutTokenConfiguration()
    {
        var context = CreateRequest(StringValues.Empty);
        context.Request.Headers.Origin = "https://untrusted.example";
        var called = false;

        await HttpTokenAuthentication.Load(null)(context, _ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await Assert.That(called).IsFalse();
        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status403Forbidden);
    }

    [Test]
    [Arguments(false, "")]
    [Arguments(false, "\n")]
    [Arguments(false, "\r\n")]
    [Arguments(true, "")]
    [Arguments(true, "\n")]
    [Arguments(true, "\r\n")]
    public async Task AuthenticatesBearerHeaderWithTokenFileLineEnding(bool fingerprint, string lineEnding)
    {
        using var file = new TokenFile(fingerprint);
        File.AppendAllText(file.Path, lineEnding);
        var authenticate = file.Load();
        var context = CreateRequest($"bEaReR {file.Token}");
        var called = false;

        await authenticate(context, _ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await Assert.That(called).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RejectsMissingWrongMalformedAndDuplicateCredentials(bool fingerprint)
    {
        using var file = new TokenFile(fingerprint);
        var authenticate = file.Load();
        StringValues[] invalidHeaders =
        [
            StringValues.Empty,
            $"Bearer {file.Token.ToUpperInvariant()}",
            $"Bearer {file.Token[..^1]}",
            $"Basic {file.Token}",
            "Bearer",
            new([$"Bearer {file.Token}", $"Bearer {file.Token}"]),
            $"Bearer {file.Token}, Bearer {file.Token}",
            $"Bearer {new string('a', 1025)}",
            $"Bearer {file.Token}é",
            $"Bearer {file.Fingerprint}",
        ];

        foreach (var header in invalidHeaders)
        {
            var context = CreateRequest(header);
            var called = false;
            await authenticate(context, _ =>
            {
                called = true;
                return Task.CompletedTask;
            });

            await Assert.That(called).IsFalse();
            await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status401Unauthorized);
            await Assert.That(context.Response.Headers.WWWAuthenticate.ToString()).IsEqualTo("Bearer");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RequiresAuthorizationHeaderOnEveryRequest(bool fingerprint)
    {
        using var file = new TokenFile(fingerprint);
        var authenticate = file.Load();
        var calls = 0;
        await authenticate(CreateRequest($"Bearer {file.Token}"), Next);

        var anonymous = CreateRequest(StringValues.Empty);
        anonymous.Request.QueryString = new QueryString($"?access_token={file.Token}");
        anonymous.Request.Headers.Cookie = $"access_token={file.Token}";
        await authenticate(anonymous, Next);

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(anonymous.Response.StatusCode).IsEqualTo(StatusCodes.Status401Unauthorized);

        Task Next(HttpContext _)
        {
            calls++;
            return Task.CompletedTask;
        }
    }

    [Test]
    [Arguments(false, "https://untrusted.example")]
    [Arguments(false, "null")]
    [Arguments(false, "")]
    [Arguments(true, "https://untrusted.example")]
    [Arguments(true, "null")]
    [Arguments(true, "")]
    public async Task RejectsBrowserOriginsEvenWithValidCredentials(bool fingerprint, string origin)
    {
        using var file = new TokenFile(fingerprint);
        var context = CreateRequest($"Bearer {file.Token}");
        context.Request.Headers.Origin = origin;
        var called = false;

        await file.Load()(context, _ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        await Assert.That(called).IsFalse();
        await Assert.That(context.Response.StatusCode).IsEqualTo(StatusCodes.Status403Forbidden);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TokenReplacementTakesEffectOnlyAfterLoadingAgain(bool fingerprint)
    {
        using var file = new TokenFile(fingerprint);
        using var replacement = new TokenFile();
        var originalAuthentication = file.Load();
        file.WriteToken(replacement.Token);
        var replacementAuthentication = file.Load();
        foreach (var (authenticate, token, accepted) in new[]
        {
            (originalAuthentication, file.Token, true),
            (originalAuthentication, replacement.Token, false),
            (replacementAuthentication, file.Token, false),
            (replacementAuthentication, replacement.Token, true),
        })
        {
            var called = false;
            await authenticate(CreateRequest($"Bearer {token}"), _ =>
            {
                called = true;
                return Task.CompletedTask;
            });
            await Assert.That(called).IsEqualTo(accepted);
        }
    }

    [Test]
    public async Task RejectsMissingOrInvalidTokenFilesWithoutDisclosingContents()
    {
        using var file = new TokenFile();
        foreach (var content in new[] { "", "short", $" {file.Token}", $"{file.Token}\n{file.Token}", $"{file.Token}," })
        {
            File.WriteAllText(file.Path, content);
            var exception = await Assert.That(() => HttpTokenAuthentication.Load(file.Path)).Throws<ArgumentException>();
            await Assert.That(exception!.Message.Contains(file.Token, StringComparison.Ordinal)).IsFalse();
        }

        File.Delete(file.Path);
        await Assert.That(() => HttpTokenAuthentication.Load(file.Path)).Throws<FileNotFoundException>();
    }

    [Test]
    public async Task RejectsConflictingTokenSources()
    {
        using var token = new TokenFile();
        using var fingerprint = new TokenFile(true);
        await Assert.That(() => HttpTokenAuthentication.Load(token.Path, fingerprint.Path)).Throws<ArgumentException>();
    }

    [Test]
    public async Task RejectsInvalidFingerprintFilesWithoutDisclosingContents()
    {
        using var file = new TokenFile(true);
        foreach (var content in new[]
        {
            "", file.Token, file.Fingerprint[..^1], $" {file.Fingerprint}",
            $"{file.Fingerprint}\n{file.Fingerprint}", new string('x', 64),
        })
        {
            File.WriteAllText(file.Path, content);
            var exception = await Assert.That(() => file.Load()).Throws<ArgumentException>();
            await Assert.That(exception!.Message.Contains(file.Token, StringComparison.Ordinal)).IsFalse();
            await Assert.That(exception.Message.Contains(file.Fingerprint, StringComparison.Ordinal)).IsFalse();
        }

        File.Delete(file.Path);
        await Assert.That(() => file.Load()).Throws<FileNotFoundException>();
    }

    [Test]
    public async Task FingerprintHexCasingDoesNotChangeTheRequiredToken()
    {
        using var file = new TokenFile(true);
        File.WriteAllText(file.Path, file.Fingerprint.ToLowerInvariant());
        var called = false;
        await file.Load()(CreateRequest($"Bearer {file.Token}"), _ =>
        {
            called = true;
            return Task.CompletedTask;
        });
        await Assert.That(called).IsTrue();
    }

    static DefaultHttpContext CreateRequest(StringValues authorization)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = authorization;
        return context;
    }

    sealed class TokenFile : IDisposable
    {
        readonly bool fingerprint;
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"conduit-token-{Guid.NewGuid():N}");
        public string Token { get; } = "a" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(Token)));

        public TokenFile(bool fingerprint = false)
        {
            this.fingerprint = fingerprint;
            WriteToken(Token);
        }

        public void WriteToken(string token) => File.WriteAllText(Path, fingerprint
            ? Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)))
            : token);

        public Func<HttpContext, RequestDelegate, Task> Load() =>
            HttpTokenAuthentication.Load(fingerprint ? null : Path, fingerprint ? Path : null);

        public void Dispose() => File.Delete(Path);
    }
}
