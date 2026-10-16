using CloudHealthOffice.Infrastructure.Data;

namespace TenantService.Services;

/// <summary>
/// The stored user changed in a way that makes this write stale (for example,
/// its invitation was redeemed, or another write moved its
/// <see cref="Models.TenantUser.Revision"/> on, after it was read). Nothing was
/// written; 409 Conflict. The caller should reload.
/// </summary>
public sealed class UserChangedConcurrentlyException(string userId)
    : ConcurrencyConflictException($"User {userId} changed while it was being edited. Reload it and try again.");
