using CloudHealthOffice.Infrastructure.Data;

namespace TenantService.Services;

/// <summary>
/// A tenant save based on a stale read: another write (an admin edit, a Stripe
/// webhook, onboarding activation) moved its <see cref="Models.Tenant.Revision"/>
/// on since it was read. Nothing was written; 409 Conflict. The caller re-reads
/// and retries.
/// </summary>
public sealed class TenantChangedConcurrentlyException : ConcurrencyConflictException
{
    public TenantChangedConcurrentlyException(string tenantId, long expectedRevision)
        : base($"Tenant {tenantId} changed while it was being edited (expected revision {expectedRevision}). Reload it and try again.")
    {
        TenantId = tenantId;
        ExpectedRevision = expectedRevision;
    }

    public string TenantId { get; }
    public long ExpectedRevision { get; }
}
