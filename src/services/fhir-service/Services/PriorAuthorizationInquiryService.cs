namespace FhirService.Services;

/// <summary>
/// Why an inquiry did not return an authorization. Kept for AUDIT ONLY — the
/// caller sees one uniform refusal, because telling them "wrong tenant" or "that
/// provider isn't yours" apart from "no such authorization" is how an id space
/// gets enumerated.
/// </summary>
public enum PriorAuthorizationInquiryOutcome
{
    Found = 0,
    /// <summary>The inquiry Claim carried no usable authorization identifier.</summary>
    MissingIdentifier = 1,
    /// <summary>No corroborating key (member or provider) accompanied the identifier.</summary>
    MissingCorroboratingKey = 2,
    /// <summary>No authorization with that number exists.</summary>
    NotFound = 3,
    /// <summary>The record belongs to another tenant.</summary>
    TenantMismatch = 4,
    /// <summary>The corroborating key did not match the record.</summary>
    NotAuthorizedForCaller = 5,
}

/// <summary>What the inquiry resolved to, plus the audit category.</summary>
public sealed record PriorAuthorizationInquiryResult
{
    public required PriorAuthorizationInquiryOutcome Outcome { get; init; }
    public PriorAuthorizationRecord? Authorization { get; init; }

    /// <summary>The identifier that was asked about, for the audit line.</summary>
    public string? RequestedAuthorizationNumber { get; init; }

    public bool Found => Outcome == PriorAuthorizationInquiryOutcome.Found
                         && Authorization is not null;

    /// <summary>
    /// How this refusal should be reported. Only the two request-shape defects
    /// are safe to describe; everything else collapses to one answer.
    /// </summary>
    public PriorAuthorizationInquiryFailureKind FailureKind => Outcome switch
    {
        PriorAuthorizationInquiryOutcome.Found => PriorAuthorizationInquiryFailureKind.None,
        PriorAuthorizationInquiryOutcome.MissingIdentifier
            => PriorAuthorizationInquiryFailureKind.BadRequest,
        PriorAuthorizationInquiryOutcome.MissingCorroboratingKey
            => PriorAuthorizationInquiryFailureKind.BadRequest,
        _ => PriorAuthorizationInquiryFailureKind.Unavailable,
    };

    public static PriorAuthorizationInquiryResult Refused(
        PriorAuthorizationInquiryOutcome outcome, string? requested = null)
        => new() { Outcome = outcome, RequestedAuthorizationNumber = requested };
}

/// <summary>
/// The keys an inquiry may be made on, lifted from the inquiry Claim.
/// </summary>
public sealed record PriorAuthorizationInquiryRequest
{
    /// <summary>From the authenticated context — never from the request body.</summary>
    public required string TenantId { get; init; }

    /// <summary>The tracking identifier issued at submit (ClaimResponse.preAuthRef).</summary>
    public string? AuthorizationNumber { get; init; }

    /// <summary>Member the caller asserts the authorization is for.</summary>
    public string? MemberReference { get; init; }

    /// <summary>Requesting provider NPI the caller asserts.</summary>
    public string? RequestingProviderNpi { get; init; }

    /// <summary>
    /// The authenticated caller, for the audit trail. Not an authorization key:
    /// see the limitation note on <see cref="IPriorAuthorizationInquiryService"/>.
    /// </summary>
    public string? CallerId { get; init; }
}

/// <summary>
/// How a refusal should surface over HTTP. A defect in the REQUEST is the
/// caller's to fix and reveals nothing about what exists, so it is reported
/// plainly. A refusal about a RECORD is uniform, because distinguishing "no such
/// authorization" from "not yours" is what turns an inquiry into a probe.
/// </summary>
public enum PriorAuthorizationInquiryFailureKind
{
    None = 0,
    /// <summary>The request itself is incomplete — safe to say so (400).</summary>
    BadRequest = 1,
    /// <summary>Anything about a record. One uniform answer (404).</summary>
    Unavailable = 2,
}

/// <summary>
/// Resolves a prior-authorization inquiry against the authoritative record.
///
/// LOOKUP SEMANTICS. An authorization number alone is NOT sufficient. The
/// inquiry must also carry a corroborating key — the member the authorization is
/// for, or the requesting provider's NPI — and that key must match the stored
/// record. Numbers are structured (<c>PAS-yyyyMMdd-xxxxxxxx</c>) and therefore
/// guessable at the margins; requiring something the caller could only know if
/// the authorization is theirs turns a guess into a dead end. Tenant is taken
/// from the authenticated context and must match the record too.
///
/// READ ONLY. The service depends on <see cref="IPriorAuthorizationStore"/>,
/// which has no write method: an inquiry cannot create a record, move a status,
/// restart a decision clock, or cause a payer submission.
///
/// CALLER BINDING — a documented limitation. PAS is a system-to-system surface
/// here: <c>$submit</c> does not check that the caller IS the provider named in
/// the Claim, and there is no mapping in this repository from a token subject to
/// a provider NPI. So an inquiry is bound to its authorization by the
/// corroborating key, not by the caller's own identity: a caller who knows both
/// the authorization number AND a matching member or provider key can read it,
/// within their own tenant. Inventing a subject-to-NPI mapping to close that
/// would be security theatre — NPIs are public — so the caller is recorded in
/// the audit trail instead, and tightening this waits on a real provider
/// identity claim. Provider Access consent is deliberately NOT applied: it
/// governs a provider reading a member's clinical record, not a submitter asking
/// after its own prior-authorization request.
/// </summary>
public interface IPriorAuthorizationInquiryService
{
    Task<PriorAuthorizationInquiryResult> InquireAsync(
        PriorAuthorizationInquiryRequest request, CancellationToken ct = default);

