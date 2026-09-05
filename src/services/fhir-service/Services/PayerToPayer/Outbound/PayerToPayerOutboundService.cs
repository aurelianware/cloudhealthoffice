using FhirService.Models;
using FhirService.Models.PayerToPayer;
using FhirService.Services.PayerToPayer.Ingestion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirService.Services.PayerToPayer.Outbound;

/// <summary>
/// Application service for outbound Payer-to-Payer initiation (CMS-0057-F
/// P2P-02). On an authorized coverage transition, Cloud Health Office — as the
/// member's new/current payer — initiates the exchange against another payer,
/// resolves the member with that payer, requests the member-scoped data package,
/// and records the outcome.
///
/// Order of operations (fail closed, and nothing leaves CHO until every local
/// gate has passed):
///   1. tenant scope — a member of another tenant is never initiated on;
///   2. member + prior-coverage context — from CHO's own authoritative data;
///   3. target payer endpoint — resolved from the trusted directory only
///      (a caller names a payer id, never a URL: no SSRF);
///   4. authorization — the member's opt-in, decided server-side by
///      <see cref="IPayerToPayerConsentGate"/>, never supplied by the caller.
///      Without it NOTHING is sent to the remote payer — not even the identity
///      that a member-match would carry;
///   5. remote <c>$member-match</c> — identity resolution owned by the REMOTE
///      payer (CHO builds the request and interprets the answer; it does not
///      re-run matching rules on the peer's behalf);
///   6. remote member-data export — issued ONLY after a single member resolved;
///   7. validation — the package must parse and be consistent with the matched
///      member before the exchange is treated as complete;
///   8. provenance + audit + exchange state.
///
/// This is the domain/application layer. The controller is a thin routing
/// surface over it, and the transport
/// (<see cref="IPayerToPayerRemoteClient"/>) is a seam, so no HTTP detail
/// reaches the workflow.
///
///   9. durable ingestion — the validated package is written to CHO's imported
///      member record by <see cref="IPayerToPayerPackageIngestionService"/>, and
///      the exchange reaches Completed ONLY once that commit lands. A package
///      that was retrieved but not stored leaves the exchange retryable, never
///      reported as success.
/// </summary>
public interface IPayerToPayerOutboundService
{
    Task<PayerToPayerOutboundResult> InitiateAsync(
        PayerToPayerOutboundRequest request, CancellationToken ct = default);
}

public sealed class PayerToPayerOutboundService : IPayerToPayerOutboundService
{
    private readonly IPayerToPayerMemberSource _memberSource;
    private readonly IPayerToPayerMemberMatchSource _coverageSource;
    private readonly IPayerToPayerConsentGate _consentGate;
    private readonly IPayerToPayerEndpointResolver _endpoints;
    private readonly IPayerToPayerRemoteClient _remote;
    private readonly IPayerToPayerOutboundExchangeStore _store;
    private readonly IPayerToPayerPackageIngestionService _ingestion;
    private readonly IOptions<PayerToPayerDirectoryOptions> _directory;
    private readonly ILogger<PayerToPayerOutboundService> _logger;

    public PayerToPayerOutboundService(
        IPayerToPayerMemberSource memberSource,
        IPayerToPayerMemberMatchSource coverageSource,
        IPayerToPayerConsentGate consentGate,
        IPayerToPayerEndpointResolver endpoints,
        IPayerToPayerRemoteClient remote,
        IPayerToPayerOutboundExchangeStore store,
        IPayerToPayerPackageIngestionService ingestion,
        IOptions<PayerToPayerDirectoryOptions> directory,
        ILogger<PayerToPayerOutboundService> logger)
    {
        _memberSource = memberSource;
        _coverageSource = coverageSource;
        _consentGate = consentGate;
        _endpoints = endpoints;
        _remote = remote;
        _store = store;
        _ingestion = ingestion;
        _directory = directory;
        _logger = logger;
    }

