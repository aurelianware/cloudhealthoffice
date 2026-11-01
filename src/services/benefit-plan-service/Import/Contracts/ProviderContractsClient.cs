using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.FeeScheduleEngine.Models;

namespace BenefitPlanService.Import.Contracts;

/// <summary>The fields of a provider-contracts-service legal contract the importer reads and writes.</summary>
public sealed class LegalContractDto
{
    public string? Id { get; set; }
    public string ContractNumber { get; set; } = string.Empty;
    [JsonPropertyName("providerNPI")] public string ProviderNpi { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public LegalProviderType ProviderType { get; set; } = LegalProviderType.Individual;
    public LegalLineOfBusiness LineOfBusiness { get; set; } = LegalLineOfBusiness.Commercial;
    public List<string> PlanIds { get; set; } = new();
    public LegalPaymentMethodology PaymentMethodology { get; set; } = LegalPaymentMethodology.FeeForService;
    public LegalNetworkStatus NetworkStatus { get; set; } = LegalNetworkStatus.Participating;
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public LegalContractStatus Status { get; set; }
    public List<ReimbursementTerm> ReimbursementTerms { get; set; } = new();
}

// Mirrors of provider-contracts-service enums (same names and values).
[JsonConverter(typeof(JsonStringEnumConverter))] public enum LegalProviderType { Individual = 1, Organization = 2 }
[JsonConverter(typeof(JsonStringEnumConverter))] public enum LegalLineOfBusiness { Commercial = 1, Medicare = 2, Medicaid = 3, Exchange = 4, TRICARE = 5, VA = 6 }
[JsonConverter(typeof(JsonStringEnumConverter))] public enum LegalPaymentMethodology { FullCapitation = 1, FeeForService = 2, Hybrid = 3, GlobalRisk = 4 }
[JsonConverter(typeof(JsonStringEnumConverter))] public enum LegalNetworkStatus { Participating = 1, NonParticipating = 2, TieredException = 3 }
[JsonConverter(typeof(JsonStringEnumConverter))] public enum LegalContractStatus { Draft = 1, Active = 2, Suspended = 3, Terminated = 4, Expired = 5, PendingRenewal = 6 }

/// <summary>GET /api/v1/contracts/{id}/pricing-contract: the engine contracts a legal contract prices as.</summary>
public sealed class PricingProjectionDto
{
    public string LegalContractId { get; set; } = string.Empty;
    public List<ProviderContract> Contracts { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

/// <summary>A provider-contracts-service call failed; the message says which and why.</summary>
public sealed class ProviderContractsCallException(string message) : Exception(message);

/// <summary>
/// The provider-contracts-service calls the CHO-mode contract import makes. The
/// caller's own token is forwarded (the shared outbound handler), so the
/// contracts service applies the caller's <c>contracts:write</c>.
/// </summary>
public interface IProviderContractsClient
{
    Task<LegalContractDto?> GetByNumberAsync(string tenantId, string contractNumber, CancellationToken ct);
    Task<LegalContractDto> CreateAsync(string tenantId, LegalContractDto contract, CancellationToken ct);
    Task ReplaceTermsAsync(string tenantId, string id, IReadOnlyList<ReimbursementTerm> terms, CancellationToken ct);
    Task ActivateAsync(string tenantId, string id, CancellationToken ct);
    Task<PricingProjectionDto> GetPricingProjectionAsync(string tenantId, string id, CancellationToken ct);
}

public sealed class HttpProviderContractsClient : IProviderContractsClient
{
    public const string ClientName = "provider-contracts-service";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _factory;

    public HttpProviderContractsClient(IHttpClientFactory factory) => _factory = factory;

    public async Task<LegalContractDto?> GetByNumberAsync(string tenantId, string contractNumber, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/v1/contracts/number/{Uri.EscapeDataString(contractNumber)}", tenantId, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureAsync(response, $"look up contract {contractNumber}", ct);
        return await response.Content.ReadFromJsonAsync<LegalContractDto>(Json, ct);
    }

    public async Task<LegalContractDto> CreateAsync(string tenantId, LegalContractDto contract, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "api/v1/contracts", tenantId, contract, ct);
        await EnsureAsync(response, $"create contract {contract.ContractNumber}", ct);
        return (await response.Content.ReadFromJsonAsync<LegalContractDto>(Json, ct))!;
    }

    public async Task ReplaceTermsAsync(string tenantId, string id, IReadOnlyList<ReimbursementTerm> terms, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, $"api/v1/contracts/{Uri.EscapeDataString(id)}/reimbursement-terms",
            tenantId, new { terms }, ct);
        await EnsureAsync(response, $"replace the rate sheet of contract {id}", ct);
    }

    public async Task ActivateAsync(string tenantId, string id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, $"api/v1/contracts/{Uri.EscapeDataString(id)}/activate", tenantId, null, ct);
        await EnsureAsync(response, $"activate contract {id}", ct);
    }

    public async Task<PricingProjectionDto> GetPricingProjectionAsync(string tenantId, string id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/v1/contracts/{Uri.EscapeDataString(id)}/pricing-contract", tenantId, null, ct);
        await EnsureAsync(response, $"read the pricing projection of contract {id}", ct);
        return (await response.Content.ReadFromJsonAsync<PricingProjectionDto>(Json, ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string tenantId, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Tenant-ID", tenantId);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        return await _factory.CreateClient(ClientName).SendAsync(request, ct);
    }

    private static async Task EnsureAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(ct);
        if (detail.Length > 300) detail = detail[..300];
        throw new ProviderContractsCallException(
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                ? $"Could not {what}: the caller needs contracts:write in provider-contracts-service ({(int)response.StatusCode})."
                : $"Could not {what}: provider-contracts-service answered {(int)response.StatusCode} {detail}");
    }
}
