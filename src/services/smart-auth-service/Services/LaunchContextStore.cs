using System.Collections.Concurrent;
using SmartAuthService.Models;

namespace SmartAuthService.Services;

/// <summary>
/// Thread-safe in-memory launch context store.
/// A background cleanup pass removes expired entries every 60 seconds.
/// Sprint 3: replace with Redis or MongoDB for multi-pod deployments.
/// </summary>
public class LaunchContextStore : ILaunchContextStore, IHostedService, IDisposable
{
    private readonly ConcurrentDictionary<string, LaunchContext> _store = new();
    private readonly IConfiguration _config;
    private Timer? _cleanupTimer;

    public LaunchContextStore(IConfiguration config)
    {
        _config = config;
    }

    public Task<string> RegisterAsync(RegisterLaunchRequest request, CancellationToken ct = default)
    {
        var ttl = TimeSpan.FromMinutes(
            _config.GetValue<int>("SmartAuth:LaunchContextTtlMinutes", 5));

        var token = GenerateToken();
        var context = new LaunchContext
        {
            LaunchToken = token,
            PatientId = request.PatientId,
            EncounterId = request.EncounterId,
            PractitionerId = request.PractitionerId,
            ClientId = request.ClientId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(ttl)
        };

        _store[token] = context;
        return Task.FromResult(token);
    }

    public Task<LaunchContext?> ConsumeAsync(string launchToken, CancellationToken ct = default)
    {
        if (_store.TryRemove(launchToken, out var ctx) && ctx.ExpiresAt > DateTimeOffset.UtcNow)
            return Task.FromResult<LaunchContext?>(ctx);

        return Task.FromResult<LaunchContext?>(null);
    }

    public Task<LaunchContext?> PeekAsync(string launchToken, CancellationToken ct = default)
    {
        if (_store.TryGetValue(launchToken, out var ctx) && ctx.ExpiresAt > DateTimeOffset.UtcNow)
            return Task.FromResult<LaunchContext?>(ctx);

        return Task.FromResult<LaunchContext?>(null);
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer = new Timer(Cleanup, null, TimeSpan.Zero, TimeSpan.FromSeconds(60));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    private void Cleanup(object? state)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _store.Keys)
            if (_store.TryGetValue(key, out var ctx) && ctx.ExpiresAt <= now)
                _store.TryRemove(key, out _);
    }

    public void Dispose() => _cleanupTimer?.Dispose();

    private static string GenerateToken()
        => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
               .Replace("+", "-").Replace("/", "_").TrimEnd('=');
}
