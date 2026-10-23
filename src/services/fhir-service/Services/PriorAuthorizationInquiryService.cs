using CloudHealthOffice.Infrastructure.Logging;
using FhirService.Services.Identity;

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

    /// <summary>
    /// The caller's provider NPI as a TRUSTED ISSUER asserted it, never as the
    /// body claimed it. Present only for an issuer that declared
    /// <c>ServesProviderCallers</c> and therefore configured the claim; absent
    /// otherwise, which is not "this caller has no NPI" but "no issuer CHO
    /// trusts has said what it is", so nothing may be authorized on it.
    /// </summary>
    public string? VerifiedProviderNpi { get; init; }

    /// <summary>
    /// The member a patient-context token is bound to, from the token. Unlike
    /// the provider claim this is populated by default
    /// (<c>IssuerClaimMappingOptions.PatientClaim</c> defaults to
    /// <c>"patient"</c>), so a binding on it reaches live callers.
    /// </summary>
    public string? VerifiedPatientId { get; init; }
}

/// <summary>
/// How hard the PAS caller binding fails (D-26).
///
/// <para>
/// The provider binding needs no switch: it applies only where a trusted issuer
/// asserted an NPI, which happens only where a deployment declared
/// <c>ServesProviderCallers</c>, so it cannot surprise anyone who did not ask
/// for it.
/// </para>
///
/// <para>
/// The PATIENT binding can, because its claim is configured by default. So it
/// ships observing: it records what it would have refused and refuses nothing,
/// until a deployment has seen its own traffic and turns
/// <see cref="EnforcePatientBinding"/> on. An authorization change whose blast
/// radius nobody measured is how a working integration breaks at 2am.
/// </para>
/// </summary>
public sealed class PasCallerBindingOptions
{
    public const string SectionName = "Pas:CallerBinding";

    /// <summary>
    /// Refuse, rather than merely record, an inquiry whose patient-context token
    /// is bound to a different member than the authorization.
    /// </summary>
    public bool EnforcePatientBinding { get; set; }
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
/// CALLER BINDING (D-26). The corroborating key above is a property of the
/// REQUEST, not of the caller: both halves of it are things the caller supplies,
/// and an NPI is public. Where CHO has a VERIFIED identity it is now used, and
/// it takes precedence over anything the body says:
///
/// <list type="bullet">
/// <item>a <b>patient-context</b> token is pinned to its own member. There is no
/// legitimate call in which a member asks after another member's prior
/// authorization. This binding reaches live callers, so it ships observing —
/// see <see cref="PasCallerBindingOptions"/>.</item>
/// <item>a <b>provider</b> caller is pinned to the NPI their trusted issuer
/// asserted. This applies only where a deployment declared
/// <c>ServesProviderCallers</c> on that issuer, so it tightens and never
/// loosens: where no issuer asserts an NPI, the corroborating key is still the
/// whole of the check and this is still a documented limitation.</item>
/// </list>
///
/// The verified identity is never INVENTED from a conventionally named claim —
/// that would turn a public number into an authorization decision. It comes only
/// from the claim a named, trusted issuer was configured to assert.
///
/// Provider Access consent is deliberately NOT applied: it governs a provider
/// reading a member's clinical record, not a submitter asking after its own
/// prior-authorization request.
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
    /// <param name="caller">
    /// The authenticated caller, resolved from the validated token. Optional so
    /// that a deployment or a test with no verified identity behaves exactly as
    /// before; where it is supplied, its claims BIND the inquiry (D-26).
    /// </param>
    PriorAuthorizationInquiryRequest FromInquiryClaim(
        Hl7.Fhir.Model.Claim claim, string tenantId, string? callerId,
        AuthenticatedCaller? caller = null);
}

/// <inheritdoc />
public sealed class PriorAuthorizationInquiryService : IPriorAuthorizationInquiryService
{
    private readonly IPriorAuthorizationStore _store;
    private readonly PasCallerBindingOptions _binding;
    private readonly ILogger<PriorAuthorizationInquiryService>? _logger;