    public async Task<PayerToPayerOutboundResult> InitiateAsync(
        PayerToPayerOutboundRequest request, CancellationToken ct = default)
    {
        var tenantId = request.TenantId?.Trim() ?? string.Empty;
        var memberId = request.MemberId?.Trim() ?? string.Empty;
        var targetPayerId = request.TargetPayerId?.Trim() ?? string.Empty;

        // 1. Tenant scope. A member/coverage context of another tenant can never
        //    be initiated on, and nothing is recorded or sent.
        if (!string.Equals(tenantId, _memberSource.ServedTenantId, StringComparison.Ordinal))
            return Unrecorded(request, tenantId, memberId, targetPayerId,
                PayerToPayerOutboundStatus.Failed, PayerToPayerOutboundFailure.TenantMismatch);

        if (memberId.Length == 0)
            return Unrecorded(request, tenantId, memberId, targetPayerId,
                PayerToPayerOutboundStatus.Failed, PayerToPayerOutboundFailure.MemberNotFound);

        if (targetPayerId.Length == 0)
            return Unrecorded(request, tenantId, memberId, targetPayerId,
                PayerToPayerOutboundStatus.Failed, PayerToPayerOutboundFailure.TargetPayerNotConfigured);

        // Idempotency: one exchange per coverage transition. A completed or
        // in-flight exchange is replayed rather than re-issued; a previously
        // FAILED exchange is retried under its own id (so a retry after a fixed
        // config or a restored peer resumes, instead of stacking exchanges).
        var idempotencyKey = IdempotencyKey(tenantId, memberId, targetPayerId, request.TransitionKey);
        var candidate = new PayerToPayerOutboundExchange
        {
            ExchangeId = Guid.NewGuid().ToString("n"),
            TenantId = tenantId,
            MemberId = memberId,
            TargetPayerId = targetPayerId,
            IdempotencyKey = idempotencyKey,
        };

        var (exchange, isNew) = await _store.ReserveAsync(candidate, ct);
        if (!isNew && !IsRetryable(exchange))
            return Replay(request, exchange);

        if (!isNew) ResetForRetry(exchange);

        // 2. The member and the coverage that established the relationship with
        //    the target payer — CHO's own authoritative data.
        var member = await ResolveMemberAsync(tenantId, memberId, ct);
        if (member is null)
            return await FailAsync(request, exchange, PayerToPayerOutboundStatus.Failed,
                PayerToPayerOutboundFailure.MemberNotFound, ct);

        var coverage = await ResolveTargetCoverageAsync(tenantId, memberId, targetPayerId, request, ct);
        if (coverage.Outcome == CoverageSelectionOutcome.Ambiguous)
            return await FailAsync(request, exchange, PayerToPayerOutboundStatus.Failed,
                PayerToPayerOutboundFailure.LocalCoverageAmbiguous, ct);
        exchange.LocalCoverageId = coverage.Coverage?.CoverageId;

        // 3. Target payer endpoint — trusted directory only.
        var endpoint = _endpoints.Resolve(tenantId, targetPayerId);
        if (endpoint is null)
            return await FailAsync(request, exchange, PayerToPayerOutboundStatus.Failed,
                PayerToPayerOutboundFailure.TargetPayerNotConfigured, ct);
        exchange.TargetEndpointKey = endpoint.EndpointKey;

        // 4. Authorization. Server-side opt-in state decides; a caller cannot
        //    attest it. Enforced BEFORE any call, so an unauthorized member's
        //    identity is never disclosed to another payer. (Generic active
        //    opt-in; no dedicated Payer-to-Payer ConsentType — P2P-03 stays
        //    PARTIAL.)
        if (!await _consentGate.HasActiveOptInAsync(tenantId, member.MemberId, ct))
            return await FailAsync(request, exchange, PayerToPayerOutboundStatus.NotAuthorized,
                PayerToPayerOutboundFailure.NotAuthorized, ct);

        // 5. Remote member-match.
        var priorPayerMemberId = coverage.Coverage?.SubscriberId;
        var matched = await MatchWithRemoteAsync(request, exchange, endpoint, member, priorPayerMemberId, ct);
        if (matched.Failure is { } matchFailure)
            return await FailAsync(request, exchange, matchFailure.Status, matchFailure.Failure, ct);

        // MemberMatchOutcome is owned by MatchWithRemoteAsync — it records the
        // peer's own outcome, or "Skipped" when the match step was not needed.
        // Restating it here would report a match that never happened.
        exchange.RemoteMemberId = matched.RemoteMemberId;
        exchange.Status = PayerToPayerOutboundStatus.Matched;
        await _store.SaveAsync(exchange, ct);

        // 6. Member-data export — only now that exactly one member resolved.
        exchange.Status = PayerToPayerOutboundStatus.RequestingData;
        await _store.SaveAsync(exchange, ct);

        var dataResponse = await _remote.RequestMemberDataAsync(endpoint, new RemoteMemberDataRequest
        {
            ReceivingPayerId = _directory.Value.LocalPayerId,
            MemberId = matched.RemoteMemberId!,
            LookbackYears = request.LookbackYears,
        }, ct);

        exchange.ExportOutcome = dataResponse.Outcome.ToString();
        if (dataResponse.Outcome != RemoteCallOutcome.Success)
            return await FailAsync(request, exchange, PayerToPayerOutboundStatus.Failed,
                MapTransportFailure(dataResponse.Outcome), ct);

        // 7. Validate before accepting. An unparseable, empty, or
        //    member-inconsistent package is rejected — no partial acceptance.
        var validation = PayerToPayerResponseReader.ValidatePackage(
            dataResponse.Payload, matched.RemoteMemberId!);
        if (!validation.IsValid)
        {
            _logger.LogWarning(
                "P2P outbound package rejected: exchange={Exchange} peer={EndpointKey} reason={Reason}",
                Clean(exchange.ExchangeId), Clean(exchange.TargetEndpointKey), validation.Outcome);
            return await FailAsync(request, exchange, PayerToPayerOutboundStatus.Failed,
                PayerToPayerOutboundFailure.InvalidRemoteResponse, ct);
        }

        // 8. Provenance, state, audit.
        var provenance = new PayerToPayerSourceProvenance
        {
            SourcePayerId = endpoint.PayerId,
            SourceEndpointKey = endpoint.EndpointKey,
            ExchangeId = exchange.ExchangeId,
            ReceivedAtUtc = DateTime.UtcNow,
        };
        var package = new PayerToPayerReceivedPackage
        {
            Bundle = PayerToPayerResponseReader.StampProvenance(validation.Bundle!, provenance),
            RemoteMemberId = matched.RemoteMemberId!,
            ResourceCount = validation.ResourceCount,
            Provenance = provenance,
        };

        // The package is in hand but not yet durable. Recording DataReceived
        // before ingesting means a crash here reads as "retrieved, not stored" —
        // which is the truth — instead of a silent success.
        exchange.ReceivedResourceCount = validation.ResourceCount;
        exchange.Status = PayerToPayerOutboundStatus.DataReceived;
        await _store.SaveAsync(exchange, ct);

        // 9. Durable ingestion. The exchange completes only if this commits.
        exchange.Status = PayerToPayerOutboundStatus.Ingesting;
        exchange.IngestionStatus = PayerToPayerIngestionStatus.Staging;
        exchange.IngestionStartedAtUtc = DateTime.UtcNow;
        await _store.SaveAsync(exchange, ct);

        var ingestion = await _ingestion.IngestAsync(new PayerToPayerIngestionContext
        {
            // Every binding comes from the exchange CHO drove — never from the
            // peer's Bundle.
            TenantId = exchange.TenantId,
            MemberId = exchange.MemberId,
            SourcePayerId = endpoint.PayerId,
            SourceEndpointKey = endpoint.EndpointKey,
            ExchangeId = exchange.ExchangeId,
            RemoteMemberId = matched.RemoteMemberId!,
            ReceivedAtUtc = provenance.ReceivedAtUtc,
        }, package, ct);

        ApplyIngestion(exchange, ingestion);

        if (!ingestion.Succeeded)
        {
            // The member's record is unchanged: nothing staged under an
            // uncommitted ledger entry is visible. Retrying the initiation
            // resumes this same exchange.
            return await FailAsync(request, exchange, PayerToPayerOutboundStatus.Failed,
                PayerToPayerOutboundFailure.IngestionFailed, ct);
        }

        exchange.Status = PayerToPayerOutboundStatus.Completed;
        exchange.Failure = PayerToPayerOutboundFailure.None;
        await _store.SaveAsync(exchange, ct);

        var audit = Audit(request, exchange);
        _logger.LogInformation(
            "P2P outbound completed: tenant={Tenant} member={Member} targetPayer={Payer} peer={EndpointKey} "
            + "exchange={Exchange} resources={Count} persisted={Persisted} duplicate={Duplicate} "
            + "unsupported={Unsupported}",
            Clean(audit.TenantId), Clean(audit.MemberId), Clean(audit.TargetPayerId),
            Clean(audit.TargetEndpointKey), Clean(audit.ExchangeId), audit.ResourceCount,
            audit.PersistedResourceCount, audit.DuplicateResourceCount, audit.UnsupportedResourceCount);

        return new PayerToPayerOutboundResult { Exchange = exchange, Package = package, Audit = audit };
    }

