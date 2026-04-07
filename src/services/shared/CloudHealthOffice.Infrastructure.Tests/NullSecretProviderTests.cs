using CloudHealthOffice.Infrastructure.Configuration;

namespace CloudHealthOffice.Infrastructure.Tests;

public class NullSecretProviderTests
{
    private readonly NullSecretProvider _sut = new();

    [Fact]
    public async Task GetSecretAsync_ReturnsNull()
    {
        var result = await _sut.GetSecretAsync("any-secret");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetSecretsAsync_ReturnsEmptyDictionary()
    {
        var result = await _sut.GetSecretsAsync("any-prefix");

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task HealthCheckAsync_ReturnsTrue()
    {
        var result = await _sut.HealthCheckAsync();

        result.Should().BeTrue();
    }
}
