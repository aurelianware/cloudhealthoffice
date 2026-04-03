namespace CloudHealthOffice.ProviderVerificationEngine.DataSources.Nppes;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.ProviderVerificationEngine.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Concrete adapter for the NPPES NPI Registry Read API v2.1.
/// No authentication required. Rate limit: undocumented but be polite.
/// 
/// API docs: https://npiregistry.cms.hhs.gov/api-page
/// Endpoint:  https://npiregistry.cms.hhs.gov/api/?version=2.1
/// </summary>
public class NppesHttpAdapter : INppesAdapter
{
    private readonly HttpClient _http;
    private readonly ILogger<NppesHttpAdapter> _logger;
    private readonly VerificationOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public NppesHttpAdapter(
        HttpClient http,
        ILogger<NppesHttpAdapter> logger,
        IOptions<VerificationOptions> options)
    {
        _http = http;
        _logger = logger;
        _options = options.Value;
        _http.BaseAddress ??= new Uri(_options.NppesApiBaseUrl);
    }

    public async Task<NppesProviderData?> LookupByNpiAsync(string npi, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(npi) || npi.Length != 10 || !npi.All(char.IsDigit))
        {
            _logger.LogDebug("Invalid NPI format: {Npi}", SanitizeForLog(npi));
            return null;
        }

        // Luhn check (NPI uses Luhn with prefix 80840)
        if (!PassesLuhnCheck(npi))
        {
            _logger.LogDebug("NPI {Npi} fails Luhn validation", SanitizeForLog(npi));
            return null;
        }

        var url = $"?version=2.1&number={npi}";
        _logger.LogDebug("NPPES lookup: {Url}", SanitizeForLog(url));

        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        NppesApiResponse? nppesResponse;
        try
        {
            nppesResponse = await response.Content
                .ReadFromJsonAsync<NppesApiResponse>(JsonOptions, ct);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogDebug(ex, "NPPES returned non-JSON response for NPI lookup");
            return null;
        }

        if (nppesResponse?.ResultCount is null or 0 || nppesResponse.Results is null)
        {
            return null;
        }

