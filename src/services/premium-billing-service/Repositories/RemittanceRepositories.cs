using PremiumBillingService.Models;

namespace PremiumBillingService.Repositories;

/// <summary>Received payments (820 / lockbox). Tenant from the request, as everywhere in this service.</summary>
public interface IRemittanceBatchRepository
{
    Task<RemittanceBatch?> GetByIdAsync(string id);

    /// <summary>Inserts the batch; false when a batch with its id (same payment) already exists.</summary>
    Task<bool> TryCreateAsync(RemittanceBatch batch);

    Task<RemittanceBatch> UpdateAsync(RemittanceBatch batch);

    Task<IEnumerable<RemittanceBatch>> SearchAsync(DateTime? receivedFrom = null, DateTime? receivedTo = null, int page = 1, int pageSize = 50);
}

/// <summary>The cash exceptions queue.</summary>
public interface IRemittanceExceptionRepository
{
    Task<RemittanceException?> GetByIdAsync(string id);
    Task<IEnumerable<RemittanceException>> ListAsync(RemittanceExceptionStatus? status = null, int page = 1, int pageSize = 50);
    Task<RemittanceException> CreateAsync(RemittanceException exception);
    Task<RemittanceException> UpdateAsync(RemittanceException exception);
}

/// <summary>Sponsor receivable accounts (open balance and unapplied credit).</summary>
public interface ISponsorAccountRepository
{
    Task<SponsorAccount?> GetAsync(string groupNumber);

    /// <summary>
    /// Applies <paramref name="change"/> to the group's account (created when
    /// missing) and saves it with optimistic concurrency, re-reading and
    /// re-applying on a conflict, so concurrent postings are not lost.
    /// </summary>
    Task<SponsorAccount> UpdateAsync(string groupNumber, Action<SponsorAccount> change);
}

/// <summary>Another writer changed the document first.</summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string message) : base(message) { }
}

internal static class RepositoryTenant
{
    public static string From(IHttpContextAccessor accessor)
    {
        var tenantId = accessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
            throw new InvalidOperationException("TenantId not found in request context");
        return tenantId;
    }

    /// <summary>Tries the change up to this many times against concurrent writers.</summary>
    public const int MaxConcurrencyAttempts = 10;
}
