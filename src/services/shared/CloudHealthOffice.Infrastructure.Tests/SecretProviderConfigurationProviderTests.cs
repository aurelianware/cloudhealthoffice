using CloudHealthOffice.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests;

public class SecretProviderConfigurationProviderTests : IDisposable
{
    private readonly SecretProviderOptions _options = new()
    {
        Provider = SecretProviderType.None,
        ReloadIntervalSeconds = 0 // disable timer for tests
    };

    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    [Fact]
    public void Load_PopulatesDataFromSecretProvider()
    {
        var mockProvider = new Mock<ISecretProvider>();
        mockProvider
            .Setup(p => p.GetSecretsAsync(string.Empty, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>
            {
                ["ConnectionString"] = "Server=localhost",
                ["ApiKey"] = "test-key"
            });

        var sut = new SecretProviderConfigurationProvider(mockProvider.Object, _options, _loggerFactory);

        sut.Load();

        sut.TryGet("ConnectionString", out var connStr).Should().BeTrue();
        connStr.Should().Be("Server=localhost");

        sut.TryGet("ApiKey", out var apiKey).Should().BeTrue();
        apiKey.Should().Be("test-key");
    }

    [Fact]
    public void Load_GracefulDegradation_PreservesExistingDataOnFailure()
    {
        var callCount = 0;
        var mockProvider = new Mock<ISecretProvider>();
        mockProvider
            .Setup(p => p.GetSecretsAsync(string.Empty, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                    return Task.FromResult<IDictionary<string, string>>(
                        new Dictionary<string, string> { ["Key"] = "original" });
                throw new Exception("Vault unreachable");
            });

        var options = _options with { GracefulDegradation = true };
        var sut = new SecretProviderConfigurationProvider(mockProvider.Object, options, _loggerFactory);

        // First load succeeds
        sut.Load();
        sut.TryGet("Key", out var val1).Should().BeTrue();
        val1.Should().Be("original");

        // Second load fails gracefully — data preserved
        sut.Load();
        sut.TryGet("Key", out var val2).Should().BeTrue();
        val2.Should().Be("original");
    }

    [Fact]
    public void Load_GracefulDegradationDisabled_ThrowsOnFailure()
    {
        var mockProvider = new Mock<ISecretProvider>();
        mockProvider
            .Setup(p => p.GetSecretsAsync(string.Empty, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Vault unreachable"));

        var options = _options with { GracefulDegradation = false };
        var sut = new SecretProviderConfigurationProvider(mockProvider.Object, options, _loggerFactory);

        var act = () => sut.Load();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Failed to load secrets*");
    }

    [Fact]
    public void Load_CaseInsensitiveKeys()
    {
        var mockProvider = new Mock<ISecretProvider>();
        mockProvider
            .Setup(p => p.GetSecretsAsync(string.Empty, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>
            {
                ["MySecret"] = "value"
            });

        var sut = new SecretProviderConfigurationProvider(mockProvider.Object, _options, _loggerFactory);

        sut.Load();

        sut.TryGet("mysecret", out var val).Should().BeTrue();
        val.Should().Be("value");
    }

    public void Dispose()
    {
        // NullLoggerFactory doesn't need disposal
    }
}
