using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services.BenefitExclusion;

namespace AuthorizationService.Backends;

/// <summary>
/// Cloud Health Office-native authorization backend — the Replace-mode
/// system of record. A thin application layer over the existing
/// <see cref="IAuthorizationRepository"/> (MongoDB in production),
/// so Cloud Health Office itself owns the authorization record without
/// requiring QNXT, Facets, or HealthEdge.
///
/// This is production code, not a test double: the acceptance suite exercises
/// this exact class with an in-memory repository fixture, while the running
/// service binds the same class to the Mongo repository.
/// </summary>
public sealed class ChoAuthorizationBackend : IAuthorizationBackend
{
    public const string Key = "cho";

    private readonly IAuthorizationRepository _repository;
    private readonly IAuthorizationExclusionService? _exclusionService;

    /// <param name="repository">The authoritative CHO record store (Mongo in production).</param>
    /// <param name="exclusionService">
    /// Optional benefit drug/service exclusion determination. When supplied
    /// (wired by DI in the running service, and by the acceptance suite), a
    /// submission for a drug/service the member's plan excludes is recorded as a
    /// denial rather than an approvable request. When absent, no plan exclusions
    /// are configured and every submission follows the ordinary path.
    /// </param>
    public ChoAuthorizationBackend(
        IAuthorizationRepository repository,
        IAuthorizationExclusionService? exclusionService = null)
    {
        _repository = repository;
        _exclusionService = exclusionService;
    }

    public string BackendKey => Key;

    /// <summary>Replace mode: Cloud Health Office owns the authoritative record.</summary>
    public bool IsAuthoritative => true;

    public async Task<Authorization> CreateAsync(Authorization authorization, CancellationToken ct = default)
    {
        // Benefit drug-exclusion enforcement (CMS-0057-F PAS-08): an explicit
        // plan exclusion takes precedence over the ordinary approvable path. The
        // request is recorded — auditably — as received and then denied
        // (278 A3) with a structured, coded reason. This runs before any
        // approval logic so an excluded drug can never be auto-approved by a
        // generic rule.
        var determination = _exclusionService?.DetermineExclusion(authorization);
        if (determination is { IsExcluded: true })
        {
            // Receipt entry, then the denial decision — an auditable trail.
            AppendHistory(authorization, authorization.Status, authorization.ReviewDecision, reason: null);

            authorization.Status = AuthorizationStatus.Denied;
            authorization.ReviewDecision = "A3"; // 278 UM06: Denied
            authorization.DenialReasonCode = determination.ReasonCode;
            authorization.DenialReason = determination.ReasonText;
            authorization.ReviewedDate ??= DateTime.UtcNow;
            authorization.LastUpdatedDate = DateTime.UtcNow;

            ApplyWholeRequestDecision(authorization);
            AppendHistory(authorization, AuthorizationStatus.Denied, "A3", determination.ReasonText);
            return await _repository.CreateAsync(authorization);
        }

        // Record the opening lifecycle entry so history survives persistence.
        AppendHistory(authorization, authorization.Status, authorization.ReviewDecision, reason: null);
        return await _repository.CreateAsync(authorization);
    }

    public async Task<Authorization> RecordAutomatedDecisionAsync(
        Authorization authorization, AutomatedDecision decision, CancellationToken ct = default)
    {
        var decidedAt = decision.DecidedAt ?? DateTime.UtcNow;

        // Receipt: the request as it arrived, at the time it arrived. The
        // decision clock runs from here (SubmittedDate), not from the write.
        authorization.StatusHistory = new List<AuthorizationStatusChange>
        {
            new()
            {
                Status = AuthorizationStatus.Submitted,
                ChangedAt = authorization.SubmittedDate,
                ChangedBy = authorization.CreatedBy,
            },
        };

        // A plan exclusion outranks any automated outcome (PAS-08), exactly as
        // it does for a REST submission. The caller reads the stored status
        // back and answers the provider with it.
        var determination = _exclusionService?.DetermineExclusion(authorization);
        if (determination is { IsExcluded: true })
        {
            authorization.Status = AuthorizationStatus.Denied;
            authorization.ReviewDecision = "A3";
            authorization.DenialReasonCode = determination.ReasonCode;
            authorization.DenialReason = determination.ReasonText;
            authorization.ReviewedDate = decidedAt;
            ApplyWholeRequestDecision(authorization);
            authorization.StatusHistory.Add(new AuthorizationStatusChange
            {
                Status = AuthorizationStatus.Denied,
                ReviewDecision = "A3",
                Reason = determination.ReasonText,
                ChangedAt = decidedAt,
                ChangedBy = authorization.CreatedBy,
                Source = "benefit-exclusion",
                LineDecisions = AuthorizationDecisionPolicy.Snapshot(authorization),
            });
            return await _repository.CreateAsync(authorization);
        }

        string? reason;
        switch (decision.Outcome)
        {
            case AutomatedDecisionOutcome.Approved:
                authorization.Status = AuthorizationStatus.Approved;
                authorization.ReviewDecision = "A1";
                authorization.ApprovedServiceDateFrom = decision.ApprovedServiceDateFrom;
                authorization.ApprovedServiceDateTo = decision.ApprovedServiceDateTo;
                authorization.ExpirationDate = decision.ApprovedServiceDateTo;
                authorization.ReviewedDate = decidedAt;
                reason = null;
                break;
            case AutomatedDecisionOutcome.Denied:
                authorization.Status = AuthorizationStatus.Denied;
                authorization.ReviewDecision = "A3";
                authorization.DenialReasonCode = decision.DenialReasonCode;
                authorization.DenialReason = decision.DenialReason;
                authorization.ReviewedDate = decidedAt;
                reason = decision.DenialReason;
                break;
            case AutomatedDecisionOutcome.Pended:
                authorization.Status = AuthorizationStatus.Pended;
                authorization.ReviewDecision = "A4";
                authorization.PendReason = decision.PendReason;
                reason = decision.PendReason;
                break;
            default:
                throw new ArgumentException("An automated decision needs an outcome.", nameof(decision));
        }

        ApplyWholeRequestDecision(authorization);
        authorization.StatusHistory.Add(new AuthorizationStatusChange
        {
            LineDecisions = AuthorizationDecisionPolicy.Snapshot(authorization),
            Status = authorization.Status,
            ReviewDecision = authorization.ReviewDecision,
            Reason = reason,
            ChangedAt = decidedAt,
            ChangedBy = authorization.CreatedBy,
            Source = AutomatedSource(decision),
        });

        return await _repository.CreateAsync(authorization);
    }

