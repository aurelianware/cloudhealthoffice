using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;

namespace CloudHealthOffice.Infrastructure.Configuration;

/// <summary>
/// Extension methods for adding Azure Key Vault as a configuration source.
/// </summary>
public static class AzureKeyVaultConfigurationExtensions
{
    /// <summary>
    /// Adds Azure Key Vault as an <see cref="IConfigurationSource"/> when the
    /// <c>SecretProvider:Provider</c> setting is <see cref="SecretProviderType.AzureKeyVault"/>.
    /// Secret names use <c>--</c> as the hierarchy delimiter, which is mapped to <c>:</c>
    /// for the .NET configuration system (e.g. <c>CosmosDb--ConnectionString</c> becomes
    /// <c>CosmosDb:ConnectionString</c>).
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="configuration">
    /// The existing configuration used to read <see cref="SecretProviderOptions"/>.
    /// </param>
    /// <returns>The configuration builder for chaining.</returns>
    public static IConfigurationBuilder AddAzureKeyVaultConfiguration(
        this IConfigurationBuilder builder,
        IConfiguration configuration)
    {
        var options = new SecretProviderOptions();
        configuration.GetSection(SecretProviderOptions.SectionName).Bind(options);

        if (options.Provider != SecretProviderType.AzureKeyVault)
            return builder;

        if (string.IsNullOrWhiteSpace(options.AzureKeyVaultUri))
        {
            throw new InvalidOperationException(
                $"SecretProvider:AzureKeyVaultUri must be configured when Provider is {SecretProviderType.AzureKeyVault}.");
        }

        var secretClient = new SecretClient(
            new Uri(options.AzureKeyVaultUri),
            new DefaultAzureCredential());

        builder.AddAzureKeyVault(secretClient, new PrefixKeyVaultSecretManager());

        return builder;
    }

    /// <summary>
    /// Maps Azure Key Vault secret names to .NET configuration keys by replacing
    /// <c>--</c> with <c>:</c>.
    /// </summary>
    private sealed class PrefixKeyVaultSecretManager : Azure.Extensions.AspNetCore.Configuration.Secrets.KeyVaultSecretManager
    {
        public override string GetKey(KeyVaultSecret secret)
        {
            return secret.Name.Replace("--", ConfigurationPath.KeyDelimiter);
        }
    }
}
