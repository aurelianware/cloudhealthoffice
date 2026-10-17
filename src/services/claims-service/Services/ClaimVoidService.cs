using ClaimsService.Models;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Infrastructure.Logging;

namespace ClaimsService.Services;

/// <summary>
/// The void actions of the claims API — <c>POST /api/claims/{id}/void</c>,
/// <c>DELETE /api/claims/{id}</c> and <c>PUT /api/claims/{id}/status</c>
/// with <see cref="ClaimStatus.Voided"/>. Every one is the void transition
/// (<see cref="IClaimFinalizationService.VoidAsync"/>), never a bare status
/// write: it saves the claim's terminal accumulator reversal with the void
/// and emits <c>ClaimVersionVoided</c>. The caller maps the
/// <see cref="ClaimVoidResult"/> to its response.
/// </summary>
public interface IClaimVoidService
{
    /// <summary>
    /// <c>POST {id}/void</c>: voids with the caller's request (reason and
    /// optional ReversalRunId). <paramref name="request"/> is already
    /// validated (present, with a reason).
    /// </summary>
    Task<ClaimVoidResult> VoidClaimAsync(
        string id, ClaimVoidRequest request, Func<string> getTenantId, string? correlationId, CancellationToken ct);

    /// <summary><c>DELETE {id}</c>: a soft delete, which is a void.</summary>
    Task<ClaimVoidResult> DeleteClaimAsync(
        string id, Func<string> getTenantId, string? correlationId, CancellationToken ct);

    /// <summary>The void behind DELETE {id} and PUT {id}/status (Voided), with a fixed reason.</summary>
    Task<ClaimVoidResult> VoidWithReasonAsync(
        string id, string reason, Func<string> getTenantId, string? correlationId, CancellationToken ct);
}

/// <summary>Default <see cref="IClaimVoidService"/> (moved from <c>ClaimsController</c>).</summary>
public sealed class ClaimVoidService : IClaimVoidService
{
    private readonly IClaimFinalizationService _finalizationService;
    private readonly ICurrentActor _actor;
    private readonly ILogger _logger;

    public ClaimVoidService(
        IClaimFinalizationService finalizationService,
        ICurrentActor actor,
        ILogger<ClaimVoidService> logger)
        : this(finalizationService, actor, (ILogger)logger)
    {
    }

    /// <summary>
    /// For <c>ClaimsController</c>'s direct-construction constructor, which
    /// passes its own logger so the log output is unchanged.
    /// </summary>
    internal ClaimVoidService(IClaimFinalizationService finalizationService, ICurrentActor actor, ILogger logger)
    {
        _finalizationService = finalizationService;
        _actor = actor;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<ClaimVoidResult> VoidClaimAsync(
        string id, ClaimVoidRequest request, Func<string> getTenantId, string? correlationId, CancellationToken ct)
    {
        _logger.LogInformation(
            "Voiding claim {Id}; ReversalRun={ReversalRunId}",
            LogSanitizer.Sanitize(id), LogSanitizer.Sanitize(request.ReversalRunId));

        return _finalizationService.VoidAsync(
            id, request, getTenantId(), TryGetActorId(), correlationId, ct);
    }

    /// <inheritdoc />
    public Task<ClaimVoidResult> DeleteClaimAsync(
        string id, Func<string> getTenantId, string? correlationId, CancellationToken ct)
    {
        _logger.LogInformation("Voiding claim: {Id}", LogSanitizer.Sanitize(id));

        return VoidWithReasonAsync(id, "Voided via DELETE /api/claims/{id}", getTenantId, correlationId, ct);
    }

    /// <inheritdoc />
    public Task<ClaimVoidResult> VoidWithReasonAsync(
        string id, string reason, Func<string> getTenantId, string? correlationId, CancellationToken ct) =>
        _finalizationService.VoidAsync(
            id, new ClaimVoidRequest { Reason = reason }, getTenantId(), TryGetActorId(), correlationId, ct);

    // Identity.Name is the display name claim, and X-User-Id is caller-supplied;
    // the actor is the token subject.
    private string? TryGetActorId() => _actor.UserId;
}
