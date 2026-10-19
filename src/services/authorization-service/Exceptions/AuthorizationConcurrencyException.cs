using CloudHealthOffice.Infrastructure.Data;

namespace AuthorizationService.Exceptions;

/// <summary>
/// An authorization write based on a stale read: another write changed the
/// authorization (its <see cref="Models.Authorization.Revision"/>) since it was
/// read. Nothing was written; 409 Conflict at the API boundary. The caller
/// re-reads and retries.
/// </summary>
public sealed class AuthorizationConcurrencyException : ConcurrencyConflictException
{
    public AuthorizationConcurrencyException(string authorizationId, long expectedRevision)
        : base($"Authorization {authorizationId} was changed by another write (expected revision {expectedRevision}); re-read it and retry.")
    {
        AuthorizationId = authorizationId;
        ExpectedRevision = expectedRevision;
    }

    public string AuthorizationId { get; }
    public long ExpectedRevision { get; }
}
