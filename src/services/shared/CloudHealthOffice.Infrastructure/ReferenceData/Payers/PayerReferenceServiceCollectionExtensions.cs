using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers.Stedi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.ReferenceData.Payers;

/// <summary>
/// Registers the canonical payer reference service, optional Mongo persistence,
/// synthetic seed data, and (when Stedi is configured) the Stedi directory
/// client + synchronizer.
/// </summary>
public static class PayerReferenceServiceCollectionExtensions
{
    public static IServiceCollection AddChoPayerReference(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<PayerReferenceOptions>()
            .Bind(configuration.GetSection(PayerReferenceOptions.SectionName));

        services.TryAddSingleton<IPayerReferenceStore>(sp => CreateStore(sp, configuration));
        services.TryAddSingleton<IPayerReferenceService, PayerReferenceService>();
        services.AddHostedService<PayerReferenceSeedHostedService>();
        services.AddHostedService<PayerDirectorySyncHostedService>();

        var stediConfigured = configuration.GetSection(StediGatewayOptions.SectionPath).Exists();
        var defaultGateway = configuration["HealthcareTransactions:DefaultGateway"];
        var stediIsDefault = string.Equals(
            defaultGateway, StediHealthcareGateway.GatewayName, StringComparison.OrdinalIgnoreCase);

        if (stediConfigured || stediIsDefault)
        {
            services.AddHttpClient(StediPayerDirectoryClient.HttpClientName, (sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<StediGatewayOptions>>().Value;
                var baseUrl = string.IsNullOrWhiteSpace(opts.PayerDirectoryBaseUrl)
                    ? opts.BaseUrl
                    : opts.PayerDirectoryBaseUrl;
                if (!string.IsNullOrWhiteSpace(baseUrl) &&
                    Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
                {
                    client.BaseAddress = baseUri;
                }

                client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds > 0 ? opts.TimeoutSeconds : 30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("CloudHealthOffice-PayerDirectory/1.0");
            });

            services.TryAddSingleton(sp => new StediPayerDirectoryClient(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IOptions<StediGatewayOptions>>(),
                sp.GetRequiredService<IOptions<PayerReferenceOptions>>(),
                sp.GetRequiredService<ILogger<StediPayerDirectoryClient>>(),
                sp.GetService<TimeProvider>()));

            services.TryAddSingleton<IPayerDirectorySynchronizer, StediPayerDirectorySynchronizer>();
        }
        else
        {
            services.TryAddSingleton<IPayerDirectorySynchronizer, DisabledPayerDirectorySynchronizer>();
        }

        return services;
    }

    private static IPayerReferenceStore CreateStore(IServiceProvider sp, IConfiguration configuration)
    {
        var options = sp.GetRequiredService<IOptions<PayerReferenceOptions>>().Value;
        if (options.UseMongo)
        {
            var client = sp.GetService<IMongoClient>();
            if (client is not null)
            {
                var databaseName = string.IsNullOrWhiteSpace(options.MongoDatabaseName)
                    ? configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice"
                    : options.MongoDatabaseName;
                return new MongoPayerReferenceStore(client.GetDatabase(databaseName), options);
            }
        }

        return new InMemoryPayerReferenceStore();
    }
}

internal sealed class PayerReferenceSeedHostedService : IHostedService
{
    private readonly IPayerReferenceStore _store;
    private readonly IOptions<PayerReferenceOptions> _options;
    private readonly ILogger<PayerReferenceSeedHostedService> _logger;
    private readonly TimeProvider _timeProvider;

    public PayerReferenceSeedHostedService(
        IPayerReferenceStore store,
        IOptions<PayerReferenceOptions> options,
        ILogger<PayerReferenceSeedHostedService> logger,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_store is MongoPayerReferenceStore mongo)
        {
            await mongo.EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!_options.Value.SeedSyntheticPayers)
        {
            return;
        }

        var count = await _store.CountAsync(cancellationToken).ConfigureAwait(false);
        if (count > 0)
        {
            return;
        }

        var seed = SyntheticPayerSeed.Create(_timeProvider.GetUtcNow());
        await _store.UpsertManyAsync(seed, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Loaded {Count} synthetic payer reference records", seed.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
