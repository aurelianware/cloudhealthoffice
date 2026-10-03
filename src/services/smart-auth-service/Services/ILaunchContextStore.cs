using SmartAuthService.Models;

namespace SmartAuthService.Services;

/// <summary>
/// EHR launch contexts: registered by an authenticated CHO caller of one
/// tenant, consumed once by the authorization endpoint for a provider user of
/// that tenant using that client.
/// </summary>
public interface ILaunchContextStore
{
    /// <summary>
    /// Register a new launch context for <paramref name="tenantId"/> (the
    /// registering caller's token tenant) and return the opaque launch token.
    /// The token expires after SmartAuth:LaunchContextTtlMinutes (default 5 min).
    /// </summary>
    Task<string> RegisterAsync(
        string tenantId, string registeredBy, RegisterLaunchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Atomically retrieve and remove the launch context, provided it is
    /// unexpired AND was registered in <paramref name="tenantId"/> for
    /// <paramref name="clientId"/>. Null otherwise. A launch presented for the
    /// wrong tenant or client is left untouched, so it can neither be used
    /// there nor be burned by someone who merely knows the token.
    /// </summary>
    Task<LaunchContext?> ConsumeAsync(
        string launchToken, string tenantId, string clientId, CancellationToken ct = default);
}
