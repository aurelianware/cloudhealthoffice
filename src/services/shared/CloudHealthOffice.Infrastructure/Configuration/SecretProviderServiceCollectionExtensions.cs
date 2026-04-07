using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Configuration;

/// <summary>
/// Extension methods for registering the secret provider in the DI container.
/// </summary>
public static class SecretProviderServiceCollectionExtensions
{
    /// <summary>
    /// Registers the appropriate <see cref="ISecretProvider"/> implementation based on the
    /// <c>SecretProvider</c> configuration section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSecretProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = new SecretProviderOptions();
        configuration.GetSection(SecretProviderOptions.SectionName).Bind(options);

        services.Configure<SecretProviderOptions>(
            configuration.GetSection(SecretProviderOptions.SectionName));

        switch (options.Provider)
        {
            case SecretProviderType.AzureKeyVault:
                services.AddSingleton<ISecretProvider>(sp =>
                    new AzureKeyVaultSecretProvider(
                        options,
                        sp.GetRequiredService<ILogger<AzureKeyVaultSecretProvider>>()));
                break;

            case SecretProviderType.HashiCorpVault:
                // Registered by the HashiCorp Vault integration package (v4.1)
                break;

            case SecretProviderType.None:
            default:
                services.AddSingleton<ISecretProvider, NullSecretProvider>();
                break;
        }

        return services;
    }
}
