using System.Net;
using System.Text;
using CloudHealthOffice.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests;

public class HashiCorpVaultSecretProviderTests
{
    private const string VaultAddress = "https://vault.test:8200";

    private static SecretProviderOptions TokenOptions(string? mountPoint = null) => new()
    {
        Provider = SecretProviderType.HashiCorpVault,
        HashiCorpVaultAddress = VaultAddress,
        HashiCorpVaultToken = "test-token",
        HashiCorpVaultMountPoint = mountPoint
    };

    private static HashiCorpVaultSecretProvider Build(StubHandler handler, SecretProviderOptions? options = null)
        => new(
            options ?? TokenOptions(),
            NullLogger<HashiCorpVaultSecretProvider>.Instance,
            new HttpClient(handler) { BaseAddress = new Uri(VaultAddress + "/") });

    // ── Configuration guards ────────────────────────────────────────────────

    [Fact]
    public void Construction_WithoutAddress_Throws()
    {
        var act = () => new HashiCorpVaultSecretProvider(
            new SecretProviderOptions { Provider = SecretProviderType.HashiCorpVault, HashiCorpVaultToken = "t" },
            NullLogger<HashiCorpVaultSecretProvider>.Instance);

        act.Should().Throw<InvalidOperationException>().WithMessage("*HashiCorpVaultAddress*");
    }

    [Fact]
    public void Construction_WithNoAuthConfigured_Throws()
    {
        // Without a role or a token nothing can authenticate, and failing at construction says so
        // rather than surfacing a 403 on the first secret read.
        var act = () => new HashiCorpVaultSecretProvider(
            new SecretProviderOptions
            {
                Provider = SecretProviderType.HashiCorpVault,
                HashiCorpVaultAddress = VaultAddress
            },
            NullLogger<HashiCorpVaultSecretProvider>.Instance);

        act.Should().Throw<InvalidOperationException>().WithMessage("*KubernetesRole*");
    }

    // ── Reads ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSecretAsync_ReturnsTheValueKey()
    {
        var handler = StubHandler.Json("""{"data":{"data":{"value":"s3cret"}}}""");

        (await Build(handler).GetSecretAsync("db-password")).Should().Be("s3cret");
    }

    [Fact]
    public async Task GetSecretAsync_UsesKvV2DataPathAndDefaultMount()
    {
        var handler = StubHandler.Json("""{"data":{"data":{"value":"x"}}}""");

        await Build(handler).GetSecretAsync("db-password");

        handler.LastPath.Should().Be("/v1/secret/data/db-password");
    }

    [Fact]
    public async Task GetSecretAsync_HonoursConfiguredMountPoint()
    {
        var handler = StubHandler.Json("""{"data":{"data":{"value":"x"}}}""");

        await Build(handler, TokenOptions("cho-kv")).GetSecretAsync("db-password");

        handler.LastPath.Should().Be("/v1/cho-kv/data/db-password");
    }

    [Fact]
    public async Task GetSecretAsync_SendsTheVaultToken()
    {
        var handler = StubHandler.Json("""{"data":{"data":{"value":"x"}}}""");

        await Build(handler).GetSecretAsync("db-password");

        handler.LastToken.Should().Be("test-token");
    }

    [Fact]
    public async Task GetSecretAsync_FallsBackToTheSoleKeyWhenNotNamedValue()
    {
        // Lets an existing Vault layout be read without reshaping it.
        var handler = StubHandler.Json("""{"data":{"data":{"password":"only"}}}""");

        (await Build(handler).GetSecretAsync("db")).Should().Be("only");
    }

    [Fact]
    public async Task GetSecretAsync_ReturnsNullWhenAmbiguous()
    {
        // Several keys and none called "value": guessing could hand back the wrong credential.
        var handler = StubHandler.Json("""{"data":{"data":{"user":"a","password":"b"}}}""");

        (await Build(handler).GetSecretAsync("db")).Should().BeNull();
    }

    [Fact]
    public async Task GetSecretAsync_ReturnsNullWhenMissing()
    {
        var handler = StubHandler.Status(HttpStatusCode.NotFound);

        (await Build(handler).GetSecretAsync("absent")).Should().BeNull();
    }

