using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// Answers the Provider Access attribution question from the plan's own claims
/// history (CMS-0057-F PROV-02).
///
/// <para>
/// fhir-service owns the policy — which providers may ask, and how far back a
/// treatment relationship counts — and arrives here with it already resolved to
/// an absolute instant. This endpoint owns only the data: it reports whether
/// the tenant's claims show the provider treating the member on or after that
/// instant. Keeping the lookback out of claims-service means the window is
/// configured in one place rather than drifting between the two services.
/// </para>
///
/// <para>
/// The response is a boolean and nothing else. No claim, no date, no count: a
/// caller that may ask about a member does not thereby get to read that
/// member's history, and the yes/no is all the authorization decision consumes.
/// </para>
///
/// <para>
/// The one exception is <c>members</c>, the provider's panel for a bulk
/// <c>$export</c> (G-34): member ids only, and only for fhir-service's own
/// service identity, which decides every listed member again before it writes
/// anything.
/// </para>
///
/// <para>
/// Tenancy: the tenant comes from claims-service's tenant middleware and the
/// query filters on it. A missing tenant is a 400, never an unscoped read.
/// </para>
/// </summary>
[ApiController]
[Route("api/provider-attribution")]
public sealed class ProviderAttributionController : ControllerBase
{
    private readonly IProviderAttributionRepository _repository;
    private readonly ILogger<ProviderAttributionController> _logger;

    public ProviderAttributionController(
        IProviderAttributionRepository repository,
        ILogger<ProviderAttributionController> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/provider-attribution?provider={npi}&amp;member={memberId}&amp;since={utc}
    /// </summary>
    /// <param name="provider">The asking provider's NPI.</param>
    /// <param name="member">The member whose data would be shared.</param>
    /// <param name="since">
    /// The lookback floor, as a UTC instant. Required: a caller that omits it
    /// is asking a different question from the one the policy authorizes, and
    /// defaulting it here would silently widen attribution.
    /// </param>
    [HttpGet]
    public async Task<IActionResult> IsAttributed(
        [FromQuery] string? provider,
        [FromQuery] string? member,
        [FromQuery] DateTime? since,
        CancellationToken ct)
    {
        var tenantId = HttpContext.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
            return BadRequest(new { error = "Tenant context missing on request." });

        if (string.IsNullOrWhiteSpace(provider))
            return BadRequest(new { error = "provider is required." });
        if (string.IsNullOrWhiteSpace(member))
            return BadRequest(new { error = "member is required." });
        if (since is null)
            return BadRequest(new { error = "since is required." });

        bool attributed;
        try
        {
            attributed = await _repository.HasTreatedAsync(
                tenantId, provider, member, since.Value, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never answer false on a fault. False is indistinguishable from
            // "no such relationship" at the caller, which would turn an
            // unreadable database into a silent denial today and, if the
            // caller's default ever inverted, into a silent grant.
            _logger.LogError(
                ex,
                "Provider attribution lookup failed for tenant={Tenant}.",
                LogSanitizer.Sanitize(tenantId));
            return StatusCode(503, new { error = "Attribution source unavailable." });
        }

        return Ok(new ProviderAttributionResponse(attributed));
    }

    /// <summary>
    /// GET /api/provider-attribution/members?provider={npi}&amp;since={utc} —
    /// the members the provider treated on or after <paramref name="since"/>,
    /// for a bulk Provider Access export. fhir-service only: no user, and no
    /// other service, has a reason to list a provider's patients.
    /// </summary>
    [HttpGet("members")]
    [RequireServiceClient(FhirServiceClientId)]
    public async Task<IActionResult> ListMembers(
        [FromQuery] string? provider,
        [FromQuery] DateTime? since,
        CancellationToken ct)
    {
        var tenantId = HttpContext.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
            return BadRequest(new { error = "Tenant context missing on request." });

        if (string.IsNullOrWhiteSpace(provider))
            return BadRequest(new { error = "provider is required." });
        if (since is null)
            return BadRequest(new { error = "since is required." });

        IReadOnlyList<string> members;
        try
        {
            members = await _repository.ListTreatedMembersAsync(tenantId, provider, since.Value, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An empty list would read as "treated no one" and export a short
            // panel without saying so; the export has to fail instead.
            _logger.LogError(
                ex,
                "Provider panel lookup failed for tenant={Tenant}.",
                LogSanitizer.Sanitize(tenantId));
            return StatusCode(503, new { error = "Attribution source unavailable." });
        }

        return Ok(new ProviderPanelResponse(members));
    }

    /// <summary>The only service that may list a provider's panel.</summary>
    public const string FhirServiceClientId = "fhir-service";
}

/// <summary>A provider's claims-history panel: member ids, nothing else.</summary>
/// <param name="Members">The members, in ordinal order.</param>
public sealed record ProviderPanelResponse(IReadOnlyList<string> Members);

/// <summary>The whole answer: whether the relationship exists.</summary>
/// <param name="Attributed">True when claims history shows the treatment relationship.</param>
public sealed record ProviderAttributionResponse(bool Attributed);
