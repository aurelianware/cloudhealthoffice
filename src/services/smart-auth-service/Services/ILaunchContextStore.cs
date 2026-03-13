using SmartAuthService.Models;

namespace SmartAuthService.Services;

/// <summary>
/// Ephemeral store for EHR launch contexts.
/// Sprint 2: in-memory (LaunchContextStore).
/// Sprint 3: replace with Redis or MongoDB-backed implementation for multi-pod deployments.
/// </summary>
public interface ILaunchContextStore
{
    /// <summary>
    /// Register a new launch context and return the opaque launch token.
    /// The token expires after SmartAuth:LaunchContextTtlMinutes (default 5 min).
    /// </summary>
    Task<string> RegisterAsync(RegisterLaunchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Retrieve and remove a launch context by its token (single-use).
    /// Returns null if the token is unknown or expired.
    /// </summary>
    Task<LaunchContext?> ConsumeAsync(string launchToken, CancellationToken ct = default);

    /// <summary>
    /// Peek at a launch context without consuming it (used for display during consent).
    /// Returns null if expired.
    /// </summary>
    Task<LaunchContext?> PeekAsync(string launchToken, CancellationToken ct = default);
}
