using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Logging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.PlanConfiguration;

/// <summary>
/// A postal address as the carrier record carries it. The property names match
/// the record tenant-service serializes, so a reader that deserializes a
/// carrier straight into <see cref="CarrierIdentity"/> keeps every line.
/// </summary>
public sealed record CarrierAddress
{
    public string? Street { get; init; }
    public string? City { get; init; }
    public string? State { get; init; }
    public string? ZipCode { get; init; }
    public string? Country { get; init; }
}

/// <summary>
/// A carrier as the services that act in its name need to read it: who the
/// legal entity is, what it originates ACH under, and what it is called on a
/// letter (plan-configuration.md §3). The record tenant-service owns carries
/// more; this is the part that is the same for every reader.
///
/// <para>
/// Every field but the identifiers is nullable, and a null means the carrier
/// does not state it rather than that it is empty. A caller that has a
/// configured value of its own keeps using that one, which is how step 2b
/// moves a consumer across without a migration: filling the carrier in is what
/// changes what goes out.
/// </para>
/// </summary>
public sealed record CarrierIdentity
{
    public required string Id { get; init; }
    /// <summary>The code within the tenant, e.g. <c>HMO</c>; the default carrier's is <c>DEFAULT</c>.</summary>
    public required string Code { get; init; }
    public required string Name { get; init; }
    /// <summary>The registered legal name, for filings and letters.</summary>
    public string? LegalName { get; init; }
    /// <summary>Federal employer identification number, digits only.</summary>
    public string? TaxId { get; init; }
    public string? NaicCode { get; init; }
    /// <summary>Payer ids this entity is known by, keyed by transaction (<c>837</c>, <c>270</c>, <c>835</c>).</summary>
    public Dictionary<string, string> PayerIds { get; init; } = new();
    /// <summary>The NACHA immediate origin this entity originates under.</summary>
    public string? NachaImmediateOrigin { get; init; }
    /// <summary>The NACHA company identification this entity's batches carry.</summary>
    public string? NachaCompanyIdentification { get; init; }
    /// <summary>The name used on letters and ID cards, where it differs from the legal name.</summary>
    public string? CorrespondenceName { get; init; }
    public CarrierAddress? CorrespondenceAddress { get; init; }
    public bool Active { get; init; } = true;
    public bool IsDefault { get; init; }

    /// <summary>
    /// The payer id for a transaction (<c>837</c>, <c>270</c>, <c>835</c>), or
    /// null where the carrier states none for it. The transaction is matched
    /// without regard to case, since whoever typed the key is not the caller.
    /// </summary>
    public string? PayerIdFor(string transaction) =>
        PayerIds.FirstOrDefault(p => string.Equals(p.Key, transaction, StringComparison.OrdinalIgnoreCase)) is { Value: { } value }
        && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    /// <summary>
    /// What to call this entity in writing: its correspondence name, else its
    /// legal name, else null. Deliberately not <see cref="Name"/>, which is
    /// what staff call the carrier and is filled in for every carrier
    /// including the default one — a caller falling back on it would start
    /// putting the tenant's own organization name on files and letters the
    /// moment carriers existed, which is the silent change step 2b is written
    /// to avoid. A caller with nothing better keeps using its own configured
    /// name until someone states one here.
    /// </summary>
    public string? LetterheadName =>
        new[] { CorrespondenceName, LegalName }.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}

/// <summary>
/// Reads carrier records from tenant-service. Answers null for a carrier that
/// is not there and throws when tenant-service could not be asked, so a caller
/// can tell "the carrier states nothing" from "did not get to look" — the same
/// distinction <see cref="IProgramDirectory"/> keeps.
/// </summary>
public interface ICarrierDirectory
{
    /// <summary>
    /// The tenant's default carrier, the one created with the tenant. Reading
    /// the list is what materializes it, so this answers for a tenant that has
    /// never had a carrier written; it answers null only for a tenant that is
    /// not there at all.
    /// </summary>
    Task<CarrierIdentity?> DefaultAsync(string tenantId, CancellationToken ct = default);

    /// <summary>The carrier with this id, or null when the tenant has no such carrier.</summary>
    Task<CarrierIdentity?> GetAsync(string tenantId, string carrierId, CancellationToken ct = default);
}

public sealed class CarrierDirectoryOptions
{
    public const string SectionName = "CarrierDirectory";

    /// <summary>
    /// How long a carrier is cached. Legal-entity details change about as often
    /// as a filing, so a stale answer here is a stale name or payer id on one
    /// file; it is minutes rather than hours so that correcting one does not
    /// need a restart.
    /// </summary>
    public int CacheSeconds { get; set; } = 300;
}

