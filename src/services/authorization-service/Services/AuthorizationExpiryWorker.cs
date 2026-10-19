using AuthorizationService.Repositories;
using CloudHealthOffice.Infrastructure.Logging;

namespace AuthorizationService.Services;

/// <summary>Expires approved authorizations after their inclusive UTC end date.</summary>
public sealed class AuthorizationExpiryWorker(
    IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<AuthorizationExpiryWorker> logger, TimeProvider? clock = null) : BackgroundService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly bool _unsupported = configuration.GetValue("MongoDb:UseTenantScoping", false);
    private IReadOnlyList<string>? _tenants;
    private DateTimeOffset _listedAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_unsupported)
        {
            logger.LogWarning("Authorization expiry disabled: tenant-database background enumeration is not supported (D-38)");
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await SweepAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError(ex, "Authorization expiry sweep failed"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        if (_unsupported) return 0;
        var now = _clock.GetUtcNow();
        if (_tenants is null || now - _listedAt >= TimeSpan.FromHours(1))
        {
            using var listing = scopes.CreateScope();
            _tenants = await listing.ServiceProvider.GetRequiredService<IAuthorizationRepository>().ListTenantIdsAsync(ct);
            _listedAt = now;
        }
        var expired = 0;
        foreach (var tenant in _tenants)
        {
            ct.ThrowIfCancellationRequested();
            using var scope = scopes.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAuthorizationRepository>();
            try
            {
                var candidates = await repository.FindExpiryCandidatesAsync(tenant, now.UtcDateTime.Date, 200, ct);
                foreach (var authorization in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    if (authorization.TenantId != tenant) continue;
                    try
                    {
                        if (await repository.ExpireIfStillEligibleAsync(tenant, authorization, now.UtcDateTime.Date, now.UtcDateTime, ct))
                            expired++;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { logger.LogError(ex, "Authorization expiry failed for record {Id}", LogSanitizer.Sanitize(authorization.Id)); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError(ex, "Authorization expiry failed for tenant {Tenant}", LogSanitizer.Sanitize(tenant)); }
        }
        return expired;
    }
}