    /// <summary>
    /// The decision's history source, stating exactly the inputs the deciding
    /// path read and nothing else:
    /// <c>automated:&lt;rule&gt;;lob=&lt;line of business|n/a&gt;;state=&lt;state|none&gt;</c>.
    /// <c>lob=n/a</c>: no line of business was evaluated (provider exclusion,
    /// disabled, time budget, undetermined). <c>state=none</c>: no rule read a
    /// state, whether or not the request named one.
    /// </summary>
    internal static string AutomatedSource(AutomatedDecision decision)
    {
        var rule = string.IsNullOrWhiteSpace(decision.Rule) ? "automated" : $"automated:{decision.Rule}";
        var lob = decision.LineOfBusiness?.ToString() ?? "n/a";
        var state = string.IsNullOrEmpty(decision.StateCode) ? "none" : decision.StateCode;
        return $"{rule};lob={lob};state={state}";
    }

    public Task<Authorization?> GetByNumberAsync(string authorizationNumber, CancellationToken ct = default)
        => _repository.GetByAuthorizationNumberAsync(authorizationNumber);

    public async Task<Authorization> UpdateStatusAsync(
        Authorization authorization,
        AuthorizationStatus status,
        string? reviewDecision,
        string? reason,
        CancellationToken ct = default,
        AuthorizationDecisionDetails? details = null)
    {
        reviewDecision ??= AuthorizationDecisionPolicy.DecisionFor(status);
        details ??= new AuthorizationDecisionDetails
        {
            DenialReasonCode = authorization.DenialReasonCode,
            DenialReason = reason ?? authorization.DenialReason,
            PendReason = authorization.PendReason,
            ApprovedUnits = authorization.ApprovedUnits,
        };
        var lines = AuthorizationDecisionPolicy.Prepare(authorization, status, reviewDecision, details);
        AuthorizationDecisionPolicy.Apply(authorization, status, reviewDecision, details, lines);
        authorization.Status = status;
        authorization.ReviewDecision = reviewDecision;
        authorization.LastUpdatedDate = DateTime.UtcNow;
        if (status is AuthorizationStatus.Approved or AuthorizationStatus.Denied or AuthorizationStatus.Modified)
            authorization.ReviewedDate ??= DateTime.UtcNow;

        AppendHistory(authorization, status, reviewDecision, authorization.DenialReason ?? authorization.PendReason ?? reason);
        return await _repository.UpdateAsync(authorization);
    }

    private static void ApplyWholeRequestDecision(Authorization authorization)
    {
        var details = new AuthorizationDecisionDetails
        {
            DenialReasonCode = authorization.DenialReasonCode, DenialReason = authorization.DenialReason,
            PendReason = authorization.PendReason,
        };
        var lines = AuthorizationDecisionPolicy.Prepare(authorization, authorization.Status, authorization.ReviewDecision, details);
        AuthorizationDecisionPolicy.Apply(authorization, authorization.Status, authorization.ReviewDecision, details, lines);
    }

    private static void AppendHistory(
        Authorization authorization, AuthorizationStatus status, string? reviewDecision, string? reason)
    {
        // Guard against a null history (client sent "statusHistory": null, or an
        // older stored record predates the field).
        authorization.StatusHistory ??= new List<AuthorizationStatusChange>();
        authorization.StatusHistory.Add(new AuthorizationStatusChange
        {
            Status = status,
            LineDecisions = AuthorizationDecisionPolicy.Snapshot(authorization),
            ReviewDecision = reviewDecision,
            Reason = reason,
            ChangedAt = DateTime.UtcNow,
            ChangedBy = authorization.LastUpdatedBy ?? authorization.CreatedBy,
        });
    }
}
