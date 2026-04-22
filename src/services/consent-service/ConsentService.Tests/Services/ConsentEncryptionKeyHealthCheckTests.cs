using CloudHealthOffice.Infrastructure.Configuration;
using ConsentService.Services;
using ConsentService.Tests.Fakes;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConsentService.Tests.Services;

public class ConsentEncryptionKeyHealthCheckTests
{
    private const string Prefix = "consent-body-encryption-key";

    private static (ConsentEncryptionKeyHealthCheck check, DictionarySecretProvider secrets)
        Build(ConsentEncryptionOptions options)
    {
        var secrets = new DictionarySecretProvider();
        var keys = new RotatingKeyProvider(secrets, NullLogger<RotatingKeyProvider>.Instance);
        var check = new ConsentEncryptionKeyHealthCheck(keys, options);
        return (check, secrets);
    }

    private static string Key32Base64(byte fill) =>
        Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray());

    [Fact]
    public async Task Healthy_WhenCurrentVersionResolves()
    {
        var (check, secrets) = Build(new ConsentEncryptionOptions
        {
            KeySecretPrefix = Prefix,
            CurrentKeyVersion = "v1",
            AcceptedKeyVersions = new[] { "v1" }
        });
        secrets.Secrets[$"{Prefix}-v1"] = Key32Base64(0x01);

        var result = await check.CheckHealthAsync(new HealthCheckContext());
        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Unhealthy_WhenCurrentVersionMissing()
    {
        var (check, _) = Build(new ConsentEncryptionOptions
        {
            KeySecretPrefix = Prefix,
            CurrentKeyVersion = "v1",
            AcceptedKeyVersions = new[] { "v1" }
        });

        var result = await check.CheckHealthAsync(new HealthCheckContext());
        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Unhealthy_WhenKeyWrongLength()
    {
        var (check, secrets) = Build(new ConsentEncryptionOptions
        {
            KeySecretPrefix = Prefix,
            CurrentKeyVersion = "v1",
            AcceptedKeyVersions = new[] { "v1" }
        });
        // 16-byte key — not AES-256.
        secrets.Secrets[$"{Prefix}-v1"] = Convert.ToBase64String(Enumerable.Repeat((byte)0x01, 16).ToArray());

        var result = await check.CheckHealthAsync(new HealthCheckContext());
        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("32 bytes");
    }
}
