using System.Net;
using System.Net.Http.Json;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Optional HTTP adapter for inbound 835 claim financials. Not registered by
/// default — <see cref="IRemittancePoster"/> uses the in-memory sink unless a
/// host replaces <see cref="IClaimRemittancePostingSink"/>.
/// Does not call <c>POST /api/claims/{id}/remittance</c> (that is CHO-as-payer
/// PaymentRun finalize and outbound 835 generation). A missing domain claim
/// is skip, not failure.
/// </summary>
public sealed class HttpClaimRemittancePostingSink : IClaimRemittancePostingSink
{
    private readonly IHttpClientFactory _http;

    public HttpClaimRemittancePostingSink(IHttpClientFactory http) => _http = http;

    public async Task<RemittanceClaimPostResult> PostAsync(
        RemittanceClaimPost request,
        CancellationToken cancellationToken = default)
    {
        var client = _http.CreateClient("ClaimsService");
        if (client.BaseAddress is null)
        {
            return new RemittanceClaimPostResult(
                RemittanceClaimPostOutcome.NotFound, "claims-service-not-configured");
        }

        // Inbound ERA posting — not PaymentRun / EraGenerator.
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/claims/{Uri.EscapeDataString(request.ClaimId)}/inbound-remittance")
        {
            Content = JsonContent.Create(new
            {
                remittanceId = request.RemittanceId,
                paymentAmount = request.PaymentAmount,
                patientResponsibility = request.PatientResponsibility,
                paymentDate = request.PaymentDate
            })
        };
        message.Headers.TryAddWithoutValidation("X-Tenant-ID", request.TenantId);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new RemittanceClaimPostResult(RemittanceClaimPostOutcome.Failed, ex.GetType().Name);
        }

        return response.StatusCode switch
        {
            HttpStatusCode.OK or HttpStatusCode.Created =>
                new RemittanceClaimPostResult(RemittanceClaimPostOutcome.Posted),
            HttpStatusCode.Conflict =>
                new RemittanceClaimPostResult(RemittanceClaimPostOutcome.AlreadyPosted),
            HttpStatusCode.NotFound =>
                new RemittanceClaimPostResult(RemittanceClaimPostOutcome.NotFound),
            HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest =>
                new RemittanceClaimPostResult(RemittanceClaimPostOutcome.Rejected, "claim-not-postable"),
            _ => new RemittanceClaimPostResult(RemittanceClaimPostOutcome.Failed, ((int)response.StatusCode).ToString())
        };
    }
}

/// <summary>
/// Optional HTTP adapter for 835 patient-responsibility amounts via
/// <c>POST /api/v1/accumulators/{memberId}/adjust</c>. Not registered by
/// default. Idempotent on remittance+claim. Distinct from
/// <c>claims.finalized.v1</c> (internal adjudication).
/// </summary>
public sealed class HttpRemittanceAccumulatorSink : IRemittanceAccumulatorSink
{
    private readonly IHttpClientFactory _http;

    public HttpRemittanceAccumulatorSink(IHttpClientFactory http) => _http = http;

    public async Task<RemittanceAccumulatorApplyResult> ApplyAsync(
        RemittanceAccumulatorApply request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.MemberId))
        {
            return new RemittanceAccumulatorApplyResult(RemittanceAccumulatorApplyOutcome.Skipped, "missing-member");
        }

        var client = _http.CreateClient("AccumulatorService");
        if (client.BaseAddress is null)
        {
            return new RemittanceAccumulatorApplyResult(
                RemittanceAccumulatorApplyOutcome.Skipped, "accumulator-service-not-configured");
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/v1/accumulators/{Uri.EscapeDataString(request.MemberId)}/adjust")
        {
            Content = JsonContent.Create(new
            {
                planYearStart = request.PlanYearStart,
                planYearEnd = request.PlanYearEnd,
                actorId = "remittance-poster",
                reason = "inbound-835-posting",
                deductibleDelta = request.DeductibleDelta,
                oopDelta = request.OopDelta,
                adjustmentId = $"835|{request.RemittanceId}|{request.ClaimId}"
            })
        };
        message.Headers.TryAddWithoutValidation("X-Tenant-ID", request.TenantId);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new RemittanceAccumulatorApplyResult(
                RemittanceAccumulatorApplyOutcome.Failed, ex.GetType().Name);
        }

        if (response.IsSuccessStatusCode)
        {
            return new RemittanceAccumulatorApplyResult(RemittanceAccumulatorApplyOutcome.Applied);
        }

        return new RemittanceAccumulatorApplyResult(
            RemittanceAccumulatorApplyOutcome.Failed, ((int)response.StatusCode).ToString());
    }
}
