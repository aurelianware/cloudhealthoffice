namespace TenantService.Services;

/// <summary>
/// The stored user changed in a way that makes this write stale (for example,
/// its invitation was redeemed after it was read). The caller should reload.
/// </summary>
public sealed class UserChangedConcurrentlyException(string userId)
    : InvalidOperationException($"User {userId} changed while it was being edited. Reload it and try again.");
