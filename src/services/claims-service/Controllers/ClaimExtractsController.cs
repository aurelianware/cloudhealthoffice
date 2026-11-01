using System.ComponentModel.DataAnnotations;
using ClaimsService.Extracts;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// The tenant's claims warehouse extract (portal-reporting.md §6): turn it
/// on under a named data-use agreement, start a delivery, see past ones.
/// Tenant admins only (<c>extracts:manage</c>). The files land in the
/// tenant's drop under <c>/outbound/extracts/claims/{run}/</c>, where the
/// plan's SFTP login can download them.
/// </summary>
[ApiController]
[Route("api/claims/extracts/claims")]
[Produces("application/json")]
[RequirePermission(Permission)]
public sealed class ClaimExtractsController : ControllerBase
{
    public const string Permission = "extracts:manage";
    public const int MaxRuns = 100;

    private readonly IClaimExtractStore _store;
    private readonly IServiceScopeFactory _scopes;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ICurrentActor _actor;
    private readonly ILogger<ClaimExtractsController> _logger;
    private readonly TimeProvider _clock;

    public ClaimExtractsController(
        IClaimExtractStore store, IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, ICurrentActor actor,
        ILogger<ClaimExtractsController> logger, TimeProvider? clock = null)
    {
        _store = store;
        _scopes = scopes;
        _lifetime = lifetime;
        _actor = actor;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    [HttpGet]
    public async Task<ActionResult<ClaimExtractSettingsView>> Get(CancellationToken ct)
        => Ok(View(await _store.GetSettingsAsync(TenantId(), ct)));

    /// <summary>Turns the extract on or off. On needs the data-use agreement the files are delivered under.</summary>
    [HttpPut]
    public async Task<ActionResult<ClaimExtractSettingsView>> Put([FromBody] ClaimExtractSettingsRequest request, CancellationToken ct)
    {
        var agreement = string.IsNullOrWhiteSpace(request.DataUseAgreement) ? null : request.DataUseAgreement.Trim();
        if (request.Enabled && agreement is null)
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["dataUseAgreement"] = ["The extract carries PHI for every member: name the data-use agreement it is delivered under."],
            }));

        var saved = await _store.SaveSettingsAsync(TenantId(), request.Enabled, agreement, _actor.UserId, _clock.GetUtcNow().UtcDateTime, ct);
        return Ok(View(saved));
    }

    /// <summary>
    /// Starts a delivery now: what changed since the last one, or everything
    /// with <c>full=true</c> (a first load, or a warehouse rebuild). The run
    /// continues after the response; its record appears under <c>runs</c>.
    /// </summary>
    [HttpPost("runs")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Start([FromQuery] bool full = false, CancellationToken ct = default)
    {
        var tenantId = TenantId();
        var settings = await _store.GetSettingsAsync(tenantId, ct);
        if (settings is not { Enabled: true })
            return Conflict(new { error = "The claims extract is off for this tenant. Turn it on, with its data-use agreement, first." });
        if (settings.LeaseUntil is { } until && until >= _clock.GetUtcNow().UtcDateTime)
            return Conflict(new { error = "A claims extract is already running for this tenant." });

        var requestedBy = _actor.UserId;
        var stopping = _lifetime.ApplicationStopping;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ClaimExtractRunner>().RunAsync(tenantId, requestedBy, full, stopping);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Claims extract started by a user failed for tenant {TenantId}", LogSanitizer.Sanitize(tenantId));
            }
        }, CancellationToken.None);

        return Accepted(new { message = "Claims extract started.", full });
    }

    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<ClaimExtractRun>>> Runs([FromQuery] int limit = 20, CancellationToken ct = default)
        => Ok(await _store.ListRunsAsync(TenantId(), Math.Clamp(limit, 1, MaxRuns), ct));

    private string TenantId() => HttpContext.Items["TenantId"]?.ToString() is { Length: > 0 } tenant
        ? tenant
        : throw new InvalidOperationException("TenantId not found in HttpContext.");

    private ClaimExtractSettingsView View(ClaimExtractSettings? s) => new()
    {
        Enabled = s?.Enabled ?? false,
        DataUseAgreement = s?.DataUseAgreement,
        DeliveredThrough = s?.Watermark,
        LastSucceededAt = s?.LastSucceededAt,
        Running = s?.LeaseUntil is { } until && until >= _clock.GetUtcNow().UtcDateTime,
        Folder = $"/{ExtractPaths.Outbound}/{ExtractPaths.Extracts}/{ClaimExtractRunner.Dataset}/",
        UpdatedAt = s?.UpdatedAt,
        UpdatedBy = s?.UpdatedBy,
    };
}

public sealed class ClaimExtractSettingsRequest
{
    public bool Enabled { get; set; }

    /// <summary>The plan's reference for the agreement, e.g. its contract or DUA number.</summary>
    [StringLength(200)]
    [RegularExpression(@"^[^\x00-\x1F\x7F]*$", ErrorMessage = "dataUseAgreement: no control characters.")]
    public string? DataUseAgreement { get; set; }
}

public sealed class ClaimExtractSettingsView
{
    public bool Enabled { get; set; }
    public string? DataUseAgreement { get; set; }

    /// <summary>Every claim change up to this instant has been delivered.</summary>
    public DateTime? DeliveredThrough { get; set; }

    public DateTime? LastSucceededAt { get; set; }
    public bool Running { get; set; }

    /// <summary>Where the runs appear, as the plan's SFTP login sees its drop.</summary>
    public string Folder { get; set; } = string.Empty;

    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}
