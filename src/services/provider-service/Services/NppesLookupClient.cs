using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;

namespace ProviderService.Services;

/// <summary>
/// One NPI's NPPES record, through provider-verification-service's
/// <c>GET api/v1/providers/{npi}/nppes</c>. Used to describe providers CHO
/// knows only by the NPI on a loaded claim.
/// </summary>
public interface INppesLookupClient
{
    /// <summary>
    /// The NPPES record, or null when NPPES has no such NPI. Throws
    /// <see cref="NppesUnavailableException"/> when the lookup itself failed,
    /// so a caller can tell "no such provider" from "could not ask".
    /// </summary>
    Task<NppesRecord?> LookupAsync(string tenantId, string npi, CancellationToken ct = default);
}

public sealed class NppesUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class HttpNppesLookupClient : INppesLookupClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;

    public HttpNppesLookupClient(HttpClient http) => _http = http;

    public async Task<NppesRecord?> LookupAsync(string tenantId, string npi, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"api/v1/providers/{Uri.EscapeDataString(npi)}/nppes").UseChoServiceIdentity();
            request.Headers.Add("X-Tenant-ID", tenantId);
            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
                throw new NppesUnavailableException($"NPPES lookup returned {(int)response.StatusCode}.");
            return await response.Content.ReadFromJsonAsync<NppesRecord>(Json, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new NppesUnavailableException("NPPES lookup failed.", ex);
        }
    }
}

/// <summary>The fields of provider-verification-service's NPPES record that describe a provider.</summary>
public sealed class NppesRecord
{
    public string Npi { get; set; } = string.Empty;
    public NppesEntityType EnumerationType { get; set; }
    public string? ProviderFirstName { get; set; }
    public string? ProviderLastName { get; set; }
    public string? ProviderMiddleName { get; set; }
    public string? ProviderCredential { get; set; }
    public string? OrganizationName { get; set; }
    public List<NppesRecordAddress> Addresses { get; set; } = [];
    public List<NppesRecordTaxonomy> Taxonomies { get; set; } = [];
    public NppesRecordStatus NpiStatus { get; set; }
}

/// <summary>NPI-1 (a person) or NPI-2 (an organization), in the engine's order.</summary>
public enum NppesEntityType { Individual, Organization }

public enum NppesRecordStatus { Active, Deactivated }

public sealed class NppesRecordAddress
{
    public string AddressPurpose { get; set; } = string.Empty;
    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string? TelephoneNumber { get; set; }
}

public sealed class NppesRecordTaxonomy
{
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsPrimary { get; set; }
}
