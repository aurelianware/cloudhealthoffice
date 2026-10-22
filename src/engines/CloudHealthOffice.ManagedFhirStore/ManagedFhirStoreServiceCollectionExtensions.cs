using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.ManagedFhirStore;

/// <summary>Which managed FHIR server CHO publishes projections into.</summary>
public enum ManagedFhirStoreProvider
{
    /// <summary>Publishing is off. The default: a plan opts in.</summary>
    None,

    /// <summary>Google Cloud Healthcare API FHIR store.</summary>
    GcpHealthcare,

    /// <summary>Process memory; Development only.</summary>
    InMemory
}

/// <summary>
/// Registers the configured <see cref="IManagedFhirStore"/> (ADR 023).
///
/// <code>
///   ManagedFhirStore:Provider          None | GcpHealthcare | InMemory   (default None)
///
///   GcpHealthcare  ManagedFhirStore:Gcp:ProjectId     required
///                  ManagedFhirStore:Gcp:Location      required, e.g. us-central1
///                  ManagedFhirStore:Gcp:DatasetId     required
///                  ManagedFhirStore:Gcp:FhirStoreId   required
/// </code>
///
/// Unlike the document store, publishing is optional: with no provider (or
/// <c>None</c>) nothing is registered and callers check
/// <see cref="IsEnabled"/>. A provider that is set fails closed: missing
/// settings stop the service at start-up rather than silently not
/// publishing. <c>InMemory</c> is refused outside Development. Azure Health
/// Data Services and AWS HealthLake are planned providers, not implemented;
/// naming one is a start-up error, not a no-op.
/// </summary>
public static class ManagedFhirStoreServiceCollectionExtensions
{
    public const string SectionName = "ManagedFhirStore";
    public const string GcpHttpClientName = "ManagedFhirStore.Gcp";

    /// <summary>True when a provider other than <c>None</c> is configured.</summary>
    public static bool IsEnabled(IConfiguration configuration, string environmentName) =>
        ResolveProvider(configuration, environmentName) != ManagedFhirStoreProvider.None;

    /// <summary>Registers <see cref="IManagedFhirStore"/> when a provider is configured. Returns whether one was.</summary>
    public static bool AddChoManagedFhirStore(this IServiceCollection services, IConfiguration configuration, string environmentName)
    {
        var provider = ResolveProvider(configuration, environmentName);
        switch (provider)
        {
            case ManagedFhirStoreProvider.None:
                return false;

            case ManagedFhirStoreProvider.InMemory:
                services.AddSingleton<IManagedFhirStore, InMemoryManagedFhirStore>();
                return true;

            case ManagedFhirStoreProvider.GcpHealthcare:
                var options = new GcpHealthcareFhirStoreOptions();
                configuration.GetSection($"{SectionName}:Gcp").Bind(options);
                options.Validate();

                services.AddSingleton(options);
                services.AddSingleton<IGoogleAccessTokenSource, ApplicationDefaultGoogleAccessTokenSource>();
                services.AddHttpClient(GcpHttpClientName, client => client.Timeout = TimeSpan.FromSeconds(60));
                services.AddSingleton<IManagedFhirStore>(sp => new GcpHealthcareFhirStore(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(GcpHttpClientName),
                    sp.GetRequiredService<IGoogleAccessTokenSource>(),
                    sp.GetRequiredService<GcpHealthcareFhirStoreOptions>(),
                    sp.GetRequiredService<ILogger<GcpHealthcareFhirStore>>()));
                return true;

            default:
                throw new InvalidOperationException($"Unknown managed FHIR store provider {provider}.");
        }
    }

    public static ManagedFhirStoreProvider ResolveProvider(IConfiguration configuration, string environmentName)
    {
        var configured = configuration[$"{SectionName}:Provider"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return ManagedFhirStoreProvider.None;
        }

        if (configured.Trim().All(char.IsDigit)
            || !Enum.TryParse(configured.Trim(), ignoreCase: true, out ManagedFhirStoreProvider provider)
            || !Enum.IsDefined(provider))
        {
            throw new InvalidOperationException(
                $"ManagedFhirStore:Provider '{configured}' is not one of None, GcpHealthcare, InMemory. "
                + "Azure Health Data Services and AWS HealthLake are planned, not implemented.");
        }

        if (provider == ManagedFhirStoreProvider.InMemory
            && !string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "ManagedFhirStore:Provider InMemory is for Development only: projections would be lost on restart.");
        }

        return provider;
    }
}
