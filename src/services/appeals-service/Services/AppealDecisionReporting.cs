using System.Net;
using System.Net.Http.Json;
using AppealsService.Models;
using AppealsService.Repositories;
using CloudHealthOffice.Infrastructure.Security;

namespace AppealsService.Services;

/// <summary>What authorization-service did with one AppealDecision fact.</summary>
public enum AppealDecisionDelivery { Accepted, Rejected, RetryLater }

/// <summary>
/// Sends a decided prior-authorization appeal to authorization-service as an
/// <c>AppealDecision</c> fact on the authorization's CMS-0057-F reporting ledger
/// (<c>POST api/authorizations/cms-0057/{id}/facts</c>), so the public metrics
/// count approvals after appeal.
/// </summary>
public interface IAppealDecisionReporter
{
    Task<(AppealDecisionDelivery Delivery, string? Reason)> SendAsync(MemberServiceCase decided, CancellationToken ct);
}

public sealed class HttpAppealDecisionReporter(IHttpClientFactory clients) : IAppealDecisionReporter
{
    public const string SourceSystem = "appeals-service";

    /// <summary>The ledger takes clinical outcomes: a reversed denial is an approval.</summary>
    public static string LedgerOutcome(MemberAppealOutcome outcome) => outcome switch
    {
        MemberAppealOutcome.Upheld => "Denied",
        MemberAppealOutcome.Overturned => "Approved",
        MemberAppealOutcome.PartiallyOverturned => "Modified",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    public async Task<(AppealDecisionDelivery Delivery, string? Reason)> SendAsync(MemberServiceCase decided, CancellationToken ct)
    {
        if (decided.DecisionOutcome is not { } outcome || decided.DecisionRecordedAt is not { } at
            || string.IsNullOrWhiteSpace(decided.AuthorizationId))
            return (AppealDecisionDelivery.Rejected, "The case has no authorization appeal outcome to report.");

        // The case id (contact-<hex>) is the idempotency key: a retry after a lost
        // response is accepted as the same fact, never recorded twice.
        var reference = "service-case:" + decided.Id;
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"api/authorizations/cms-0057/{Uri.EscapeDataString(decided.AuthorizationId)}/facts")
        {
            Content = JsonContent.Create(new
            {
                EventId = reference, Kind = "AppealDecision",
                EffectiveAt = new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)),
                Outcome = LedgerOutcome(outcome), SourceSystem, EvidenceReference = reference,
            }),
        };
        // Reporting is this service's pipeline, not something the reviewer's own
        // role must be allowed to do: send it as the service, for the case's tenant.
        request.Headers.Add("X-Tenant-ID", decided.TenantId);
        request.UseChoServiceIdentity();
        try
        {
            using var response = await clients.CreateClient("ContactAppealAuthorization").SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return (AppealDecisionDelivery.Accepted, null);
            return response.StatusCode switch
            {
                // The fact itself is refused (no evidenced adverse determination, a
                // time outside the ledger) or the authorization is gone: retrying
                // will not change the answer.
                HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity =>
                    (AppealDecisionDelivery.Rejected, $"authorization-service refused the fact ({(int)response.StatusCode})."),
                // 409: legacy record awaiting reconciliation or a non-authoritative
                // backend; 401/403/5xx: deployment or outage. All can clear.
                _ => (AppealDecisionDelivery.RetryLater, $"authorization-service answered {(int)response.StatusCode}."),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (AppealDecisionDelivery.RetryLater, "authorization-service was unreachable.");
        }
    }
}

/// <summary>
/// One delivery attempt for a decided appeal, written back to the case: accepted,
/// refused for good, or left owed for <see cref="HostedServices.AppealDecisionReportingSweep"/>.
/// </summary>
public sealed class AppealDecisionReporting(IAppealDecisionReporter reporter, IMemberServiceCaseStore store,
    ILogger<AppealDecisionReporting> logger)
{
    /// <summary>Attempts after which a fact that still cannot be delivered is given up and flagged (about four days of backoff).</summary>
    public const int MaxAttempts = 24;

    /// <summary>Wait before the next try after <paramref name="attempts"/> failures: 1, 2, 4 ... minutes, at most six hours.</summary>
    public static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromMinutes(Math.Min(360, Math.Pow(2, Math.Clamp(attempts - 1, 0, 16))));

    /// <summary>Never throws except on cancellation: a failure leaves the case owed for the sweep.</summary>
    public async Task<AppealDecisionDelivery> DeliverAsync(MemberServiceCase decided, CancellationToken ct)
    {
        try { return await DeliverOnceAsync(decided, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Appeal outcome for service case {CaseId} could not be reported; the sweep retries it.", decided.Id);
            return AppealDecisionDelivery.RetryLater;
        }
    }

    private async Task<AppealDecisionDelivery> DeliverOnceAsync(MemberServiceCase decided, CancellationToken ct)
    {
        var (delivery, reason) = await reporter.SendAsync(decided, ct);
        var now = DateTime.UtcNow;
        switch (delivery)
        {
            case AppealDecisionDelivery.Accepted:
                await store.RecordReportingAttemptAsync(decided.TenantId, decided.Id, now, null, null, CancellationToken.None);
                break;
            case AppealDecisionDelivery.Rejected:
                logger.LogWarning("Appeal outcome for service case {CaseId} was refused by the PA reporting ledger: {Reason}",
                    decided.Id, reason);
                await store.RecordReportingAttemptAsync(decided.TenantId, decided.Id, null, reason, null, CancellationToken.None);
                break;
            default:
                var attempts = decided.ReportingAttempts + 1;
                var exhausted = attempts >= MaxAttempts;
                logger.LogWarning("Appeal outcome for service case {CaseId} is not yet on the PA reporting ledger " +
                    "(attempt {Attempt}): {Reason}", decided.Id, attempts, reason);
                await store.RecordReportingAttemptAsync(decided.TenantId, decided.Id, null,
                    exhausted ? $"Gave up after {MaxAttempts} attempts; last: {reason}" : null,
                    exhausted ? null : now + Backoff(attempts), CancellationToken.None);
                break;
        }
        return delivery;
    }
}
