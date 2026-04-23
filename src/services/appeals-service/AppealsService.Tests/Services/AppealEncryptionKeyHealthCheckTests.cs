using System.Security.Cryptography;
using AppealsService.Services;
using AppealsService.Tests.Fakes;
using CloudHealthOffice.Infrastructure.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppealsService.Tests.Services;

public class AppealEncryptionKeyHealthCheckTests
{
    private static AppealEncryptionKeyHealthCheck Build(DictionarySecretProvider secrets, string currentVersion = "v1")
    {
        var keys = new RotatingKeyProvider(secrets, NullLogger<RotatingKeyProvider>.Instance);
        var options = new AppealEncryptionOptions
        {
            KeySecretPrefix = "appeal-body-encryption-key",
            CurrentKeyVersion = currentVersion,
            AcceptedKeyVersions = new[] { currentVersion }
        };
        return new AppealEncryptionKeyHealthCheck(keys, options);
    }

    [Fact]
    public async Task Healthy_WhenKeyPresentAnd32Bytes()
    {
        var secrets = new DictionarySecretProvider();
        secrets.Secrets["appeal-body-encryption-key-v1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var check = Build(secrets);
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Unhealthy_WhenKeyMissing()
    {
        var check = Build(new DictionarySecretProvider());
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Unhealthy_WhenKeyWrongLength()
    {
        var secrets = new DictionarySecretProvider();
        secrets.Secrets["appeal-body-encryption-key-v1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        var check = Build(secrets);
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("32 bytes");
    }
}
