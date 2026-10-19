using CloudHealthOffice.Infrastructure.Data;

namespace AuthorizationService.Exceptions;

/// <summary>A different record already owns this number in the authenticated tenant (409).</summary>
public sealed class DuplicateAuthorizationNumberException(string number, Exception? inner = null)
    : ConcurrencyConflictException($"Authorization {number} already exists in this tenant.", inner);
