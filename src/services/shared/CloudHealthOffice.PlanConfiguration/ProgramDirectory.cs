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
/// A program as other services need to read it: one line of business in one
/// state, under one carrier (plan-configuration.md §3). The record tenant-service
/// owns carries more; this is the part that is the same for every reader, and
/// it is what makes a program worth having — the three facts a caller would
/// otherwise piece together from a member's address and a coverage coding
/// arrive together, from the plan that was actually sold.
/// </summary>
public sealed record ProgramDescriptor
{
    public required string Id { get; init; }
    /// <summary>The program's code within the tenant, e.g. <c>STAR</c>. What a rule set is keyed on.</summary>
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string CarrierId { get; init; }
    /// <summary>A canonical line-of-business type name (<see cref="LinesOfBusiness"/>).</summary>
    public required string LineOfBusiness { get; init; }
    /// <summary>The two-letter state the program operates in, upper case.</summary>
    public required string StateCode { get; init; }
    public bool Active { get; init; } = true;
}

/// <summary>
/// Reads program records from tenant-service. Answers null rather than throwing
/// for a program or plan that is not there; throws only when tenant-service
/// could not be asked, so a caller can tell "no program" from "did not get to
/// look" — the distinction the CRD resolver is built on.
/// </summary>
public interface IProgramDirectory
{
    /// <summary>The program with this id, or null when the tenant has no such program.</summary>
    Task<ProgramDescriptor?> GetAsync(string tenantId, string programId, CancellationToken ct = default);

    /// <summary>
    /// The program the benefit plan belongs to, or null when the plan is
    /// unknown or carries no program (every plan written before programs
    /// existed carries none).
    /// </summary>
    Task<ProgramDescriptor?> ForBenefitPlanAsync(string tenantId, string planId, CancellationToken ct = default);
}

public sealed class ProgramDirectoryOptions
{
    public const string SectionName = "ProgramDirectory";

    /// <summary>
    /// How long a program is cached. Programs change about once a plan year, so
    /// this is longer than a setting's cache; a stale answer here is a stale
    /// state or line of business, which is why it is minutes and not hours.
    /// </summary>
    public int CacheSeconds { get; set; } = 300;
}

/// <summary>Reads through tenant-service's <c>api/v1/programs</c>, cached per tenant.</summary>
public sealed class HttpProgramDirectory(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    ProgramDirectoryOptions options,
    ILogger<HttpProgramDirectory> logger) : IProgramDirectory
{
    public const string HttpClientName = "program-directory";

    public Task<ProgramDescriptor?> GetAsync(string tenantId, string programId, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(programId)
            ? Task.FromResult<ProgramDescriptor?>(null)
            : FetchAsync(tenantId, $"api/v1/programs/{Uri.EscapeDataString(programId.Trim())}", $"id|{programId.Trim()}", ct);

    public Task<ProgramDescriptor?> ForBenefitPlanAsync(string tenantId, string planId, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(planId)
            ? Task.FromResult<ProgramDescriptor?>(null)
            : FetchAsync(tenantId, $"api/v1/programs/by-benefit-plan/{Uri.EscapeDataString(planId.Trim())}", $"plan|{planId.Trim()}", ct);

    private async Task<ProgramDescriptor?> FetchAsync(string tenantId, string path, string cacheId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var cacheKey = $"program|{tenantId}|{cacheId}";
        if (cache.TryGetValue(cacheKey, out Box? cached) && cached is not null) return cached.Program;

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        // With no caller, ChoOutboundTokenHandler mints a service token for this tenant.
        request.Headers.Add("X-Tenant-ID", tenantId);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

        ProgramDescriptor? program;
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            program = null;
        }
        else
        {
            response.EnsureSuccessStatusCode();
            program = await response.Content.ReadFromJsonAsync<ProgramDescriptor>(HttpPlanSettings.Json, ct).ConfigureAwait(false);
            if (program is null)
            {
                logger.LogWarning("tenant-service answered {Path} for tenant {TenantId} with an empty body",
                    LogSanitizer.Sanitize(path), LogSanitizer.Sanitize(tenantId));
            }
        }

        // "No such program" is cached too: a caller that asks about a plan with
        // no program asks on every claim, and the answer is stable.
        cache.Set(cacheKey, new Box(program), TimeSpan.FromSeconds(Math.Max(1, options.CacheSeconds)));
        return program;
    }

    /// <summary>Lets a cached "there is none" be told from nothing cached.</summary>
    private sealed record Box(ProgramDescriptor? Program);
}

/// <summary>Programs held in memory, for tests and for hosts that run without tenant-service.</summary>
public sealed class InMemoryProgramDirectory : IProgramDirectory
{
    private readonly Dictionary<string, Dictionary<string, ProgramDescriptor>> _byTenant = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _planPrograms = new(StringComparer.Ordinal);

    /// <summary>Set to make every call throw, as an unreachable tenant-service would.</summary>
    public bool Unavailable { get; set; }

    public int Calls { get; private set; }

    public void Add(string tenantId, ProgramDescriptor program, params string[] benefitPlanIds)
    {
        if (!_byTenant.TryGetValue(tenantId, out var programs))
            _byTenant[tenantId] = programs = new Dictionary<string, ProgramDescriptor>(StringComparer.Ordinal);
        programs[program.Id] = program;

        if (!_planPrograms.TryGetValue(tenantId, out var plans))
            _planPrograms[tenantId] = plans = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var planId in benefitPlanIds) plans[planId] = program.Id;
    }

    public Task<ProgramDescriptor?> GetAsync(string tenantId, string programId, CancellationToken ct = default)
    {
        Calls++;
        if (Unavailable) throw new HttpRequestException("tenant-service unavailable");
        return Task.FromResult(_byTenant.GetValueOrDefault(tenantId)?.GetValueOrDefault(programId));
    }

    public Task<ProgramDescriptor?> ForBenefitPlanAsync(string tenantId, string planId, CancellationToken ct = default)
    {
        Calls++;
        if (Unavailable) throw new HttpRequestException("tenant-service unavailable");
        var programId = _planPrograms.GetValueOrDefault(tenantId)?.GetValueOrDefault(planId);
        return Task.FromResult(programId is null ? null : _byTenant.GetValueOrDefault(tenantId)?.GetValueOrDefault(programId));
    }
}

public static class ProgramDirectoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IProgramDirectory"/> reading from tenant-service
    /// (<c>Services:TenantService</c>), cached for
    /// <c>ProgramDirectory:CacheSeconds</c>. The outbound CHO token handler is
    /// added to the client by AddChoAuthentication.
    /// </summary>
    public static IServiceCollection AddProgramDirectoryClient(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ProgramDirectoryOptions.SectionName).Get<ProgramDirectoryOptions>() ?? new ProgramDirectoryOptions();
        services.TryAddSingleton(options);
        services.AddMemoryCache();
        services.AddHttpClient(HttpProgramDirectory.HttpClientName, client =>
        {
            client.BaseAddress = new Uri((configuration["Services:TenantService"] ?? "http://tenant-service").TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        }).SetHandlerLifetime(TimeSpan.FromMinutes(5));
        services.TryAddSingleton<IProgramDirectory, HttpProgramDirectory>();
        return services;
    }
}
