using AuthorizationService.Models;
using AuthorizationService.Repositories;

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

    public ChoAuthorizationBackend(IAuthorizationRepository repository)
    {
        _repository = repository;
    }

    public string BackendKey => Key;

    /// <summary>Replace mode: Cloud Health Office owns the authoritative record.</summary>
    public bool IsAuthoritative => true;

    public async Task<Authorization> CreateAsync(Authorization authorization, CancellationToken ct = default)
    {
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
