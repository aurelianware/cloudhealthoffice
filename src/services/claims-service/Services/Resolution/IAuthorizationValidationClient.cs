namespace ClaimsService.Services.Resolution;

public interface IAuthorizationValidationClient
{
    Task<AuthorizationValidationResult?> ValidateAsync(
        string tenantId,
        string authorizationNumber,
        string? procedureCode,
        DateTime serviceDate,
        CancellationToken ct = default);
}

public sealed record AuthorizationValidationResult(
    string AuthorizationNumber,
    bool IsValid,
    string? Status,
    DateTime? ApprovedServiceDateFrom,
    DateTime? ApprovedServiceDateTo,
    DateTime? ExpirationDate,
    decimal? ApprovedUnits,
    string? ValidationMessage);
