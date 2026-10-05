using PersonalRepresentativeService.Models;
using PersonalRepresentativeService.Repositories;

namespace PersonalRepresentativeService.Tests.Fakes;

/// <summary>
/// Delegates to an <see cref="InMemoryPersonalRepRepository"/> and runs a hook
/// at a chosen point, to simulate another request committing in between
/// (an activation landing while a member is being added, or a member being
/// added while an activation is being checked).
/// </summary>
public sealed class HookedPersonalRepRepository : IPersonalRepRepository
{
    private readonly InMemoryPersonalRepRepository _inner;

    public HookedPersonalRepRepository(InMemoryPersonalRepRepository inner) => _inner = inner;

    /// <summary>Runs once, just before a status transition is written.</summary>
    public Func<Task>? BeforeTransition { get; set; }

    /// <summary>Runs once, just after an association pair is written.</summary>
    public Func<Task>? AfterAddAssociationPair { get; set; }

    public Task<PersonalRepresentative> CreateAsync(PersonalRepresentative rep, PersonalRepEvent genesisEvent, CancellationToken ct = default)
        => _inner.CreateAsync(rep, genesisEvent, ct);

    public Task<PersonalRepresentative?> GetByIdAsync(string tenantId, string repId, CancellationToken ct = default)
        => _inner.GetByIdAsync(tenantId, repId, ct);

    public Task<IReadOnlyList<PersonalRepresentative>> GetByIdsAsync(string tenantId, IReadOnlyList<string> repIds, CancellationToken ct = default)
        => _inner.GetByIdsAsync(tenantId, repIds, ct);

    public Task<IReadOnlyList<PersonalRepresentative>> ListByTenantAsync(string tenantId, bool activeOnly = false, DateTime? asOf = null, CancellationToken ct = default)
        => _inner.ListByTenantAsync(tenantId, activeOnly, asOf, ct);

    public async Task<PersonalRepresentative> TransitionStatusAsync(PersonalRepresentative rep, PersonalRepEvent auditEvent, CancellationToken ct = default)
    {
        var hook = BeforeTransition;
        BeforeTransition = null;
        if (hook != null) await hook();
        return await _inner.TransitionStatusAsync(rep, auditEvent, ct);
    }

    public Task<PersonalRepresentative?> TryTransitionToInactiveAsync(PersonalRepresentative rep, PersonalRepEvent auditEvent)
        => _inner.TryTransitionToInactiveAsync(rep, auditEvent);

    public async Task AddAssociationPairAsync(PersonalRepAssociation forward, PersonalRepAssociation inverse, PersonalRepEvent auditEvent, CancellationToken ct = default)
    {
        await _inner.AddAssociationPairAsync(forward, inverse, auditEvent, ct);
        var hook = AfterAddAssociationPair;
        AfterAddAssociationPair = null;
        if (hook != null) await hook();
    }

    public Task RemoveAssociationPairAsync(string tenantId, string pairId, string removedBy, PersonalRepEvent auditEvent, CancellationToken ct = default)
        => _inner.RemoveAssociationPairAsync(tenantId, pairId, removedBy, auditEvent, ct);

    public Task<IReadOnlyList<PersonalRepAssociation>> ListAssociationsForMemberAsync(string tenantId, string memberId, bool activeOnly = false, DateTime? asOf = null, CancellationToken ct = default)
        => _inner.ListAssociationsForMemberAsync(tenantId, memberId, activeOnly, asOf, ct);

    public Task<IReadOnlyList<PersonalRepAssociation>> ListAssociationsForRepAsync(string tenantId, string repId, bool activeOnly = false, DateTime? asOf = null, CancellationToken ct = default)
        => _inner.ListAssociationsForRepAsync(tenantId, repId, activeOnly, asOf, ct);

    public Task<PersonalRepAssociation?> FindActiveAssociationAsync(string tenantId, string repId, string memberId, CancellationToken ct = default)
        => _inner.FindActiveAssociationAsync(tenantId, repId, memberId, ct);
}
