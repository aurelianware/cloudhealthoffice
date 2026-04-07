using CloudHealthOffice.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.Infrastructure.Tests;

public class SecretProviderServiceCollectionExtensionsTests
{
    [Fact]
    public void AddSecretProvider_None_RegistersNullSecretProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SecretProvider:Provider"] = "None"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSecretProvider(config);

        var provider = services.BuildServiceProvider();
        var secretProvider = provider.GetRequiredService<ISecretProvider>();

        secretProvider.Should().BeOfType<NullSecretProvider>();
    }

    [Fact]
    public void AddSecretProvider_DefaultConfig_RegistersNullSecretProvider()
    {
        var config = new ConfigurationBuilder().Build();

        var services = new ServiceCollection();
        services.AddSecretProvider(config);

        var provider = services.BuildServiceProvider();
        var secretProvider = provider.GetRequiredService<ISecretProvider>();

        secretProvider.Should().BeOfType<NullSecretProvider>();
    }

    [Fact]
    public void AddSecretProvider_HashiCorpVault_ThrowsNotSupportedException()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SecretProvider:Provider"] = "HashiCorpVault"
            })
            .Build();

        var services = new ServiceCollection();

        var act = () => services.AddSecretProvider(config);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*v4.1*");
    }
}
