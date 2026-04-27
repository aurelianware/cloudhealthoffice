namespace ProviderService.Models.CredentialingPayloads;

/// <summary>
/// FHIR-aligned reference to a supporting document. Phase 1 carries the
/// triple as opaque metadata on event payloads — there is no document
/// upload service in this PR. The shape maps cleanly to FHIR
/// DocumentReference for a future projection:
/// <list type="bullet">
///   <item><see cref="Uri"/> → <c>DocumentReference.content.attachment.url</c></item>
///   <item><see cref="DocumentType"/> → <c>DocumentReference.type.coding</c></item>
///   <item><see cref="Sha256"/> → <c>DocumentReference.content.attachment.hash</c></item>
/// </list>
/// <see cref="DocumentType"/> is intentionally an open string (not a
/// closed enum) so future credentialing artifact categories don't require
/// a code change — categorization vocabulary is a runtime concern.
///
/// <para>
/// The credentialing service does NOT validate <see cref="Uri"/>
/// reachability, does NOT fetch the document, and does NOT recompute
/// <see cref="Sha256"/>. URI is opaque audit metadata; validation is a
/// Phase 2 document-service responsibility.
/// </para>
/// </summary>
public sealed record DocumentReference(
    string Uri,
    string DocumentType,
    string? Sha256);

/// <summary>
/// Payload carried on
/// <see cref="CredentialingEventType.ApplicationSubmitted"/>. The event's
/// <see cref="CredentialingEvent.EventId"/> is the application identifier
/// referenced by every downstream event in the chain.
/// </summary>
public sealed record ApplicationSubmittedPayload(
    DateTimeOffset SubmittedAt,
    string ApplicationSource,
    IReadOnlyList<DocumentReference>? SupportingDocuments,
    bool SynthesizedForDelegatedAuthority = false);

/// <summary>
/// Payload carried on
/// <see cref="CredentialingEventType.PrimarySourceVerificationCompleted"/>.
/// </summary>
public sealed record PrimarySourceVerificationPayload(
    DateTimeOffset VerifiedAt,
    string VerificationVendor,
    IReadOnlyList<string> VerifiedItems,
    IReadOnlyList<DocumentReference>? Evidence);

/// <summary>
/// Payload carried on
/// <see cref="CredentialingEventType.CommitteeReviewScheduled"/>.
/// </summary>
public sealed record CommitteeReviewScheduledPayload(
    DateTimeOffset ScheduledFor,
    string CommitteeId,
    string? AgendaReference);

/// <summary>
/// Payload carried on <see cref="CredentialingEventType.DecisionRecorded"/>.
/// The four authority fields
/// (<see cref="DecisionAuthorityType"/>,
/// <see cref="DecisionAuthorityId"/>, <see cref="CommitteeMembers"/>,
/// <see cref="DecisionMinuteReference"/>) are what makes the chain
/// credentialing-grade audit-quality.
/// <see cref="CommitteeMembers"/> and <see cref="DecisionMinuteReference"/>
/// are nullable — only the
/// <see cref="ProviderService.Models.DecisionAuthorityType.CredentialingCommittee"/>
/// path requires both.
/// </summary>
public sealed record DecisionRecordedPayload(
    CredentialingDecision Decision,
    DateTimeOffset DecidedAt,
    DateTime? CredentialingDate,
    DateTime? RecredentialingDueDate,
    DecisionAuthorityType DecisionAuthorityType,
    string DecisionAuthorityId,
    IReadOnlyList<string>? CommitteeMembers,
    string? DecisionMinuteReference,
    string? DenialReason);

/// <summary>
/// Payload carried on
/// <see cref="CredentialingEventType.RecredentialingTriggered"/>. Opens a
/// new chain that will be followed by
/// <see cref="CredentialingEventType.ApplicationSubmitted"/>; the projector
/// flips status to <see cref="CredentialingStatus.Pending"/> on the trigger
/// alone so consumers see the re-cred state immediately.
/// </summary>
public sealed record RecredentialingTriggeredPayload(
    DateTimeOffset TriggeredAt,
    string Reason);

/// <summary>
/// Payload carried on
/// <see cref="CredentialingEventType.ApplicationWithdrawn"/>. Terminates
/// the open chain; the projector reverts to the predecessor decision
/// (or to <see cref="CredentialingStatus.Unknown"/> when no predecessor
/// exists).
/// </summary>
public sealed record ApplicationWithdrawnPayload(
    DateTimeOffset WithdrawnAt,
    string Reason);
