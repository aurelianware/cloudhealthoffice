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
            throw new InvalidOperationException(
                $"claim status read failed ({submittedClaimId}): {(int)response.StatusCode} {body}");
        }

        using var document = JsonDocument.Parse(body);
        return ObservedClaimStatus.FromClaimJson(document.RootElement);
    }
}

internal sealed class MccClaimStatusObserver
{
    private readonly IClaimStatusSource _source;

    public MccClaimStatusObserver(IClaimStatusSource source)
    {
        _source = source;
    }

    public async Task<ClaimValidationResult> ObserveExpectedPendAsync(
        ClaimValidationResult result,
        TimeSpan timeout,
        TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        if (result.ExpectedOutcome != ClaimValidationOutcome.Pended.ToString()
            || string.IsNullOrWhiteSpace(result.SubmittedClaimId)
            || result.Outcome is ClaimValidationOutcome.PlatformFailure)
        {
            return result;
        }

        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (true)
        {
            ObservedClaimStatus? observed;
            try
            {
                observed = await _source.GetAsync(result.SubmittedClaimId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return result with
                {
                    ValidationStatus = MccWorkflowValidation.ObservationTimeoutStatus,
                    Outcome = ClaimValidationOutcome.ObservationTimeout,
                    FailureStage = "pend-observation",
                    Error = $"Claim status observation failed: {ex.Message}"
                };
            }

            if (observed is { IsTerminal: true })
            {
                return result with
                {
                    ValidationStatus = MccWorkflowValidation.ValidationStatus(
                        new ExpectedValidation(
                            result.ValidationScenario,
                            ClaimValidationOutcome.Pended,
                            result.ExpectedBusinessDenialCode),
                        observed.Outcome,
                        result.BusinessDenialCode),
                    Outcome = observed.Outcome,
                    BusinessDenialCode = observed.Outcome is ClaimValidationOutcome.BusinessDenial
                        ? result.BusinessDenialCode
                        : null
                };
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return result with
                {
                    ValidationStatus = MccWorkflowValidation.ObservationTimeoutStatus,
                    Outcome = ClaimValidationOutcome.ObservationTimeout,
                    FailureStage = "pend-observation",
                    Error = $"Timed out waiting for claim status to become terminal or pended after {timeout.TotalSeconds:N0}s"
                };
            }

            var delay = interval <= TimeSpan.Zero || interval < remaining ? interval : remaining;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }
    }
}

internal sealed record ObservedClaimStatus(
    ClaimValidationOutcome Outcome,
    string RawStatus,
    string? PendCode,
    bool IsTerminal)
{
    public static ObservedClaimStatus FromClaimJson(JsonElement root)
    {
        var rawStatus = TryReadStringOrNumber(root, "status") ?? string.Empty;
        var pendCode = TryReadPendCode(root);
        var outcome = rawStatus.Trim() switch
        {
            "4" => ClaimValidationOutcome.Pended,
            "5" or "7" or "9" => ClaimValidationOutcome.Paid,
            "6" or "8" => ClaimValidationOutcome.BusinessDenial,
            { } value when value.Equals("Pended", StringComparison.OrdinalIgnoreCase) => ClaimValidationOutcome.Pended,
            { } value when value.Equals("Approved", StringComparison.OrdinalIgnoreCase) => ClaimValidationOutcome.Paid,
            { } value when value.Equals("Paid", StringComparison.OrdinalIgnoreCase) => ClaimValidationOutcome.Paid,
            { } value when value.Equals("PartiallyPaid", StringComparison.OrdinalIgnoreCase) => ClaimValidationOutcome.Paid,
            { } value when value.Equals("Denied", StringComparison.OrdinalIgnoreCase) => ClaimValidationOutcome.BusinessDenial,
            { } value when value.Equals("Voided", StringComparison.OrdinalIgnoreCase) => ClaimValidationOutcome.BusinessDenial,
            _ => ClaimValidationOutcome.PlatformFailure
        };

        return new ObservedClaimStatus(
            outcome,
            rawStatus,
            pendCode,
            outcome is ClaimValidationOutcome.Pended
                or ClaimValidationOutcome.Paid
                or ClaimValidationOutcome.BusinessDenial);
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
}