/// <summary>Reads through tenant-service's <c>api/v1/carriers</c>, cached per tenant.</summary>
public sealed class HttpCarrierDirectory(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    CarrierDirectoryOptions options,
    ILogger<HttpCarrierDirectory> logger) : ICarrierDirectory
{
    public const string HttpClientName = "carrier-directory";

    public async Task<CarrierIdentity?> DefaultAsync(string tenantId, CancellationToken ct = default)
    {
        var carriers = await ListAsync(tenantId, ct).ConfigureAwait(false);
        // A tenant must keep one active carrier, and the list materializes the
        // default one, so the fallbacks below are for a tenant that is not there.
        return carriers.FirstOrDefault(c => c.IsDefault)
            ?? carriers.FirstOrDefault(c => c.Active)
            ?? carriers.FirstOrDefault();
    }

    public async Task<CarrierIdentity?> GetAsync(string tenantId, string carrierId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(carrierId)) return null;
        var carriers = await ListAsync(tenantId, ct).ConfigureAwait(false);
        return carriers.FirstOrDefault(c => string.Equals(c.Id, carrierId.Trim(), StringComparison.Ordinal));
    }

    private async Task<IReadOnlyList<CarrierIdentity>> ListAsync(string tenantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var cacheKey = $"carriers|{tenantId}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<CarrierIdentity>? cached) && cached is not null) return cached;

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/carriers");
        // With no caller, ChoOutboundTokenHandler mints a service token for this tenant.
        request.Headers.Add("X-Tenant-ID", tenantId);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

        IReadOnlyList<CarrierIdentity> carriers;
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            carriers = [];
        }
        else
        {
            response.EnsureSuccessStatusCode();
            carriers = await response.Content.ReadFromJsonAsync<List<CarrierIdentity>>(HttpPlanSettings.Json, ct).ConfigureAwait(false) ?? [];
            if (carriers.Count == 0)
            {
                logger.LogWarning("tenant-service listed no carriers for tenant {TenantId}; it should hold at least the default one",
                    LogSanitizer.Sanitize(tenantId));
            }
        }

        cache.Set(cacheKey, carriers, TimeSpan.FromSeconds(Math.Max(1, options.CacheSeconds)));
        return carriers;
    }
}

/// <summary>Carriers held in memory, for tests and for hosts that run without tenant-service.</summary>
public sealed class InMemoryCarrierDirectory : ICarrierDirectory
{
    private readonly Dictionary<string, List<CarrierIdentity>> _byTenant = new(StringComparer.Ordinal);

    /// <summary>Set to make every call throw, as an unreachable tenant-service would.</summary>
    public bool Unavailable { get; set; }

    public int Calls { get; private set; }

    public void Add(string tenantId, CarrierIdentity carrier)
    {
        if (!_byTenant.TryGetValue(tenantId, out var carriers))
            _byTenant[tenantId] = carriers = [];
        carriers.RemoveAll(c => string.Equals(c.Id, carrier.Id, StringComparison.Ordinal));
        carriers.Add(carrier);
    }

    public Task<CarrierIdentity?> DefaultAsync(string tenantId, CancellationToken ct = default)
    {
        var carriers = List(tenantId);
        return Task.FromResult(carriers.FirstOrDefault(c => c.IsDefault) ?? carriers.FirstOrDefault());
    }

    public Task<CarrierIdentity?> GetAsync(string tenantId, string carrierId, CancellationToken ct = default) =>
        Task.FromResult(List(tenantId).FirstOrDefault(c => string.Equals(c.Id, carrierId, StringComparison.Ordinal)));

    private List<CarrierIdentity> List(string tenantId)
    {
        Calls++;
        if (Unavailable) throw new HttpRequestException("tenant-service unavailable");
        return _byTenant.GetValueOrDefault(tenantId) ?? [];
    }
}

public static class CarrierDirectoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ICarrierDirectory"/> reading from tenant-service
    /// (<c>Services:TenantService</c>), cached for
    /// <c>CarrierDirectory:CacheSeconds</c>. The outbound CHO token handler is
    /// added to the client by AddChoAuthentication.
    /// </summary>
    public static IServiceCollection AddCarrierDirectoryClient(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(CarrierDirectoryOptions.SectionName).Get<CarrierDirectoryOptions>() ?? new CarrierDirectoryOptions();
        services.TryAddSingleton(options);
        services.AddMemoryCache();
        services.AddHttpClient(HttpCarrierDirectory.HttpClientName, client =>
        {
            client.BaseAddress = new Uri((configuration["Services:TenantService"] ?? "http://tenant-service").TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        }).SetHandlerLifetime(TimeSpan.FromMinutes(5));
        services.TryAddSingleton<ICarrierDirectory, HttpCarrierDirectory>();
        return services;
    }
}