    [Fact]
    public async Task GetSecretAsync_ThrowsOnServerError()
    {
        // A sealed or broken Vault must not read as "secret absent".
        var handler = StubHandler.Status(HttpStatusCode.InternalServerError);

        var act = async () => await Build(handler).GetSecretAsync("db");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task GetSecretByVersionAsync_RequestsThatVersion()
    {
        var handler = StubHandler.Json("""{"data":{"data":{"value":"old"}}}""");

        await Build(handler).GetSecretByVersionAsync("db-password", "3");

        handler.LastPath.Should().Be("/v1/secret/data/db-password?version=3");
    }

    // ── Versions ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListSecretVersionsAsync_MapsDestroyedAndDeletedAsDisabled()
    {
        var handler = StubHandler.Json("""
            {"data":{"versions":{
              "1":{"created_time":"2026-01-01T00:00:00Z","deletion_time":null,"destroyed":true},
              "2":{"created_time":"2026-02-01T00:00:00Z","deletion_time":null,"destroyed":false}
            }}}
            """);

        var versions = await Build(handler).ListSecretVersionsAsync("db");

        versions.Should().HaveCount(2);
        versions.Single(v => v.Version == "1").Enabled.Should().BeFalse();
        versions.Single(v => v.Version == "2").Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task ListSecretVersionsAsync_HandlesEmptyStringDeletionTime()
    {
        // Vault sends timestamps as strings and uses "" -- not null -- for a version that was
        // never deleted. Binding that to DateTimeOffset? throws, which only showed up against a
        // real server.
        var handler = StubHandler.Json("""
            {"data":{"versions":{
              "1":{"created_time":"2026-09-21T17:01:43.958916625Z","deletion_time":"","destroyed":false}
            }}}
            """);

        var versions = await Build(handler).ListSecretVersionsAsync("db");

        versions.Should().ContainSingle();
        versions[0].Enabled.Should().BeTrue();
        versions[0].ExpiresOn.Should().BeNull();
        versions[0].CreatedOn.Should().NotBeNull();
    }

    [Fact]
    public async Task ListSecretVersionsAsync_ReturnsEmptyWhenMissing()
    {
        (await Build(StubHandler.Status(HttpStatusCode.NotFound)).ListSecretVersionsAsync("db"))
            .Should().BeEmpty();
    }

    // ── Health ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task HealthCheckAsync_IsHealthyWhenUnsealedAndActive()
    {
        (await Build(StubHandler.Json("""{"initialized":true,"sealed":false}""")).HealthCheckAsync())
            .Should().BeTrue();
    }

    [Fact]
    public async Task HealthCheckAsync_IsUnhealthyWhenSealed()
    {
        // Vault answers 503 when sealed; only an unsealed active node can serve reads.
        (await Build(StubHandler.Status(HttpStatusCode.ServiceUnavailable)).HealthCheckAsync())
            .Should().BeFalse();
    }

    [Fact]
    public async Task HealthCheckAsync_IsUnhealthyWhenUnreachable()
    {
        (await Build(StubHandler.Throws()).HealthCheckAsync()).Should().BeFalse();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string? _body;
        private readonly bool _throw;

        private StubHandler(HttpStatusCode status, string? body, bool shouldThrow)
            => (_status, _body, _throw) = (status, body, shouldThrow);

        public static StubHandler Json(string body) => new(HttpStatusCode.OK, body, false);
        public static StubHandler Status(HttpStatusCode status) => new(status, null, false);
        public static StubHandler Throws() => new(HttpStatusCode.OK, null, true);

        public string LastPath { get; private set; } = string.Empty;
        public string? LastToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_throw) throw new HttpRequestException("unreachable");

            LastPath = request.RequestUri?.PathAndQuery ?? string.Empty;
            LastToken = request.Headers.TryGetValues("X-Vault-Token", out var v) ? v.FirstOrDefault() : null;

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body ?? "{}", Encoding.UTF8, "application/json")
            });
        }
    }
}
