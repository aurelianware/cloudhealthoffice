namespace ClaimsService.Services.Resolution;

public interface IAuthorizationValidationClient
{
    /// <summary>
    /// Validates <paramref name="authorizationNumber"/> for
    /// <paramref name="tenantId"/>. Never returns null and never throws for
    /// a failed lookup: anything that is not a definitive answer from
    /// authorization-service is <see cref="AuthorizationLookupStatus.Unavailable"/>
    /// (D-04, owner decision Q-9 — a failed lookup never pays or denies).
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
        CancellationToken ct = default,
        int? authorizationLineNumber = null);
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
    string? ValidationCode = null);
