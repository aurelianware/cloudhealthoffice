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
                        ? observed.BusinessDenialCode ?? result.BusinessDenialCode
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

    public async Task<ClaimValidationResult> DetectUnexpectedPendAsync(
        ClaimValidationResult result,
        CancellationToken cancellationToken = default)
        => await DetectUnexpectedPendAsync(result, TimeSpan.Zero, TimeSpan.Zero, cancellationToken);

    public async Task<ClaimValidationResult> DetectUnexpectedPendAsync(
        ClaimValidationResult result,
        TimeSpan terminalObservationTimeout,
        TimeSpan terminalObservationInterval,
        CancellationToken cancellationToken = default)
    {
        if (result.ExpectedOutcome == ClaimValidationOutcome.Pended.ToString()
            || string.IsNullOrWhiteSpace(result.SubmittedClaimId)
            || result.Outcome is ClaimValidationOutcome.PlatformFailure)
        {
            return result;
        }

        try
        {
            var shouldWaitForTerminal = ShouldWaitForPersistedTerminalOutcome(result);
            var observed = shouldWaitForTerminal
                ? await ObserveTerminalAsync(result.SubmittedClaimId, terminalObservationTimeout, terminalObservationInterval, cancellationToken)
                : await _source.GetAsync(result.SubmittedClaimId, cancellationToken);

            if (shouldWaitForTerminal && observed is not { IsTerminal: true })
            {
                return result with
                {
                    ValidationStatus = MccWorkflowValidation.ObservationTimeoutStatus,
                    Outcome = ClaimValidationOutcome.ObservationTimeout,
                    FailureStage = "terminal-status-observation",
                    Error = $"Timed out waiting for persisted claim status to become terminal after {terminalObservationTimeout.TotalSeconds:N0}s"
                };
            }

            if (observed?.Outcome is ClaimValidationOutcome.Pended)
            {
                return result with
                {
                    Outcome = ClaimValidationOutcome.Pended,
                    ValidationStatus = MccWorkflowValidation.MismatchedStatus,
                    FailureStage = "false-pend-observation",
                    Error = $"Expected {result.ExpectedOutcome}, but persisted claim status is pended ({observed.PendCode ?? "no pend code"})"
                };
            }

            if (observed is null || !ShouldReconcilePersistedTerminalOutcome(result, observed))
            {
                return result;
            }

            var expected = ExpectedValidationFromResult(result);
            var businessDenialCode = observed.Outcome is ClaimValidationOutcome.BusinessDenial
                ? observed.BusinessDenialCode ?? result.BusinessDenialCode
                : null;

            return result with
            {
                Outcome = observed.Outcome,
                BusinessDenialCode = businessDenialCode,
                ValidationStatus = MccWorkflowValidation.ValidationStatus(
                    expected,
                    observed.Outcome,
                    businessDenialCode),
                FailureStage = "terminal-status-observation"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return result with
            {
                ValidationStatus = MccWorkflowValidation.ObservationTimeoutStatus,
                Outcome = ClaimValidationOutcome.ObservationTimeout,
                FailureStage = "false-pend-observation",
                Error = $"Claim status observation failed: {ex.Message}"
            };
        }
    }

    private async Task<ObservedClaimStatus?> ObserveTerminalAsync(
        string submittedClaimId,
        TimeSpan timeout,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (true)
        {
            var observed = await _source.GetAsync(submittedClaimId, cancellationToken);
            if (observed?.IsTerminal is true)
            {
                return observed;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero || timeout <= TimeSpan.Zero)
            {
                return observed;
            }

            var delay = interval <= TimeSpan.Zero || interval < remaining ? interval : remaining;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static bool ShouldWaitForPersistedTerminalOutcome(ClaimValidationResult result)
        => result.ExpectedOutcome == ClaimValidationOutcome.BusinessDenial.ToString()
            && result.ValidationStatus == MccWorkflowValidation.MismatchedStatus;

    private static bool ShouldReconcilePersistedTerminalOutcome(
        ClaimValidationResult result,
        ObservedClaimStatus observed)
        => observed.IsTerminal
            && observed.Outcome is ClaimValidationOutcome.Paid or ClaimValidationOutcome.BusinessDenial
            && (observed.Outcome != result.Outcome
                || (observed.Outcome is ClaimValidationOutcome.BusinessDenial
                    && !string.Equals(
                        observed.BusinessDenialCode,
                        result.BusinessDenialCode,
                        StringComparison.OrdinalIgnoreCase)));

    private static ExpectedValidation ExpectedValidationFromResult(ClaimValidationResult result)
        => new(
            result.ValidationScenario,
            ParseExpectedOutcome(result.ExpectedOutcome),
            result.ExpectedBusinessDenialCode);

    private static ClaimValidationOutcome? ParseExpectedOutcome(string? expectedOutcome)
        => expectedOutcome?.Trim() switch
        {
            { } value when value.Equals(ClaimValidationOutcome.Paid.ToString(), StringComparison.OrdinalIgnoreCase) =>
                ClaimValidationOutcome.Paid,
            { } value when value.Equals(ClaimValidationOutcome.BusinessDenial.ToString(), StringComparison.OrdinalIgnoreCase) =>
                ClaimValidationOutcome.BusinessDenial,
            { } value when value.Equals(ClaimValidationOutcome.Pended.ToString(), StringComparison.OrdinalIgnoreCase) =>
                ClaimValidationOutcome.Pended,
            _ => null
        };
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