    public PriorAuthorizationInquiryService(
        IPriorAuthorizationStore store,
        Microsoft.Extensions.Options.IOptions<PasCallerBindingOptions>? binding = null,
        ILogger<PriorAuthorizationInquiryService>? logger = null)
    {
        _store = store;
        // Defaulting to observe-only is the safe default in both directions: a
        // deployment that configures nothing refuses nothing new, and the
        // setting has to be turned on deliberately.
        _binding = binding?.Value ?? new PasCallerBindingOptions();
        _logger = logger;
    }

    /// <inheritdoc />
    public PriorAuthorizationInquiryRequest FromInquiryClaim(
        Hl7.Fhir.Model.Claim claim, string tenantId, string? callerId,
        AuthenticatedCaller? caller = null) => new()
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
        // From the TOKEN, and only ever from the token. Both are null unless a
        // trusted issuer was configured to assert them.
        VerifiedProviderNpi = Normalize(caller?.ProviderNpi),
        VerifiedPatientId = Normalize(caller?.PatientId),
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

        // VERIFIED IDENTITY FIRST (D-26). What a trusted issuer asserted beats
        // anything the body says, and a mismatch here is refused whether or not
        // the body's corroborating key happens to match.
        if (!VerifiedCallerMatches(request, record, authorizationNumber))
            return PriorAuthorizationInquiryResult.Refused(
                PriorAuthorizationInquiryOutcome.NotAuthorizedForCaller,
                authorizationNumber);

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
    /// The D-26 binding: where CHO has a verified identity for this caller, the
    /// record must belong to it.
    ///
    /// <para>
    /// Returns true when nothing is verified, which is the ONLY way this method
    /// loosens anything — and it is not a loosening, it is the pre-existing
    /// behaviour continuing where no issuer has asserted an identity.
    /// </para>
    ///
    /// <para>
    /// The patient half returns true while
    /// <see cref="PasCallerBindingOptions.EnforcePatientBinding"/> is off,
    /// recording what it would have refused. The log line names the
    /// authorization and the caller, never the two member ids, because a line
    /// saying "token for member A asked about member B" puts the pair of them in
    /// a log to answer a question the authorization number already answers.
    /// </para>
    /// </summary>
    private bool VerifiedCallerMatches(
        PriorAuthorizationInquiryRequest request,
        PriorAuthorizationRecord record,
        string? authorizationNumber)
    {
        if (!string.IsNullOrWhiteSpace(request.VerifiedProviderNpi)
            && !string.Equals(
                request.VerifiedProviderNpi.Trim(),
                record.RequestingProviderNpi?.Trim(),
                StringComparison.Ordinal))
        {
            _logger?.LogWarning(
                "PAS $inquire: the provider identity asserted by the caller's trusted issuer "
                + "does not match authorization {Authorization}.",
                LogSanitizer.Sanitize(authorizationNumber));
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.VerifiedPatientId)
            && !MemberMatches(request.VerifiedPatientId, record.MemberId)
            && !MemberMatches(request.VerifiedPatientId, record.PatientReference))
        {
            if (!_binding.EnforcePatientBinding)
            {
                _logger?.LogWarning(
                    "PAS $inquire: patient-context caller {Caller} is bound to a different member "
                    + "than authorization {Authorization}. Observing only — set "
                    + "Pas:CallerBinding:EnforcePatientBinding to refuse.",
                    LogSanitizer.Sanitize(request.CallerId),
                    LogSanitizer.Sanitize(authorizationNumber));
                return true;
            }

            _logger?.LogWarning(
                "PAS $inquire: refused patient-context caller {Caller} for authorization "
                + "{Authorization}: the token is bound to a different member.",
                LogSanitizer.Sanitize(request.CallerId),
                LogSanitizer.Sanitize(authorizationNumber));
            return false;
        }

        return true;
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
            if (!MemberMatches(request.MemberReference, record.MemberId)
                && !MemberMatches(request.MemberReference, record.PatientReference))
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
    /// PAS $submit records the member id (from the Bundle's Patient/Coverage)
    /// and, separately, the submitter's own patient reference
    /// (<c>Patient/pat-001</c>). An inquiry may name either, the reference with
    /// or without its <c>Patient/</c> prefix. Nothing else resolves.
    /// </summary>
    private static bool MemberMatches(string supplied, string? stored)
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
