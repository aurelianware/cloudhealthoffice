using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Configuration;

/// <summary>
/// An <see cref="IConfigurationSource"/> that loads configuration values from the
/// registered <see cref="ISecretProvider"/>.
/// </summary>
public class SecretProviderConfigurationSource : IConfigurationSource
{
    private readonly ISecretProvider _secretProvider;
    private readonly SecretProviderOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of <see cref="SecretProviderConfigurationSource"/>.
    /// </summary>
    /// <param name="secretProvider">The secret provider to load secrets from.</param>
    /// <param name="options">Secret provider options controlling reload and degradation behavior.</param>
    /// <param name="loggerFactory">Logger factory for diagnostic output.</param>
    public SecretProviderConfigurationSource(
        ISecretProvider secretProvider,
        SecretProviderOptions options,
        ILoggerFactory loggerFactory)
    {
        _secretProvider = secretProvider;
        _options = options;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        return new SecretProviderConfigurationProvider(_secretProvider, _options, _loggerFactory);
    }
}
