namespace CloudHealthOffice.ProviderVerificationService;

using CloudHealthOffice.ProviderVerificationEngine;
using CloudHealthOffice.ProviderVerificationEngine.DataSources;
using CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;
using CloudHealthOffice.ProviderVerificationEngine.Models;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

/// <summary>
/// Wires OIG LEIE / SAM.gov exclusion screening from configuration
/// (<c>ProviderVerification:ExclusionScreening</c>). With neither source
/// enabled the placeholder <see cref="NullExclusionAdapter"/> stays registered
/// and every provider is reported NOT screened.
/// </summary>
public static class ExclusionScreeningRegistration
{
    public static IServiceCollection AddExclusionScreening(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(ExclusionScreeningOptions.SectionName);
        services.AddOptions<ExclusionScreeningOptions>()
            .Bind(section)
            .PostConfigure(o =>
            {
                // Existing deployments hold the key as ProviderVerification:SamGovApiKey.
                if (string.IsNullOrWhiteSpace(o.Sam.ApiKey))
                    o.Sam.ApiKey = configuration[$"{VerificationOptions.SectionName}:SamGovApiKey"];
            });

        var bound = section.Get<ExclusionScreeningOptions>() ?? new ExclusionScreeningOptions();
        var leie = bound.Leie.Enabled;
        var sam = bound.Sam.Enabled;

        if (!leie && !sam)
        {
            services.TryAddSingleton<IExclusionScreeningAdapter, NullExclusionAdapter>();
            return services;
        }

        services.TryAddSingleton(TimeProvider.System);

        var needsLocalStore = leie || bound.Sam.Mode == SamScreeningMode.Extract;
        if (needsLocalStore)
        {
            services.TryAddSingleton<IExclusionRecordStore>(sp => CreateStore(sp, configuration));

            // Bulk downloads: long timeout, no CHO token, no standard resilience
            // handler (its 30 s total timeout would cut a large download; the
            // sync worker retries on its own schedule). Request logging is
            // removed because the SAM extract URL carries the API key.
            services.AddHttpClient(ExclusionDatasetSyncBase.HttpClientName, (sp, client) =>
                {
                    var opts = sp.GetRequiredService<IOptions<ExclusionScreeningOptions>>().Value;
                    client.Timeout = opts.DownloadTimeout + TimeSpan.FromMinutes(1);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("CloudHealthOffice-ExclusionSync/1.0");
                })
                .WithoutChoTokens()
                .RemoveAllLoggers();

            services.AddHostedService<ExclusionDatasetSyncHostedService>();
        }

        if (leie)
        {
            services.AddSingleton<IExclusionDatasetSync, LeieDatasetSync>();
            services.AddSingleton<IExclusionSource>(sp => new LocalExclusionListScreener(
                ExclusionScreeningSource.OigLeie,
                sp.GetRequiredService<IExclusionRecordStore>(),
                sp.GetRequiredService<IOptions<ExclusionScreeningOptions>>(),
                sp.GetRequiredService<ILogger<LocalExclusionListScreener>>(),
                sp.GetRequiredService<TimeProvider>()));
        }

        if (sam)
        {
            if (bound.Sam.Mode == SamScreeningMode.Extract)
            {
                services.AddSingleton<IExclusionDatasetSync, SamExtractDatasetSync>();
                services.AddSingleton<IExclusionSource>(sp => new LocalExclusionListScreener(
                    ExclusionScreeningSource.SamGov,
                    sp.GetRequiredService<IExclusionRecordStore>(),
                    sp.GetRequiredService<IOptions<ExclusionScreeningOptions>>(),
                    sp.GetRequiredService<ILogger<LocalExclusionListScreener>>(),
                    sp.GetRequiredService<TimeProvider>()));
            }
            else
            {
                // Own retry/backoff (429 Retry-After aware) lives in the screener,
                // so no resilience handler here. Loggers removed: the request
                // URL carries the API key.
                services.AddHttpClient(SamExclusionsApiScreener.HttpClientName, client =>
                    {
                        client.DefaultRequestHeaders.Add("Accept", "application/json");
                        client.Timeout = Timeout.InfiniteTimeSpan; // per-request timeout enforced by the screener
                    })
                    .WithoutChoTokens()
                    .RemoveAllLoggers();
                services.AddSingleton<IExclusionSource>(sp => new SamExclusionsApiScreener(
                    sp.GetRequiredService<IHttpClientFactory>(),
                    sp.GetRequiredService<IOptions<ExclusionScreeningOptions>>(),
                    sp.GetRequiredService<ILogger<SamExclusionsApiScreener>>(),
                    sp.GetRequiredService<TimeProvider>()));
            }
        }

        services.TryAddSingleton<IExclusionScreeningAdapter, CompositeExclusionScreeningAdapter>();
        return services;
    }

    /// <summary>
    /// MongoDB (shared by all replicas) when <c>MongoDb:ConnectionString</c> is
    /// set; otherwise a per-process in-memory copy, each replica downloading
    /// its own. Tenant-agnostic: always the base database, never a tenant one.
    /// </summary>
    private static IExclusionRecordStore CreateStore(IServiceProvider sp, IConfiguration configuration)
    {
        var options = sp.GetRequiredService<IOptions<ExclusionScreeningOptions>>().Value;
        var client = sp.GetService<IMongoClient>();
        var connectionString = configuration["MongoDb:ConnectionString"];
        if (client is null && !string.IsNullOrWhiteSpace(connectionString))
            client = new MongoClient(connectionString);

        if (client is null)
        {
            sp.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(ExclusionScreeningRegistration).FullName!)
                .LogWarning("MongoDb:ConnectionString is not set; exclusion lists are held in memory per replica");
            return new InMemoryExclusionRecordStore();
        }

        var databaseName = string.IsNullOrWhiteSpace(options.MongoDatabaseName)
            ? configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice"
            : options.MongoDatabaseName;
        return new MongoExclusionRecordStore(client.GetDatabase(databaseName), options);
    }
}