    // ── Steps ───────────────────────────────────────────────────────────────────

    private async Task<ChoMember?> ResolveMemberAsync(string tenantId, string memberId, CancellationToken ct)
    {
        var candidates = await _memberSource.FindCandidatesAsync(
            tenantId, new PayerToPayerMemberCriteria { MemberId = memberId }, ct);
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// The member's coverage with the target payer, when CHO holds one. It gives
    /// the member's identifier under that payer — the strongest thing to match
    /// on. Several coverages with the same payer are narrowed by effective date
    /// using the SAME selector the inbound member-match uses (P2P-04); a genuine
    /// overlap refuses rather than asserts the wrong relationship. Holding NO
    /// coverage with the target payer is normal for a member transitioning in,
    /// and the exchange proceeds on demographics.
    /// </summary>
    private async Task<CoverageSelection> ResolveTargetCoverageAsync(
        string tenantId, string memberId, string targetPayerId,
        PayerToPayerOutboundRequest request, CancellationToken ct)
    {
        var coverages = await _coverageSource.GetCoveragesAsync(tenantId, memberId, ct);
        var normalizedTarget = MemberIdentityNormalizer.Identifier(targetPayerId);
        var withTargetPayer = coverages
            .Where(c => string.Equals(
                MemberIdentityNormalizer.Identifier(c.PayerId), normalizedTarget, StringComparison.Ordinal))
            .ToList();

        if (withTargetPayer.Count == 0) return CoverageSelection.NoCoverage;
        if (withTargetPayer.Count == 1) return CoverageSelection.Select(withTargetPayer[0]);

        return PayerToPayerCoverageSelector.Select(withTargetPayer, new MemberMatchCriteria
        {
            AsOfDate = MemberIdentityNormalizer.BirthDate(request.AsOfDate)
                       ?? request.ExchangeDateUtc.ToString("yyyy-MM-dd"),
        });
    }

    private sealed record MatchStep(string? RemoteMemberId, (PayerToPayerOutboundStatus Status, PayerToPayerOutboundFailure Failure)? Failure);

    private async Task<MatchStep> MatchWithRemoteAsync(
        PayerToPayerOutboundRequest request, PayerToPayerOutboundExchange exchange,
        PayerToPayerEndpoint endpoint, ChoMember member, string? priorPayerMemberId, CancellationToken ct)
    {
        // A payer that does not require $member-match can be called directly with
        // the identifier CHO already holds for the member with that payer. Without
        // such an identifier the match is required regardless — CHO will not guess
        // which member the peer means.
        if (!endpoint.RequiresMemberMatch && !string.IsNullOrWhiteSpace(priorPayerMemberId))
        {
            exchange.MemberMatchOutcome = "Skipped";
            return new MatchStep(priorPayerMemberId.Trim(), null);
        }

        exchange.Status = PayerToPayerOutboundStatus.Matching;
        await _store.SaveAsync(exchange, ct);

        // Only the attributes the operation needs: the member's identifier with
        // this payer (when known) plus family name and birth date. No SSN,
        // address, phone, or email is disclosed.
        var response = await _remote.MatchMemberAsync(endpoint, new RemoteMemberMatchRequest
        {
            ReceivingPayerId = _directory.Value.LocalPayerId,
            MemberId = priorPayerMemberId,
            FamilyName = member.LastName,
            BirthDate = member.Dob,
            RequestedPayerId = endpoint.PayerId,
            AsOfDate = request.AsOfDate,
        }, ct);

        exchange.MemberMatchOutcome = response.Outcome.ToString();

        if (response.Outcome != RemoteCallOutcome.Success)
        {
            return new MatchStep(null, response.Outcome switch
            {
                RemoteCallOutcome.NoMatch =>
                    (PayerToPayerOutboundStatus.NoMatch, PayerToPayerOutboundFailure.MemberNoMatch),
                RemoteCallOutcome.Ambiguous =>
                    (PayerToPayerOutboundStatus.Ambiguous, PayerToPayerOutboundFailure.MemberAmbiguous),
                _ => (PayerToPayerOutboundStatus.Failed, MapTransportFailure(response.Outcome)),
            });
        }

        var reading = PayerToPayerResponseReader.ReadMatch(response.Payload);
        if (!reading.IsValid || string.IsNullOrWhiteSpace(reading.RemoteMemberId))
        {
            return new MatchStep(null,
                (PayerToPayerOutboundStatus.Failed, PayerToPayerOutboundFailure.InvalidRemoteResponse));
        }

        return new MatchStep(reading.RemoteMemberId, null);
    }

    // ── Outcomes ────────────────────────────────────────────────────────────────

    private static PayerToPayerOutboundFailure MapTransportFailure(RemoteCallOutcome outcome) => outcome switch
    {
        RemoteCallOutcome.Unauthorized => PayerToPayerOutboundFailure.RemoteUnauthorized,
        RemoteCallOutcome.Unavailable => PayerToPayerOutboundFailure.RemoteUnavailable,
        RemoteCallOutcome.NoMatch => PayerToPayerOutboundFailure.MemberNoMatch,
        RemoteCallOutcome.Ambiguous => PayerToPayerOutboundFailure.MemberAmbiguous,
        _ => PayerToPayerOutboundFailure.InvalidRemoteResponse,
    };

    private async Task<PayerToPayerOutboundResult> FailAsync(
        PayerToPayerOutboundRequest request, PayerToPayerOutboundExchange exchange,
        PayerToPayerOutboundStatus status, PayerToPayerOutboundFailure failure, CancellationToken ct)
    {
        exchange.Status = status;
        exchange.Failure = failure;
        await _store.SaveAsync(exchange, ct);

        var audit = Audit(request, exchange);
        _logger.LogInformation(
            "P2P outbound declined: tenant={Tenant} member={Member} targetPayer={Payer} exchange={Exchange} "
            + "status={Status} failure={Failure}",
            Clean(audit.TenantId), Clean(audit.MemberId), Clean(audit.TargetPayerId),
            Clean(audit.ExchangeId), audit.Outcome, audit.FailureCategory);

        return new PayerToPayerOutboundResult { Exchange = exchange, Audit = audit };
    }

    /// <summary>
    /// A refusal decided before any exchange is registered (wrong tenant, missing
    /// member/payer reference). Nothing is persisted under a tenant this instance
    /// does not serve; the attempt is still auditable.
    /// </summary>
    private PayerToPayerOutboundResult Unrecorded(
        PayerToPayerOutboundRequest request, string tenantId, string memberId, string targetPayerId,
        PayerToPayerOutboundStatus status, PayerToPayerOutboundFailure failure)
    {
        var exchange = new PayerToPayerOutboundExchange
        {
            ExchangeId = string.Empty,
            TenantId = tenantId,
            MemberId = memberId,
            TargetPayerId = targetPayerId,
            Status = status,
            Failure = failure,
        };

        var audit = Audit(request, exchange);
        _logger.LogInformation(
            "P2P outbound refused: tenant={Tenant} targetPayer={Payer} failure={Failure}",
            Clean(tenantId), Clean(targetPayerId), audit.FailureCategory);

        return new PayerToPayerOutboundResult { Exchange = exchange, Audit = audit };
    }

    private PayerToPayerOutboundResult Replay(
        PayerToPayerOutboundRequest request, PayerToPayerOutboundExchange exchange)
    {
        // A repeated initiation for the same transition returns the exchange that
        // already exists. The remote payer is not called again — the package is
        // not re-fetched; the caller sees the recorded outcome.
        var audit = Audit(request, exchange);
        _logger.LogInformation(
            "P2P outbound replay: tenant={Tenant} member={Member} exchange={Exchange} status={Status}",
            Clean(audit.TenantId), Clean(audit.MemberId), Clean(audit.ExchangeId), audit.Outcome);

        return new PayerToPayerOutboundResult { Exchange = exchange, Audit = audit, IsReplay = true };
    }

    /// <summary>A previously failed exchange may be retried under its own id.</summary>
    private static bool IsRetryable(PayerToPayerOutboundExchange exchange) =>
        exchange.Status is PayerToPayerOutboundStatus.Failed
            or PayerToPayerOutboundStatus.NotAuthorized
            or PayerToPayerOutboundStatus.NoMatch
            or PayerToPayerOutboundStatus.Ambiguous;

    private static void ResetForRetry(PayerToPayerOutboundExchange exchange)
    {
        exchange.Status = PayerToPayerOutboundStatus.Pending;
        exchange.Failure = PayerToPayerOutboundFailure.None;
        exchange.MemberMatchOutcome = null;
        exchange.ExportOutcome = null;
        exchange.RemoteMemberId = null;
        exchange.ReceivedResourceCount = 0;

        // Ingestion counters describe one attempt; a retry re-derives them. The
        // import keys the previous attempt staged are deterministic, so the retry
        // lands on the same rows rather than duplicating the member's history.
        exchange.IngestionStatus = PayerToPayerIngestionStatus.NotStarted;
        exchange.IngestionFailure = PayerToPayerIngestionFailure.None;
        exchange.PersistedResourceCount = 0;
        exchange.AdministrativeResourceCount = 0;
        exchange.DuplicateResourceCount = 0;
        exchange.UnsupportedResourceCount = 0;
        exchange.UnsupportedResourceTypes = Array.Empty<string>();
        exchange.IngestionStartedAtUtc = null;
        exchange.IngestionCompletedAtUtc = null;
    }

    /// <summary>Copies the ingestion outcome onto the exchange as structured state, not prose.</summary>
    private static void ApplyIngestion(
        PayerToPayerOutboundExchange exchange, PayerToPayerIngestionResult ingestion)
    {
        exchange.IngestionStatus = ingestion.Status;
        exchange.IngestionFailure = ingestion.Failure;
        exchange.PersistedResourceCount = ingestion.Counts.Persisted;
        exchange.AdministrativeResourceCount = ingestion.Counts.AdministrativeReference;
        exchange.DuplicateResourceCount = ingestion.Counts.Duplicate;
        exchange.UnsupportedResourceCount = ingestion.Counts.Unsupported;
        exchange.UnsupportedResourceTypes = ingestion.Counts.UnsupportedTypes;
        exchange.IngestionCompletedAtUtc = ingestion.CompletedAtUtc;
    }

    private static string IdempotencyKey(
        string tenantId, string memberId, string targetPayerId, string? transitionKey)
        => string.Join('|', tenantId, memberId, targetPayerId,
            string.IsNullOrWhiteSpace(transitionKey) ? "-" : transitionKey.Trim());

    private static PayerToPayerOutboundAuditEntry Audit(
        PayerToPayerOutboundRequest request, PayerToPayerOutboundExchange exchange) => new()
    {
        TenantId = exchange.TenantId,
        MemberId = exchange.MemberId,
        TargetPayerId = exchange.TargetPayerId,
        TargetEndpointKey = exchange.TargetEndpointKey,
        ExchangeId = exchange.ExchangeId,
        InitiatedBy = request.InitiatedBy,
        Outcome = exchange.Status.ToString(),
        FailureCategory = exchange.Failure.ToString(),
        ResourceCount = exchange.ReceivedResourceCount,
        IngestionStatus = exchange.IngestionStatus.ToString(),
        PersistedResourceCount = exchange.PersistedResourceCount,
        DuplicateResourceCount = exchange.DuplicateResourceCount,
        UnsupportedResourceCount = exchange.UnsupportedResourceCount,
    };

    /// <summary>
    /// Strips CR/LF from caller/config-derived values before they reach a log
    /// entry, preventing log-forging / injection (CWE-117).
    /// </summary>
    private static string Clean(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty, StringComparison.Ordinal)
                   .Replace("\n", string.Empty, StringComparison.Ordinal);
}
