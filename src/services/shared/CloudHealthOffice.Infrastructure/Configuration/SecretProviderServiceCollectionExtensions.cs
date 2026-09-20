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
                throw new NotSupportedException(
                    "HashiCorp Vault secret provider requires the CloudHealthOffice.HashiCorpVault package (planned for v4.1). " +
                    "Use SecretProviderType.AzureKeyVault or SecretProviderType.None.");

            case SecretProviderType.Configuration:
                // Local development and tests only: Azure Key Vault needs a real vault and
                // workload identity, and NullSecretProvider resolves nothing, which leaves
                // services that require a rotating encryption key permanently unready off-Azure.
                //
                // Fails closed rather than warning. The provider is selected from configuration,
                // so without this an operator could set SecretProvider__Provider=Configuration on
                // a production host and route real PHI keys through process configuration, where
                // anything able to read the environment can see them. A log line does not prevent
                // that; refusing to start does.
                if (!IsDevelopmentEnvironment(configuration))
                {
                    throw new InvalidOperationException(
                        $"SecretProvider:Provider={SecretProviderType.Configuration} reads secrets " +
                        "from configuration and is restricted to the Development environment. " +
                        $"Set ASPNETCORE_ENVIRONMENT=Development, or use " +
                        $"{SecretProviderType.AzureKeyVault} for any environment handling real " +
                        "PHI or production keys.");
                }

                services.AddSingleton<ISecretProvider>(sp =>
                {
                    sp.GetRequiredService<ILogger<ConfigurationSecretProvider>>().LogWarning(
                        "Secrets are being read from configuration. This is intended for local " +
                        "development only and must not be used where real PHI or production keys " +
                        "are handled.");
                    return new ConfigurationSecretProvider(configuration);
                });
                break;

            case SecretProviderType.None:
            default:
                services.AddSingleton<ISecretProvider, NullSecretProvider>();
                break;
        }

        // Rotation-aware key resolution, shared by every cryptographic
        // consumer (idcard QR signing, member identifier encryption,
        // member identifier fingerprinting, …). Registered once here so
        // all 35 host services pick it up automatically via AddSecretProvider.
        services.AddSingleton<RotatingKeyProvider>();
        services.AddHostedService<SecretRefreshService>();

        return services;
    }

    /// <summary>
    /// Reads the host environment from configuration. AddSecretProvider runs during service
    /// registration and is not handed an IHostEnvironment, but the host populates the same value
    /// from ASPNETCORE_ENVIRONMENT / DOTNET_ENVIRONMENT before any of this executes.
    /// </summary>
    private static bool IsDevelopmentEnvironment(IConfiguration configuration)
    {
        var environment = configuration["ASPNETCORE_ENVIRONMENT"]
            ?? configuration["DOTNET_ENVIRONMENT"]
            ?? configuration["Environment"];

        return string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);
    }
}
