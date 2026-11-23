namespace ClaimsService.Services.Resolution;

public interface IAuthorizationValidationClient
{
    /// <summary>
    /// Validates <paramref name="authorizationNumber"/> for
    /// <paramref name="tenantId"/>. Never returns null and never throws for
    /// a failed lookup: anything that is not a definitive answer from
    /// authorization-service is <see cref="AuthorizationLookupStatus.Unavailable"/>
    /// (D-04, owner decision Q-9 — a failed lookup never pays or denies).
    /// The individual receiving services is <paramref name="memberId"/>; a
    /// subscriber or PAS Patient reference is not an identity fallback.
    /// Cancellation of <paramref name="ct"/> itself still throws.
    /// <paramref name="authorizationLineNumber"/> identifies a service on the
    /// authorization, never the claim's line ordinal. Ambiguous matching is
    /// Unavailable rather than a definitive clinical denial.
    /// </summary>
    Task<AuthorizationLookupResult> ValidateAsync(
        string tenantId,
        string authorizationNumber,
        string? procedureCode,
        DateTime serviceDate,
        string? providerNpi,
        string memberId,
        CancellationToken ct = default,
        int? authorizationLineNumber = null);
}

/// <summary>
/// Finds the plan's approved authorizations that cover a claim service the
/// claim named no authorization number for (authorization-service
/// <c>GET /api/authorizations/match</c>): same member, an approved period that
/// includes the service date, the procedure and the provider. Cores such as
/// QNXT and Facets attach authorizations to claims this way.
/// </summary>
public interface IAuthorizationMatchClient
{
    /// <summary>Never throws for a failed lookup: anything that is not the service's answer is <see cref="AuthorizationMatchStatus.Unavailable"/>.</summary>
    Task<AuthorizationMatchLookup> FindCoveringAsync(
        string tenantId,
        string memberId,
        DateTime serviceDate,
        string? procedureCode,
        string? providerNpi,
        CancellationToken ct = default);
}

public enum AuthorizationMatchStatus
{
    /// <summary>At least one approved authorization covers the service.</summary>
    Found,

    /// <summary>authorization-service answered that none does.</summary>
    None,

    /// <summary>No definitive answer.</summary>
    Unavailable,
}

/// <summary>One approved authorization that covers the service.</summary>
public sealed record AuthorizationMatchCandidate(
    string AuthorizationNumber,
    int? LineNumber = null,
    DateTime? ApprovedServiceDateFrom = null,
    DateTime? ApprovedServiceDateTo = null,
    string? CriteriaReference = null,
    IReadOnlyList<string>? MatchedOn = null);

/// <summary>Result of <see cref="IAuthorizationMatchClient.FindCoveringAsync"/>; candidates most specific first.</summary>
public sealed record AuthorizationMatchLookup(
    AuthorizationMatchStatus Status,
    IReadOnlyList<AuthorizationMatchCandidate> Candidates,
    string? Reason = null)
{
    public static AuthorizationMatchLookup Unavailable(string reason) =>
        new(AuthorizationMatchStatus.Unavailable, [], reason);

    public static AuthorizationMatchLookup From(IReadOnlyList<AuthorizationMatchCandidate> candidates) =>
        new(candidates.Count > 0 ? AuthorizationMatchStatus.Found : AuthorizationMatchStatus.None, candidates);
}

/// <summary>Outcome of one prior-authorization lookup.</summary>
public enum AuthorizationLookupStatus
{
    /// <summary>The authorization exists and is valid for this service.</summary>
    Valid,

    /// <summary>The authorization exists but is not valid for this service (not approved, service date outside the approved period, code or provider not covered).</summary>
    Invalid,

    /// <summary>authorization-service answered that the number does not exist for this tenant.</summary>
    NotFound,

    /// <summary>No definitive answer: transport error, timeout, 5xx, 401/403, unreadable body, or any other non-definitive response.</summary>
    Unavailable,
}

/// <summary>
/// Typed result of <see cref="IAuthorizationValidationClient.ValidateAsync"/>.
/// <see cref="Validation"/> is the service's body for
/// <see cref="AuthorizationLookupStatus.Valid"/> and
/// <see cref="AuthorizationLookupStatus.Invalid"/>; <see cref="Reason"/> is
/// the denial reason (Invalid) or what went wrong (Unavailable).
/// </summary>
public sealed record AuthorizationLookupResult(
    AuthorizationLookupStatus Status,
    AuthorizationValidationResult? Validation = null,
    string? Reason = null)
{
    public static AuthorizationLookupResult Valid(AuthorizationValidationResult validation) =>
        new(AuthorizationLookupStatus.Valid, validation);

    public static AuthorizationLookupResult Invalid(AuthorizationValidationResult validation, string reason) =>
        new(AuthorizationLookupStatus.Invalid, validation, reason);

    public static AuthorizationLookupResult NotFound() =>
        new(AuthorizationLookupStatus.NotFound);

    public static AuthorizationLookupResult Unavailable(string reason) =>
        new(AuthorizationLookupStatus.Unavailable, Reason: reason);
}

public sealed record AuthorizationValidationResult(
    string AuthorizationNumber,
    bool IsValid,
    string? Status,
    DateTime? ApprovedServiceDateFrom,
    DateTime? ApprovedServiceDateTo,
    DateTime? ExpirationDate,
    decimal? ApprovedUnits,
    string? ValidationMessage,
    string? ValidationCode = null,
    string? ValidatedMemberId = null,
    bool? MemberMatches = null,
    string? ApprovedUnitType = null,
    string? ApprovedUnitCode = null,
    string? ApprovedUnitSystem = null,
    string? CriteriaReference = null);
