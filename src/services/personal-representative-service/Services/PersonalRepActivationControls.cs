using CloudHealthOffice.Infrastructure.Security;
using PersonalRepresentativeService.Models;

namespace PersonalRepresentativeService.Services;

/// <summary>
/// The answer to "may the current user activate this representative?".
/// A refusal carries the HTTP status and problem text the controller returns.
/// </summary>
public sealed record PersonalRepActivationDecision
{
    public bool Allowed { get; init; }
    public int StatusCode { get; init; }
    public string? Title { get; init; }
    public string? Detail { get; init; }

    /// <summary>The proof-of-authority document that was verified, when the credential type needs one.</summary>
    public string? VerifiedDocumentId { get; init; }

    /// <summary>The activator is the creator and the tenant's override allowed it.</summary>
    public bool SecondPersonOverridden { get; init; }

    public static PersonalRepActivationDecision Refuse(int status, string title, string detail)
        => new() { Allowed = false, StatusCode = status, Title = title, Detail = detail };
}

/// <summary>
/// Controls on activating a personal representative (45 CFR 164.502(g)), the
/// step that lets someone act for a member:
/// <list type="number">
///   <item>Only a user may activate: a service token is refused (403).</item>
///   <item>Second person: the activator must not be the user who created the
///   representative (403 "Separation of duties"). A tenant can turn this off in
///   tenant-service (<c>configuration.personalRepresentativeControls.requireSecondPerson = false</c>);
///   every same-user activation that allows is logged as an audit warning and
///   marked on the audit event.</item>
///   <item>Proof of authority: a legal guardian, conservator, healthcare power
///   of attorney or healthcare surrogate needs a document in
///   member-document-service, in the same tenant and linked to every member the
///   representative covers (every association that has not ended, including
///   future-dated ones). Parent and Other need none.</item>
/// </list>
/// Members are added only while a representative is a Draft (the controller
/// answers 409 otherwise), so these controls approve every member a
/// representative ever covers.
/// </summary>
public interface IPersonalRepActivationControls
{
    Task<PersonalRepActivationDecision> EvaluateAsync(
        PersonalRepresentative rep,
        string? requestedDocumentId,
        IReadOnlyCollection<string> associatedMemberIds,
        CancellationToken ct = default);
}

public sealed class PersonalRepActivationControls : IPersonalRepActivationControls
{
    /// <summary>Event id carried by every audit entry the second-person override writes.</summary>
    public static readonly EventId SecondPersonOverrideAuditEvent = new(4701, "PersonalRepSecondPersonOverride");

    public const string SeparationOfDutiesTitle = "Separation of duties";
    public const string ProofOfAuthorityTitle = "Proof of authority required";

    /// <summary>
    /// Credential types whose authority comes from a legal instrument, so
    /// activation needs that instrument on file.
    /// </summary>
    public static bool RequiresProofOfAuthority(PersonalRepCredentialType type) => type switch
    {
        PersonalRepCredentialType.LegalGuardian => true,
        PersonalRepCredentialType.Conservator => true, // court-appointed, like a guardian
        PersonalRepCredentialType.HealthcarePowerOfAttorney => true,
        PersonalRepCredentialType.HealthcareSurrogate => true,
        _ => false
    };

    private readonly IProofOfAuthorityDocuments _documents;
    private readonly ITenantPersonalRepControls _controls;
    private readonly ICurrentActor _actor;
    private readonly ILogger<PersonalRepActivationControls> _logger;

    public PersonalRepActivationControls(
        IProofOfAuthorityDocuments documents,
        ITenantPersonalRepControls controls,
        ICurrentActor actor,
        ILogger<PersonalRepActivationControls> logger)
    {
        _documents = documents;
        _controls = controls;
        _actor = actor;
        _logger = logger;
    }

