using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// A payment: one check or EFT to a payee, with the claims it paid and the
/// provider-level adjustments (PLB) and recoupments taken from it
/// (payment-service <c>/payments</c>; needs <c>payments:read</c>).
/// </summary>
public sealed class PaymentView
{
    public string Id { get; set; } = string.Empty;
    public string CheckNumber { get; set; } = string.Empty;

    /// <summary>ACH, CHK, ...</summary>
    public string PaymentMethod { get; set; } = string.Empty;

    public decimal TotalPaymentAmount { get; set; }
    public DateTime PaymentDate { get; set; }
    public string PayerName { get; set; } = string.Empty;
    public string? PayerId { get; set; }
    public string PayeeName { get; set; } = string.Empty;
    public string? PayeeNPI { get; set; }
    public PayeeAddressView? PayeeAddress { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public DateTime? PostedAt { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? ReconciledAt { get; set; }
    public string? ReconciledBy { get; set; }
    public string? Notes { get; set; }
    public List<string> TraceNumbers { get; set; } = new();
    public bool IsReversal { get; set; }
    public string? RunId { get; set; }
    public string? RunNumber { get; set; }
    public DateTime? VoidedBeforePaymentAt { get; set; }
    public List<ClaimPaymentView> ClaimPayments { get; set; } = new();
    public List<ProviderAdjustmentView> ProviderAdjustments { get; set; } = new();
    public List<ReceivableOffsetView> ReceivableOffsets { get; set; } = new();

    public bool IsEft => string.Equals(PaymentMethod, "ACH", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(PaymentMethod, "EFT", StringComparison.OrdinalIgnoreCase);

    public string Label => $"{(IsEft ? "EFT" : "Check")} {CheckNumber}";
}

public sealed class PayeeAddressView
{
    public string Line1 { get; set; } = string.Empty;
    public string? Line2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string? State { get; set; }
    public string? PostalCode { get; set; }

    public override string ToString() =>
        string.Join(", ", new[] { Line1, Line2, City, $"{State} {PostalCode}".Trim() }.Where(p => !string.IsNullOrWhiteSpace(p)));
}

public sealed class ClaimPaymentView
{
    public string ClaimId { get; set; } = string.Empty;
    public string PatientControlNumber { get; set; } = string.Empty;

    /// <summary>835 CLP02: 1 processed as primary, 4 denied, 22 reversal, ...</summary>
    public string ClaimStatusCode { get; set; } = string.Empty;

    public decimal ChargeAmount { get; set; }
    public decimal PaymentAmount { get; set; }
    public decimal PatientResponsibilityAmount { get; set; }
    public string? MemberId { get; set; }
    public List<ClaimAdjustmentView> ClaimAdjustments { get; set; } = new();

    public string StatusLabel => ClaimStatusCode switch
    {
        "1" => "Primary",
        "2" => "Secondary",
        "3" => "Tertiary",
        "4" => "Denied",
        "19" => "Primary, forwarded",
        "20" => "Secondary, forwarded",
        "22" => "Reversal",
        _ => ClaimStatusCode,
    };
}

public sealed class ClaimAdjustmentView
{
    public string GroupCode { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

/// <summary>A PLB provider-level adjustment (interest, forward balance, recoupment, ...).</summary>
public sealed class ProviderAdjustmentView
{
    public string AdjustmentIdentifier { get; set; } = string.Empty;
    public string? ReferenceIdentification { get; set; }
    public decimal Amount { get; set; }
    public DateTime? FiscalPeriodEnd { get; set; }

    public string Description => (AdjustmentIdentifier.Length >= 2 ? AdjustmentIdentifier[..2] : AdjustmentIdentifier) switch
    {
        "L6" => "Interest owed",
        "FB" => "Forward balance",
        "WO" => "Overpayment recovery",
        "72" => "Authorized return",
        "CS" => "Adjustment",
        "J1" => "Non-reimbursable",
        "IR" => "Internal Revenue Service withholding",
        "LS" => "Lump sum",
        _ => AdjustmentIdentifier,
    };
}

/// <summary>A provider receivable recouped from this payment.</summary>
public sealed class ReceivableOffsetView
{
    public string ReceivableId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string AdjustmentCode { get; set; } = string.Empty;
    public string? Reference { get; set; }
}

/// <summary>Payments (checks / EFTs) from payment-service.</summary>
public interface IPaymentService
{
    /// <summary>A payment by id; null when it doesn't exist.</summary>
    Task<PaymentView?> GetPaymentAsync(string id);

    /// <summary>A payment by check or EFT trace number; null when none.</summary>
    Task<PaymentView?> GetPaymentByCheckNumberAsync(string checkNumber);

    /// <summary>Every payment that paid (or recouped) a claim.</summary>
    Task<List<PaymentView>> GetPaymentsForClaimAsync(string claimId);

    /// <summary>A payee's checks and EFTs by NPI, newest first.</summary>
    Task<List<PaymentView>> GetPaymentsForPayeeAsync(string npi, int limit = 100);
}

public sealed class PaymentService : IPaymentService
{
    public const string ServiceName = "Payment Service";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(HttpClient http, IConfiguration configuration, ILogger<PaymentService> logger)
    {
        _http = http;
        _configuration = configuration;
        _logger = logger;
    }

    private string BaseUrl => (_configuration["Services:PaymentService"] ?? "http://payment-service.cloudhealthoffice/api").TrimEnd('/') + "/payments";

    private static string E(string value) => Uri.EscapeDataString(value);

    private async Task<T?> GetAsync<T>(string path) where T : class
    {
        try
        {
            using var response = await _http.GetAsync($"{BaseUrl}{path}");
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(Json);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", ServiceName);
            throw new ServiceUnavailableException(ServiceName, ex);
        }
    }

    public Task<PaymentView?> GetPaymentAsync(string id) => GetAsync<PaymentView>($"/{E(id)}");

    public Task<PaymentView?> GetPaymentByCheckNumberAsync(string checkNumber) => GetAsync<PaymentView>($"/check/{E(checkNumber)}");

    public async Task<List<PaymentView>> GetPaymentsForClaimAsync(string claimId) =>
        await GetAsync<List<PaymentView>>($"/claim/{E(claimId)}") ?? new();

    public async Task<List<PaymentView>> GetPaymentsForPayeeAsync(string npi, int limit = 100) =>
        await GetAsync<List<PaymentView>>($"/payee/{E(npi)}?limit={limit}") ?? new();
}
