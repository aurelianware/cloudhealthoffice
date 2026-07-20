namespace ClaimsService.Services.Resolution;

public interface IAuthorizationValidationClient
{
    Task<AuthorizationValidationResult?> ValidateAsync(
        string tenantId,
        string authorizationNumber,
        string? procedureCode,
        DateTime serviceDate,
        string? providerNpi,
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