    public async Task<PersonalRepActivationDecision> EvaluateAsync(
        PersonalRepresentative rep,
        string? requestedDocumentId,
        IReadOnlyCollection<string> associatedMemberIds,
        CancellationToken ct = default)
    {
        // ── Who is activating ──────────────────────────────────────────
        if (_actor.IsService)
        {
            return PersonalRepActivationDecision.Refuse(403, SeparationOfDutiesTitle,
                "A personal representative is activated by a person. Service tokens cannot activate one.");
        }

        var activator = _actor.UserId;
        var tenantId = _actor.TenantId;

        // A rep with no recorded creator (or the pre-authentication "System"
        // placeholder) cannot show that a second person is acting, so it is
        // treated as created by the activator.
        var creator = rep.CreatedBy;
        var sameUser = string.IsNullOrWhiteSpace(creator)
            || string.Equals(creator, "System", StringComparison.OrdinalIgnoreCase)
            || string.Equals(creator, activator, StringComparison.OrdinalIgnoreCase);

        var overridden = false;
        if (sameUser)
        {
            if (await _controls.IsSecondPersonRequiredAsync(tenantId, ct))
            {
                return PersonalRepActivationDecision.Refuse(403, SeparationOfDutiesTitle,
                    "Separation of duties: you established this personal representative, so you cannot activate it. " +
                    "Another user with members:write must review and activate it.");
            }
            overridden = true;
        }

        // ── Proof of authority ─────────────────────────────────────────
        string? verifiedDocumentId = null;
        if (RequiresProofOfAuthority(rep.CredentialType))
        {
            var documentId = string.IsNullOrWhiteSpace(requestedDocumentId)
                ? rep.ProofOfAuthorityDocumentId
                : requestedDocumentId.Trim();

            if (string.IsNullOrWhiteSpace(documentId))
            {
                return PersonalRepActivationDecision.Refuse(400, ProofOfAuthorityTitle,
                    $"A {rep.CredentialType} representative needs a proof-of-authority document. Upload it to " +
                    "member-document-service for the member and send its id as proofOfAuthorityDocumentId.");
            }

            if (associatedMemberIds.Count == 0)
            {
                return PersonalRepActivationDecision.Refuse(422, ProofOfAuthorityTitle,
                    "Associate the representative with the member before activating it, so the proof-of-authority " +
                    "document can be checked against that member.");
            }

            var check = await _documents.CheckAsync(tenantId, documentId, associatedMemberIds, ct);
            switch (check.Status)
            {
                case ProofOfAuthorityDocumentStatus.Verified:
                    verifiedDocumentId = documentId;
                    break;
                case ProofOfAuthorityDocumentStatus.Unavailable:
                    return PersonalRepActivationDecision.Refuse(503, "Proof of authority could not be checked",
                        "member-document-service could not confirm the proof-of-authority document. " +
                        "The representative was not activated; try again later.");
                case ProofOfAuthorityDocumentStatus.NotFound:
                    return PersonalRepActivationDecision.Refuse(422, ProofOfAuthorityTitle,
                        $"Proof-of-authority document {documentId} was not found in this tenant.");
                case ProofOfAuthorityDocumentStatus.WrongTenant:
                    return PersonalRepActivationDecision.Refuse(422, ProofOfAuthorityTitle,
                        $"Proof-of-authority document {documentId} does not belong to this tenant.");
                case ProofOfAuthorityDocumentStatus.NotFinalized:
                    return PersonalRepActivationDecision.Refuse(422, ProofOfAuthorityTitle,
                        $"Proof-of-authority document {documentId} has not finished uploading.");
                case ProofOfAuthorityDocumentStatus.NotLinkedToMember:
                    return PersonalRepActivationDecision.Refuse(422, ProofOfAuthorityTitle,
                        $"Proof-of-authority document {documentId} is not linked to member {check.UnlinkedMemberId}, " +
                        "whom this representative is associated with.");
                default:
                    return PersonalRepActivationDecision.Refuse(503, "Proof of authority could not be checked",
                        "The proof-of-authority document could not be confirmed.");
            }
        }

        if (overridden)
        {
            _logger.LogWarning(SecondPersonOverrideAuditEvent,
                "AUDIT separation-of-duties override: user {UserId} activated personal representative {PersonalRepId} " +
                "({CredentialType}) that they established, because tenant {TenantId} has " +
                "personalRepresentativeControls.requireSecondPerson = false",
                LogSanitizer.SafeForLog(activator), LogSanitizer.SafeForLog(rep.Id), rep.CredentialType,
                LogSanitizer.SafeForLog(tenantId));
        }

        return new PersonalRepActivationDecision
        {
            Allowed = true,
            StatusCode = 200,
            VerifiedDocumentId = verifiedDocumentId,
            SecondPersonOverridden = overridden
        };
    }
}
