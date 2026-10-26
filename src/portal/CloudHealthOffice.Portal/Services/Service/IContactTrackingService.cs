namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Contacts from contact-service: the record of a call, chat or walk-in.
/// The service checks <c>service:read</c> on reads and
/// <c>service:contact</c> on writes.
/// </summary>
public interface IContactTrackingService
{
    Task<ContactRecord?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>The signed-in rep's own recent contacts, newest first.</summary>
    Task<List<ContactListItem>> GetMineAsync(bool openOnly = false, int limit = 50, CancellationToken ct = default);

    Task<List<ContactListItem>> GetByMemberAsync(string memberId, int limit = 50, CancellationToken ct = default);

    Task<List<ContactListItem>> GetByProviderAsync(string npi, int limit = 50, CancellationToken ct = default);

    /// <summary>Who called about this claim, authorization or appeal.</summary>
    Task<List<ContactListItem>> GetBySubjectAsync(
        ContactSubjectType type, string subjectId, int limit = 50, CancellationToken ct = default);

    Task<ContactRecord?> StartAsync(StartContactRequest request, CancellationToken ct = default);

    Task<ContactRecord?> UpdateAsync(string id, UpdateContactRequest request, CancellationToken ct = default);

    Task<ContactRecord?> LinkSubjectAsync(string id, LinkContactSubjectRequest request, CancellationToken ct = default);

    Task<ContactRecord?> CloseAsync(string id, CloseContactRequest request, CancellationToken ct = default);

    Task<ContactRecord?> ReopenAsync(string id, ReopenContactRequest request, CancellationToken ct = default);

    Task<ContactRecord?> OpenCaseAsync(string id, OpenContactCaseRequest request, CancellationToken ct = default);
    Task<ContactRecord?> CreateFollowUpAsync(string id, CreateContactFollowUpRequest request, CancellationToken ct = default);

    /// <summary>The reasons a rep can pick from.</summary>
    Task<List<ContactReasonOption>> GetReasonsAsync(bool activeOnly = true, CancellationToken ct = default);
}

/// <summary>
/// A write contact-service refused: someone else changed the contact first,
/// or the contact is closed. The message is the service's own, which is
/// written for the person reading it.
/// </summary>
public sealed class ContactWriteRefusedException(string message) : Exception(message);
