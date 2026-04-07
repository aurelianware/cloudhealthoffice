using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Configuration;

/// <summary>
/// A <see cref="ConfigurationProvider"/> that loads key-value pairs from the
/// registered <see cref="ISecretProvider"/> and supports periodic reload.
/// </summary>
public class SecretProviderConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly ISecretProvider _secretProvider;
    private readonly SecretProviderOptions _options;
    private readonly ILogger<SecretProviderConfigurationProvider> _logger;
    private readonly Timer? _reloadTimer;
    private int _reloading;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of <see cref="SecretProviderConfigurationProvider"/>.
    /// </summary>
    /// <param name="secretProvider">The secret provider to load secrets from.</param>
    /// <param name="options">Secret provider options controlling reload and degradation behavior.</param>
    /// <param name="loggerFactory">Logger factory for diagnostic output.</param>
    public SecretProviderConfigurationProvider(
        ISecretProvider secretProvider,
        SecretProviderOptions options,
        ILoggerFactory loggerFactory)
    {
        _secretProvider = secretProvider;
        _options = options;
        _logger = loggerFactory.CreateLogger<SecretProviderConfigurationProvider>();

        if (options.ReloadIntervalSeconds > 0)
        {
            var interval = TimeSpan.FromSeconds(options.ReloadIntervalSeconds);
            _reloadTimer = new Timer(_ => ReloadSecrets(), null, interval, interval);
        }
    }

    /// <inheritdoc />
    public override void Load()
    {
        try
        {
            var secrets = _secretProvider
                .GetSecretsAsync(prefix: string.Empty)
                .GetAwaiter()
                .GetResult();

            Data = secrets.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value, StringComparer.OrdinalIgnoreCase);
            _logger.LogInformation("Loaded {Count} secret(s) from {Provider}", secrets.Count, _options.Provider);
        }
        catch (Exception ex)
        {
            HandleLoadFailure(ex);
        }
    }

    private void ReloadSecrets()
    {
        if (Interlocked.CompareExchange(ref _reloading, 1, 0) != 0)
            return;

        try
        {
            var secrets = _secretProvider
                .GetSecretsAsync(prefix: string.Empty)
                .GetAwaiter()
                .GetResult();

            Data = secrets.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value, StringComparer.OrdinalIgnoreCase);
            OnReload();
            _logger.LogInformation("Reloaded {Count} secret(s) from {Provider}", secrets.Count, _options.Provider);
        }
        catch (Exception ex)
        {
            HandleLoadFailure(ex);
        }
        finally
        {
            Interlocked.Exchange(ref _reloading, 0);
        }
    }

    private void HandleLoadFailure(Exception ex)
    {
        if (_options.GracefulDegradation)
        {
            _logger.LogWarning(ex,
                "Failed to load secrets from {Provider}. Graceful degradation is enabled — preserving existing values",
                _options.Provider);
        }
        else
        {
            _logger.LogError(ex, "Failed to load secrets from {Provider}. Graceful degradation is disabled — throwing", _options.Provider);
            throw new InvalidOperationException(
                $"Failed to load secrets from {_options.Provider}. See inner exception for details.", ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _reloadTimer?.Dispose();
            _disposed = true;
        }
    }
}
