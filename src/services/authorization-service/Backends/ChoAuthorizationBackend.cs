using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services.BenefitExclusion;

namespace AuthorizationService.Backends;

/// <summary>
/// Cloud Health Office-native authorization backend — the Replace-mode
/// system of record. A thin application layer over the existing
/// <see cref="IAuthorizationRepository"/> (Cosmos DB / MongoDB in production),
/// so Cloud Health Office itself owns the authorization record without
/// requiring QNXT, Facets, or HealthEdge.
///
/// This is production code, not a test double: the acceptance suite exercises
/// this exact class with an in-memory repository fixture, while the running
/// service binds the same class to the Cosmos/Mongo repository.
/// </summary>
public sealed class ChoAuthorizationBackend : IAuthorizationBackend
{
    public const string Key = "cho";

    private readonly IAuthorizationRepository _repository;
    private readonly IAuthorizationExclusionService? _exclusionService;

    /// <param name="repository">The authoritative CHO record store (Cosmos/Mongo in production).</param>
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

            AppendHistory(authorization, AuthorizationStatus.Denied, "A3", determination.ReasonText);
            return await _repository.CreateAsync(authorization);
        }

        // Record the opening lifecycle entry so history survives persistence.
        AppendHistory(authorization, authorization.Status, authorization.ReviewDecision, reason: null);
        return await _repository.CreateAsync(authorization);
    }

    public Task<Authorization?> GetByNumberAsync(string authorizationNumber, CancellationToken ct = default)
        => _repository.GetByAuthorizationNumberAsync(authorizationNumber);

    public async Task<Authorization> UpdateStatusAsync(
        Authorization authorization,
        AuthorizationStatus status,
        string? reviewDecision,
        string? reason,
        CancellationToken ct = default)
    {
        authorization.Status = status;
        if (!string.IsNullOrEmpty(reviewDecision))
            authorization.ReviewDecision = reviewDecision;
        if (status == AuthorizationStatus.Denied && !string.IsNullOrEmpty(reason))
            authorization.DenialReason = reason;
        authorization.LastUpdatedDate = DateTime.UtcNow;
        if (status is AuthorizationStatus.Approved or AuthorizationStatus.Denied or AuthorizationStatus.Modified)
            authorization.ReviewedDate ??= DateTime.UtcNow;

        AppendHistory(authorization, status, reviewDecision, reason);
        return await _repository.UpdateAsync(authorization);
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
            ReviewDecision = reviewDecision,
            Reason = reason,
            ChangedAt = DateTime.UtcNow,
            ChangedBy = authorization.LastUpdatedBy ?? authorization.CreatedBy,
        });
    }
}
