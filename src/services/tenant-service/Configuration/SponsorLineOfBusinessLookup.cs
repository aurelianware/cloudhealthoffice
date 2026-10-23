using System.Net;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PlanConfiguration;

namespace TenantService.Configuration;

/// <summary>How a sponsor-level write was checked against line-of-business floors.</summary>
public enum FloorCheck
{
    /// <summary>Not a sponsor-level numeric write, or the setting has no line-specific floors.</summary>
    NotApplicable,

    /// <summary>The sponsor's line of business was found and its floors were checked on write.</summary>
    CheckedAgainstSponsorLine,

    /// <summary>sponsor-service has no such group, or it has no line of business; accepted, clamped at resolution.</summary>
    SponsorNotFound,

    /// <summary>sponsor-service could not be reached or answered with an error; accepted, clamped at resolution.</summary>
    LookupFailed,
}

/// <summary>A sponsor's line of business, from sponsor-service, for floor checks on write.</summary>
public interface ISponsorLineOfBusinessLookup
{
    /// <summary>The canonical line of business, or null when the group is unknown. Throws when sponsor-service fails.</summary>
    Task<string?> GetAsync(string tenantId, string groupNumber, CancellationToken ct = default);
}

/// <summary>
/// Calls sponsor-service's <c>GET api/v1/sponsors/{groupNumber}</c> with
/// tenant-service's own service identity, not the caller's token: checking a
/// floor is this service's rule, and an account manager need not be allowed to
/// read sponsor records to be held to it. The token is minted for the tenant
/// in <c>X-Tenant-ID</c>, which is always the caller's.
/// </summary>
public sealed class HttpSponsorLineOfBusinessLookup(IHttpClientFactory factory) : ISponsorLineOfBusinessLookup
{
    public const string HttpClientName = "sponsor-service";

    public async Task<string?> GetAsync(string tenantId, string groupNumber, CancellationToken ct = default)
    {
        var client = factory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/sponsors/{Uri.EscapeDataString(groupNumber)}");
        request.Headers.Add("X-Tenant-ID", tenantId);
        request.UseChoServiceIdentity();
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (!string.Equals(property.Name, "lineOfBusiness", StringComparison.OrdinalIgnoreCase)) continue;
            // sponsor-service serializes the enum as a number; accept a name too.
            var raw = property.Value.ValueKind switch
            {
                JsonValueKind.Number => property.Value.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
                JsonValueKind.String => property.Value.GetString(),
                _ => null,
            };
            return LinesOfBusiness.Normalize(raw);
        }
        return null;
    }
}

/// <summary>Resolves the line of business to check a write against, best-effort.</summary>
public static class FloorChecks
{
    public static async Task<(FloorCheck Check, string? LineOfBusiness)> ForWriteAsync(
        ISponsorLineOfBusinessLookup? lookup, SettingDefinition definition, SettingLevel level, string node,
        bool cleared, string tenantId, ILogger logger, CancellationToken ct)
    {
        if (level != SettingLevel.Sponsor || cleared || string.IsNullOrWhiteSpace(node)
            || definition.Type is not (SettingType.Integer or SettingType.Decimal)
            || !SettingValidator.HasLineSpecificFloors(definition))
            return (FloorCheck.NotApplicable, null);
        if (lookup is null) return (FloorCheck.LookupFailed, null);

        try
        {
            var lob = await lookup.GetAsync(tenantId, node, ct);
            return lob is null ? (FloorCheck.SponsorNotFound, null) : (FloorCheck.CheckedAgainstSponsorLine, lob);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Accept the write; the floors still hold at resolution.
            logger.LogWarning(ex, "Could not look up sponsor {GroupNumber}'s line of business for tenant {TenantId}; the write is checked only against floors for every line",
                LogSanitizer.Sanitize(node), LogSanitizer.Sanitize(tenantId));
            return (FloorCheck.LookupFailed, null);
        }
    }
}
