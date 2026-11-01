using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;

namespace PaymentService.Services.InformationReturns;

/// <summary>A payee's TIN and legal name as provider-service holds them. The TIN is never logged or returned whole.</summary>
public sealed record PayeeTaxIdentity(string Npi, string? TaxId, string? TaxIdType, string LegalName, bool W9OnFile)
{
    public string? TaxIdDigits => TaxId == null ? null : new string(TaxId.Where(char.IsDigit).ToArray());
}

public interface IProviderTaxIdentitySource
{
    /// <summary>The payee's tax identity; null when the NPI is unknown. Throws <see cref="HttpRequestException"/> when provider-service cannot answer.</summary>
    Task<PayeeTaxIdentity?> GetAsync(string tenantId, string npi, CancellationToken ct = default);
}

/// <summary>
/// provider-service's service-only <c>GET api/v1/internal/providers/npi/{npi}/tax-identity</c>,
/// called with payment-service's own identity for the caller's tenant: the
/// finance user totalling 1099s need not be able to read provider TINs.
/// </summary>
public sealed class HttpProviderTaxIdentitySource(IHttpClientFactory factory) : IProviderTaxIdentitySource
{
    public const string HttpClientName = "ProviderTaxIdentity";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<PayeeTaxIdentity?> GetAsync(string tenantId, string npi, CancellationToken ct = default)
    {
        var client = factory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/internal/providers/npi/{Uri.EscapeDataString(npi)}/tax-identity");
        request.Headers.Add(TenantMiddleware.TenantHeaderName, tenantId);
        request.UseChoServiceIdentity();

        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"provider-service answered {(int)response.StatusCode} to the tax identity read");
        var body = await response.Content.ReadFromJsonAsync<Body>(JsonOptions, ct)
                   ?? throw new HttpRequestException("provider-service returned an empty tax identity");
        return new PayeeTaxIdentity(npi, body.TaxId, body.TaxIdType, body.LegalName ?? string.Empty, body.W9OnFile);
    }

    private sealed class Body
    {
        public string? TaxId { get; set; }
        public string? TaxIdType { get; set; }
        public string? LegalName { get; set; }
        public bool W9OnFile { get; set; }
    }
}