        return MapToProviderData(nppesResponse.Results[0]);
    }

    public async Task<List<NppesProviderData>> SearchAsync(
        NppesSearchCriteria criteria,
        CancellationToken ct = default)
    {
        var queryParams = new List<string> { "version=2.1" };

        if (!string.IsNullOrWhiteSpace(criteria.FirstName))
            queryParams.Add($"first_name={Uri.EscapeDataString(criteria.FirstName)}");
        if (!string.IsNullOrWhiteSpace(criteria.LastName))
            queryParams.Add($"last_name={Uri.EscapeDataString(criteria.LastName)}");
        if (!string.IsNullOrWhiteSpace(criteria.OrganizationName))
            queryParams.Add($"organization_name={Uri.EscapeDataString(criteria.OrganizationName)}");
        if (!string.IsNullOrWhiteSpace(criteria.TaxonomyDescription))
            queryParams.Add($"taxonomy_description={Uri.EscapeDataString(criteria.TaxonomyDescription)}");
        if (!string.IsNullOrWhiteSpace(criteria.City))
            queryParams.Add($"city={Uri.EscapeDataString(criteria.City)}");
        if (!string.IsNullOrWhiteSpace(criteria.State))
            queryParams.Add($"state={Uri.EscapeDataString(criteria.State)}");
        if (!string.IsNullOrWhiteSpace(criteria.PostalCode))
            queryParams.Add($"postal_code={Uri.EscapeDataString(criteria.PostalCode)}");

        queryParams.Add($"limit={Math.Clamp(criteria.Limit, 1, 200)}");

        var url = $"?{string.Join("&", queryParams)}";
        _logger.LogDebug("NPPES search: {Url}", SanitizeForLog(url));

        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        NppesApiResponse? nppesResponse;
        try
        {
            nppesResponse = await response.Content
                .ReadFromJsonAsync<NppesApiResponse>(JsonOptions, ct);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogDebug(ex, "NPPES returned non-JSON response for search");
            return [];
        }

        if (nppesResponse?.Results is null)
            return [];

        return nppesResponse.Results.Select(MapToProviderData).ToList();
    }

    public Task<BulkSyncResult> BulkSyncAsync(CancellationToken ct = default)
    {
        // TODO: Implement NPPES V2 weekly dissemination file download + parse.
        // File: ~8GB CSV, 330+ columns. Use CsvHelper with streaming reader.
        // Schedule: Sunday 2 AM via BackgroundService/Hangfire.
        // Target: local PostgreSQL nppes_providers table with GIN indexes on
        //         npi, last_name, organization_name, taxonomy_code, state.
        throw new NotImplementedException(
            "NPPES bulk sync requires infrastructure setup. " +
            "See /docs/provider-verification/bulk-sync-design.md");
    }

    // ── Mapping ──────────────────────────────────────────────────

    private static NppesProviderData MapToProviderData(NppesApiResult result)
    {
        var data = new NppesProviderData
        {
            Npi = result.Number?.ToString() ?? string.Empty,
            EnumerationType = result.EnumerationType == "NPI-1"
                ? NppesEnumerationType.Individual
                : NppesEnumerationType.Organization,
            RetrievedAt = DateTimeOffset.UtcNow
        };

        // Basic info
        if (result.Basic != null)
        {
            data.ProviderFirstName = result.Basic.FirstName;
            data.ProviderLastName = result.Basic.LastName;
            data.ProviderMiddleName = result.Basic.MiddleName;
            data.ProviderCredential = result.Basic.Credential;
            data.OrganizationName = result.Basic.OrganizationName;
            data.OrganizationSubpart = result.Basic.OrganizationalSubpart;
            data.AuthorizedOfficialFirstName = result.Basic.AuthorizedOfficialFirstName;
            data.AuthorizedOfficialLastName = result.Basic.AuthorizedOfficialLastName;
            data.AuthorizedOfficialTitle = result.Basic.AuthorizedOfficialTitleOrPosition;

            if (DateTimeOffset.TryParse(result.Basic.EnumerationDate, out var enumDate))
                data.EnumerationDate = enumDate;
            if (DateTimeOffset.TryParse(result.Basic.LastUpdated, out var lastUpdated))
                data.LastUpdated = lastUpdated;
            if (DateTimeOffset.TryParse(result.Basic.DeactivationDate, out var deactDate))
                data.DeactivationDate = deactDate;
            if (DateTimeOffset.TryParse(result.Basic.ReactivationDate, out var reactDate))
                data.ReactivationDate = reactDate;

            // Status field is authoritative — use it over date-based inference
            data.NpiStatus = string.IsNullOrEmpty(result.Basic.Status)
                ? NppesNpiStatus.Active
                : result.Basic.Status.Equals("A", StringComparison.OrdinalIgnoreCase)
                    ? NppesNpiStatus.Active
                    : NppesNpiStatus.Deactivated;
        }

        // Addresses
        if (result.Addresses != null)
        {
            data.Addresses = result.Addresses.Select(a => new NppesAddress
            {
                AddressPurpose = a.AddressPurpose ?? string.Empty,
                AddressLine1 = a.Address1 ?? string.Empty,
                AddressLine2 = a.Address2,
                City = a.City ?? string.Empty,
                State = a.State ?? string.Empty,
                PostalCode = a.PostalCode ?? string.Empty,
                CountryCode = a.CountryCode ?? "US",
                TelephoneNumber = a.TelephoneNumber,
                FaxNumber = a.FaxNumber
            }).ToList();
        }

        // Taxonomies
        if (result.Taxonomies != null)
        {
            data.Taxonomies = result.Taxonomies.Select(t => new NppesTaxonomy
            {
                Code = t.Code ?? string.Empty,
                Description = t.Desc,
                License = t.License,
                State = t.State,
                IsPrimary = t.Primary == true
            }).ToList();
        }

        // Other identifiers
        if (result.Identifiers != null)
        {
            data.OtherIdentifiers = result.Identifiers.Select(i => new NppesIdentifier
            {
                Identifier = i.Identifier ?? string.Empty,
                Type = i.Desc,
                State = i.State,
                Issuer = i.Issuer
            }).ToList();
        }

        // Endpoints
        if (result.Endpoints != null)
        {
            data.Endpoints = result.Endpoints.Select(e => new NppesEndpoint
            {
                EndpointType = e.EndpointType ?? string.Empty,
                EndpointDescription = e.EndpointTypeDescription,
                Endpoint = e.Endpoint ?? string.Empty,
                Affiliation = e.Affiliation,
                ContentType = e.ContentType
            }).ToList();
        }

        return data;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }

    /// <summary>
    /// NPI Luhn validation. NPI uses the Luhn algorithm with
    /// the prefix 80840 prepended before check digit calculation.
    /// </summary>
    private static bool PassesLuhnCheck(string npi)
    {
        var prefixed = "80840" + npi;
        var sum = 0;
        var alternate = false;

        for (var i = prefixed.Length - 1; i >= 0; i--)
        {
            var n = prefixed[i] - '0';
            if (alternate)
            {
                n *= 2;
                if (n > 9) n -= 9;
            }
            sum += n;
            alternate = !alternate;
        }

        return sum % 10 == 0;
    }
}

