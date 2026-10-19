using System.Net;
using System.Text.Json;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

internal interface IClaimStatusSource
{
    Task<ObservedClaimStatus?> GetAsync(string submittedClaimId, CancellationToken cancellationToken);
}

internal sealed class HttpClaimStatusSource : IClaimStatusSource
{
    private readonly HttpClient _http;
    private readonly string _claimsUrl;

    public HttpClaimStatusSource(HttpClient http, string claimsUrl)
    {
        _http = http;
        _claimsUrl = claimsUrl.TrimEnd('/');
    }

    public async Task<ObservedClaimStatus?> GetAsync(string submittedClaimId, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync($"{_claimsUrl}/api/claims/{submittedClaimId}", cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                || (int)response.StatusCode >= 500)
            {
                // A transient read failure means the persisted outcome is not
                // observable yet; callers retain it for bounded reconciliation.
                return null;
            }

            throw new InvalidOperationException(
                $"claim status read failed ({submittedClaimId}): {(int)response.StatusCode} {body}");
        }

        using var document = JsonDocument.Parse(body);
        return ObservedClaimStatus.FromClaimJson(document.RootElement);
    }
}

internal sealed record ObservedClaimStatus(
    ClaimValidationOutcome Outcome,
    string RawStatus,
    string? PendCode,
    bool IsTerminal,
    string? BusinessDenialCode = null,
    decimal? PlanPayment = null)
{
    public static ObservedClaimStatus FromClaimJson(JsonElement root)
    {
        var rawStatus = TryReadStringOrNumber(root, "status") ?? string.Empty;
        var pendCode = TryReadPendCode(root);
        var businessDenialCode = MccWorkflowValidation.NormalizeBusinessDenialCode(TryReadBusinessDenialCode(root));
        var planPayment = TryReadPlanPayment(root);
        var outcome = rawStatus.Trim() switch
        {
            "4" => ClaimValidationOutcome.Pended,
            "5" or "7" or "9" => ClaimValidationOutcome.Paid,
            "6" or "8" or "10" => ClaimValidationOutcome.BusinessDenial,
            { } value when value.Equals("Pended", StringComparison.OrdinalIgnoreCase) => ClaimValidationOutcome.Pended,
            { } value when value.Equals("Approved", StringComparison.OrdinalIgnoreCase) => ClaimValidationOutcome.Paid,
            { } value when value.Equals("Paid", StringComparison.OrdinalIgnoreCase) => ClaimValidationOutcome.Paid,
            { } value when value.Equals("PartiallyPaid", StringComparison.OrdinalIgnoreCase) => ClaimValidationOutcome.Paid,
            { } value when value.Equals("Denied", StringComparison.OrdinalIgnoreCase) => ClaimValidationOutcome.BusinessDenial,
            { } value when value.Equals("Deny", StringComparison.OrdinalIgnoreCase) => ClaimValidationOutcome.BusinessDenial,
            { } value when value.Equals("Voided", StringComparison.OrdinalIgnoreCase) => ClaimValidationOutcome.BusinessDenial,
            _ => ClaimValidationOutcome.PlatformFailure
        };

        return new ObservedClaimStatus(
            outcome,
            rawStatus,
            pendCode,
            outcome is ClaimValidationOutcome.Pended
                or ClaimValidationOutcome.Paid
                or ClaimValidationOutcome.BusinessDenial,
            businessDenialCode,
            planPayment);
    }

    private static string? TryReadStringOrNumber(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt32(out var number) ? number.ToString() : value.GetRawText(),
            _ => null
        };
    }

    private static string? TryReadPendCode(JsonElement root)
    {
        if (!root.TryGetProperty("pendDetails", out var pendDetails)
            || pendDetails.ValueKind is not JsonValueKind.Object
            || !pendDetails.TryGetProperty("pendCode", out var pendCode)
            || pendCode.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        return pendCode.GetString();
    }

    private static string? TryReadBusinessDenialCode(JsonElement root)
    {
        if (!root.TryGetProperty("adjudicationResult", out var adjudicationResult)
            || adjudicationResult.ValueKind is not JsonValueKind.Object
            || !adjudicationResult.TryGetProperty("denialReasonCode", out var denialReasonCode))
        {
            return null;
        }

        return denialReasonCode.ValueKind switch
        {
            JsonValueKind.String => denialReasonCode.GetString(),
            JsonValueKind.Number => denialReasonCode.TryGetInt32(out var number)
                ? number.ToString()
                : denialReasonCode.GetRawText(),
            _ => null
        };
    }

    private static decimal? TryReadPlanPayment(JsonElement root)
    {
        if (!root.TryGetProperty("adjudicationResult", out var adjudicationResult)
            || adjudicationResult.ValueKind is not JsonValueKind.Object
            || !adjudicationResult.TryGetProperty("payerPayment", out var payerPayment))
        {
            return null;
        }

        return payerPayment.ValueKind switch
        {
            JsonValueKind.Number when payerPayment.TryGetDecimal(out var amount) => amount,
            JsonValueKind.String when decimal.TryParse(
                payerPayment.GetString(),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var amount) => amount,
            _ => null
        };
    }
}
