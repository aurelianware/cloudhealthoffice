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
    public void AddSecretProvider_HashiCorpVault_RegistersTheVaultProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SecretProvider:Provider"] = "HashiCorpVault",
                ["SecretProvider:HashiCorpVaultAddress"] = "https://vault.test:8200",
                ["SecretProvider:HashiCorpVaultKubernetesRole"] = "cho-services"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSecretProvider(config);

        services.BuildServiceProvider().GetRequiredService<ISecretProvider>()
            .Should().BeOfType<HashiCorpVaultSecretProvider>();
    }

    [Fact]
    public void AddSecretProvider_HashiCorpVault_WithoutAddress_Throws()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SecretProvider:Provider"] = "HashiCorpVault"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSecretProvider(config);

        var act = () => services.BuildServiceProvider().GetRequiredService<ISecretProvider>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*HashiCorpVaultAddress*");
    }
}
