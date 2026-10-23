using ContactService.Models;

namespace ContactService.Repositories;

/// <summary>A stale write: the contact changed since it was read.</summary>
public sealed class ContactConcurrencyException(string contactId, long expectedRevision)
    : InvalidOperationException(
        $"Contact {contactId} was changed by another write (expected revision {expectedRevision}); re-read it and retry.")
{
    public string ContactId { get; } = contactId;
    public long ExpectedRevision { get; } = expectedRevision;
}

/// <summary>Two contacts in the same tenant cannot share a contact number.</summary>
public sealed class DuplicateContactNumberException(string contactNumber)
    : InvalidOperationException($"Contact number {contactNumber} already exists in this tenant.")
{
    public string ContactNumber { get; } = contactNumber;
}

/// <summary>Contacts, scoped to the caller's tenant on every read and write.</summary>
public interface IContactRepository
{
    Task<Contact?> GetByIdAsync(string id, CancellationToken ct = default);

    Task<Contact?> GetByContactNumberAsync(string contactNumber, CancellationToken ct = default);

    /// <summary>A member's contact history, newest first.</summary>
    Task<IReadOnlyList<Contact>> ListByMemberAsync(string memberId, int limit = 100, CancellationToken ct = default);

    /// <summary>A provider's contact history, newest first.</summary>
    Task<IReadOnlyList<Contact>> ListByProviderAsync(string npi, int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// Every contact that touched one subject — who called about this claim,
    /// and what was said.
    /// </summary>
    Task<IReadOnlyList<Contact>> ListBySubjectAsync(
        ContactSubjectType type, string subjectId, int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// One rep's contacts, newest first; their own recent work. Open ones
    /// only when <paramref name="openOnly"/>, which is how a rep finds the
    /// contact they left half-written.
    /// </summary>
    Task<IReadOnlyList<Contact>> ListByAgentAsync(
        string agentId, bool openOnly = false, int limit = 100, CancellationToken ct = default);

    Task<Contact> CreateAsync(Contact contact, CancellationToken ct = default);

    /// <summary>
    /// Replaces the contact, but only while its stored revision is the one
    /// that was read; otherwise <see cref="ContactConcurrencyException"/>.
    /// </summary>
    Task<Contact> UpdateAsync(Contact contact, CancellationToken ct = default);
}

/// <summary>The tenant's reason taxonomy.</summary>
public interface IContactReasonRepository
{
    /// <summary>Every reason, active first then by category and order.</summary>
    Task<IReadOnlyList<ContactReason>> ListAsync(bool activeOnly = false, CancellationToken ct = default);

    Task<ContactReason?> GetByCodeAsync(string code, CancellationToken ct = default);

    /// <summary>Creates the reason, or updates the one with the same code.</summary>
    Task<ContactReason> SaveAsync(ContactReason reason, CancellationToken ct = default);

    /// <summary>
    /// Writes the starter taxonomy for a tenant that has none, and returns
    /// how many reasons it added. A tenant that already has any reason at
    /// all is left alone: a plan that configured its own list, or deliberately
    /// pruned the starter one, does not want it back.
    /// </summary>
    Task<int> SeedStarterReasonsAsync(CancellationToken ct = default);
}