// ── NPPES API response DTOs ─────────────────────────────────────

internal class NppesApiResponse
{
    [JsonPropertyName("result_count")]
    public int? ResultCount { get; set; }

    [JsonPropertyName("results")]
    public List<NppesApiResult>? Results { get; set; }
}

internal class NppesApiResult
{
    [JsonPropertyName("number")]
    public long? Number { get; set; }

    [JsonPropertyName("enumeration_type")]
    public string? EnumerationType { get; set; }

    [JsonPropertyName("basic")]
    public NppesBasic? Basic { get; set; }

    [JsonPropertyName("addresses")]
    public List<NppesAddressDto>? Addresses { get; set; }

    [JsonPropertyName("taxonomies")]
    public List<NppesTaxonomyDto>? Taxonomies { get; set; }

    [JsonPropertyName("identifiers")]
    public List<NppesIdentifierDto>? Identifiers { get; set; }

    [JsonPropertyName("endpoints")]
    public List<NppesEndpointDto>? Endpoints { get; set; }
}

internal class NppesBasic
{
    [JsonPropertyName("first_name")] public string? FirstName { get; set; }
    [JsonPropertyName("last_name")] public string? LastName { get; set; }
    [JsonPropertyName("middle_name")] public string? MiddleName { get; set; }
    [JsonPropertyName("credential")] public string? Credential { get; set; }
    [JsonPropertyName("organization_name")] public string? OrganizationName { get; set; }
    [JsonPropertyName("organizational_subpart")] public string? OrganizationalSubpart { get; set; }
    [JsonPropertyName("authorized_official_first_name")] public string? AuthorizedOfficialFirstName { get; set; }
    [JsonPropertyName("authorized_official_last_name")] public string? AuthorizedOfficialLastName { get; set; }
    [JsonPropertyName("authorized_official_title_or_position")] public string? AuthorizedOfficialTitleOrPosition { get; set; }
    [JsonPropertyName("enumeration_date")] public string? EnumerationDate { get; set; }
    [JsonPropertyName("last_updated")] public string? LastUpdated { get; set; }
    [JsonPropertyName("deactivation_date")] public string? DeactivationDate { get; set; }
    [JsonPropertyName("reactivation_date")] public string? ReactivationDate { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
}

internal class NppesAddressDto
{
    [JsonPropertyName("address_purpose")] public string? AddressPurpose { get; set; }
    [JsonPropertyName("address_1")] public string? Address1 { get; set; }
    [JsonPropertyName("address_2")] public string? Address2 { get; set; }
    [JsonPropertyName("city")] public string? City { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("postal_code")] public string? PostalCode { get; set; }
    [JsonPropertyName("country_code")] public string? CountryCode { get; set; }
    [JsonPropertyName("telephone_number")] public string? TelephoneNumber { get; set; }
    [JsonPropertyName("fax_number")] public string? FaxNumber { get; set; }
}

internal class NppesTaxonomyDto
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("desc")] public string? Desc { get; set; }
    [JsonPropertyName("license")] public string? License { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("primary")] public bool? Primary { get; set; }
}

internal class NppesIdentifierDto
{
    [JsonPropertyName("identifier")] public string? Identifier { get; set; }
    [JsonPropertyName("desc")] public string? Desc { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("issuer")] public string? Issuer { get; set; }
}

internal class NppesEndpointDto
{
    [JsonPropertyName("endpointType")] public string? EndpointType { get; set; }
    [JsonPropertyName("endpointTypeDescription")] public string? EndpointTypeDescription { get; set; }
    [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }
    [JsonPropertyName("affiliation")] public string? Affiliation { get; set; }
    [JsonPropertyName("contentType")] public string? ContentType { get; set; }
}