    /// <summary>
    /// Lifts the lookup keys out of an inquiry Claim. Which elements carry which
    /// key is a property of the PAS request shape, so it belongs with the lookup
    /// rules rather than in the HTTP layer.
    /// </summary>
    PriorAuthorizationInquiryRequest FromInquiryClaim(
        Hl7.Fhir.Model.Claim claim, string tenantId, string? callerId);
}

/// <inheritdoc />
public sealed class PriorAuthorizationInquiryService : IPriorAuthorizationInquiryService
{
    private readonly IPriorAuthorizationStore _store;

    public PriorAuthorizationInquiryService(IPriorAuthorizationStore store)
        => _store = store;

    /// <inheritdoc />
    public PriorAuthorizationInquiryRequest FromInquiryClaim(
        Hl7.Fhir.Model.Claim claim, string tenantId, string? callerId) => new()
    {
        // Tenant is the authenticated context's. A tenant named anywhere in the
        // body is data, never authority.
        TenantId = tenantId,
        CallerId = callerId,
        // Trimmed on the way in: a padded identifier would otherwise miss the
        // lookup and be echoed untrimmed into the audit line.
        AuthorizationNumber = Normalize(ExtractAuthorizationNumber(claim)),
        MemberReference = Normalize(claim.Patient?.Reference),
        RequestingProviderNpi = Normalize(claim.Provider?.Identifier?.Value),
    };

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The authorization number: the Claim's own identifier, or the pre-auth
    /// reference some submitters echo on <c>Claim.insurance.preAuthRef</c>.
    /// </summary>
    private static string? ExtractAuthorizationNumber(Hl7.Fhir.Model.Claim claim)
    {
        var fromIdentifier = claim.Identifier
            ?.Select(i => i.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (!string.IsNullOrWhiteSpace(fromIdentifier))
            return fromIdentifier;

        return claim.Insurance
            ?.SelectMany(i => i.PreAuthRef ?? new List<string>())
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    public async Task<PriorAuthorizationInquiryResult> InquireAsync(
        PriorAuthorizationInquiryRequest request, CancellationToken ct = default)
    {
        var authorizationNumber = request.AuthorizationNumber?.Trim();
        if (string.IsNullOrWhiteSpace(authorizationNumber))
            return PriorAuthorizationInquiryResult.Refused(
                PriorAuthorizationInquiryOutcome.MissingIdentifier);

        var hasMember = !string.IsNullOrWhiteSpace(request.MemberReference);
        var hasProvider = !string.IsNullOrWhiteSpace(request.RequestingProviderNpi);
        if (!hasMember && !hasProvider)
            return PriorAuthorizationInquiryResult.Refused(
                PriorAuthorizationInquiryOutcome.MissingCorroboratingKey,
                authorizationNumber);

        var record = await _store.GetByAuthorizationNumberAsync(authorizationNumber, ct);
        if (record is null)
            return PriorAuthorizationInquiryResult.Refused(
                PriorAuthorizationInquiryOutcome.NotFound, authorizationNumber);

        // Tenant isolation, applied here rather than trusted from the lookup:
        // the read endpoint partitions on the propagated tenant header, and this
        // is the check that holds even if that propagation is ever lost.
        if (!string.Equals(record.TenantId, request.TenantId, StringComparison.Ordinal))
            return PriorAuthorizationInquiryResult.Refused(
                PriorAuthorizationInquiryOutcome.TenantMismatch, authorizationNumber);

        if (!CorroboratingKeyMatches(request, record))
            return PriorAuthorizationInquiryResult.Refused(
                PriorAuthorizationInquiryOutcome.NotAuthorizedForCaller,
                authorizationNumber);

        return new PriorAuthorizationInquiryResult
        {
            Outcome = PriorAuthorizationInquiryOutcome.Found,
            Authorization = record,
            RequestedAuthorizationNumber = authorizationNumber,
        };
    }

    /// <summary>
    /// At least one supplied key must match the record. A supplied key that does
    /// NOT match refuses even when another one does — a caller who names the
    /// wrong member for a real authorization is guessing, and guessing must not
    /// be rewarded with a different answer than a miss.
    /// </summary>
    private static bool CorroboratingKeyMatches(
        PriorAuthorizationInquiryRequest request, PriorAuthorizationRecord record)
    {
        var matched = false;

        if (!string.IsNullOrWhiteSpace(request.MemberReference))
        {
            if (!MemberMatches(request.MemberReference, record.MemberId))
                return false;
            matched = true;
        }

        if (!string.IsNullOrWhiteSpace(request.RequestingProviderNpi))
        {
            if (!string.Equals(
                    request.RequestingProviderNpi.Trim(),
                    record.RequestingProviderNpi?.Trim(),
                    StringComparison.Ordinal))
                return false;
            matched = true;
        }

        return matched;
    }

    /// <summary>
    /// The submit path records the member as the Claim's patient reference
    /// (<c>Patient/pat-001</c>), while a caller may inquire with either that form
    /// or the bare id. Both resolve; nothing else does.
    /// </summary>
    private static bool MemberMatches(string supplied, string stored)
    {
        var a = StripPatientPrefix(supplied.Trim());
        var b = StripPatientPrefix((stored ?? string.Empty).Trim());
        return a.Length > 0 && string.Equals(a, b, StringComparison.Ordinal);
    }

    private static string StripPatientPrefix(string value)
        => value.StartsWith("Patient/", StringComparison.Ordinal)
            ? value["Patient/".Length..]
            : value;
}
