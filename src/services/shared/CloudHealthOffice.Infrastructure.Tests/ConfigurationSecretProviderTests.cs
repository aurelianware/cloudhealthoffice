using CloudHealthOffice.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;

namespace CloudHealthOffice.Infrastructure.Tests;

public class ConfigurationSecretProviderTests
{
    private static ConfigurationSecretProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        return new ConfigurationSecretProvider(configuration);
    }

    [Fact]
    public async Task GetSecretAsync_ReturnsValueFromSecretsSection()
    {
        var provider = Build(("Secrets:appeal-body-encryption-key-v1", "dGVzdA=="));

        (await provider.GetSecretAsync("appeal-body-encryption-key-v1")).Should().Be("dGVzdA==");
    }

    [Fact]
    public async Task GetSecretAsync_FallsBackToUnderscoreForm()
    {
        // Kubernetes environment variable names must be C identifiers, so a hyphenated secret
        // name cannot be injected as one -- kubectl drops it silently. The underscore form is
        // what callers can actually set, so it has to resolve the same secret.
        var provider = Build(("Secrets:appeal_body_encryption_key_v1", "dGVzdA=="));

        (await provider.GetSecretAsync("appeal-body-encryption-key-v1")).Should().Be("dGVzdA==");
    }

    [Fact]
    public async Task GetSecretAsync_PrefersExactNameOverUnderscoreForm()
    {
        var provider = Build(
            ("Secrets:my-secret", "exact"),
            ("Secrets:my_secret", "fallback"));

        (await provider.GetSecretAsync("my-secret")).Should().Be("exact");
    }

    [Fact]
    public async Task GetSecretAsync_ReturnsNullWhenMissing()
    {
        var provider = Build();

        (await provider.GetSecretAsync("absent")).Should().BeNull();
    }

    [Fact]
    public async Task GetSecretAsync_DoesNotReadOutsideTheSecretsSection()
    {
        // A secret must be declared as one; an unrelated configuration key of the same name
        // should not satisfy a secret lookup.
        var provider = Build(("appeal-body-encryption-key-v1", "dGVzdA=="));

        (await provider.GetSecretAsync("appeal-body-encryption-key-v1")).Should().BeNull();
    }

    [Fact]
    public async Task GetSecretByVersionAsync_AppendsTheVersionToTheName()
    {
        var provider = Build(("Secrets:key-v2", "second"));

        (await provider.GetSecretByVersionAsync("key", "v2")).Should().Be("second");
    }

    [Fact]
    public async Task GetSecretsAsync_ReturnsOnlyMatchingPrefix()
    {
        var provider = Build(
            ("Secrets:appeal-key-v1", "a"),
            ("Secrets:appeal-key-v2", "b"),
            ("Secrets:consent-key-v1", "c"));

        var results = await provider.GetSecretsAsync("appeal-");

        results.Should().HaveCount(2);
        results.Keys.Should().BeEquivalentTo("appeal-key-v1", "appeal-key-v2");
    }

    [Fact]
    public async Task HealthCheckAsync_IsHealthy()
    {
        (await Build().HealthCheckAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task ListSecretVersionsAsync_IsEmpty()
    {
        // Configuration holds one value per name, so there is no version history to report.
        (await Build().ListSecretVersionsAsync("key")).Should().BeEmpty();
    }
}
