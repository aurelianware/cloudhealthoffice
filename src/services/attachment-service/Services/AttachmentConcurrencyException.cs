using CloudHealthOffice.Infrastructure.Data;

namespace AttachmentService.Services;

/// <summary>
/// An attachment write based on a stale read: another write changed the
/// attachment (its <see cref="Models.Attachment.Revision"/>) since it was read.
/// Nothing was written; 409 Conflict at the API boundary. The caller re-reads
/// and retries.
/// </summary>
public sealed class AttachmentConcurrencyException : ConcurrencyConflictException
{
    public AttachmentConcurrencyException(string id, long expectedRevision)
        : base($"Attachment {id} was changed by another write (expected revision {expectedRevision}); re-read it and retry.")
    {
        AttachmentId = id;
        ExpectedRevision = expectedRevision;
    }

    public string AttachmentId { get; }
    public long ExpectedRevision { get; }
}
