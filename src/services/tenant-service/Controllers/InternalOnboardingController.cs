using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using TenantService.Security;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// The one platform step the <c>tenant-onboarding</c> Argo workflow performs:
/// activating the tenant it has just provisioned.
///
/// Changing a tenant's status otherwise needs <c>platform:tenants</c>, which no
/// service or workload token carries. This route instead admits exactly the
/// workflow's workload identity, <c>wf-tenant-onboarding</c>: a token
/// token-service issued for that workflow's Kubernetes service account (see
/// docs/security/argo-service-tokens.md). User tokens, service tokens and other
/// workloads get 403.
///
/// It is as narrow as the step: the tenant in the path must be the tenant the
/// token was issued for (the route tenant check applies), and the only change
/// it makes is <c>pending</c> → <c>active</c>. An active tenant is left as it
/// is; a suspended or terminated tenant is never reactivated (409). Every call
/// is written to the tenant audit log.
/// </summary>
[ApiController]
[Route("internal/v1/tenants")]
[RequireServiceClient(OnboardingClientId)]
public class InternalOnboardingController : ControllerBase
{
    /// <summary>The only identity allowed to call this route.</summary>
    public const string OnboardingClientId = "wf-tenant-onboarding";

    private const string Action = "onboarding-complete (activate pending tenant)";

    private readonly ITenantRepository _tenants;
    private readonly ICurrentActor _actor;
    private readonly TenantAuditLog _audit;

    public InternalOnboardingController(ITenantRepository tenants, ICurrentActor actor, TenantAuditLog audit)
    {
        _tenants = tenants;
        _actor = actor;
        _audit = audit;
    }

    /// <summary>
    /// 200 <c>{ tenantId, status: "active", changed }</c>; 404 if the tenant
    /// does not exist; 409 <c>tenant_not_pending</c> if it is neither pending
    /// nor already active.
    /// </summary>
    [HttpPost("{tenantId}/onboarding-complete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CompleteOnboarding(string tenantId)
    {
        var tenant = await _tenants.GetByTenantIdAsync(tenantId);
        if (tenant == null)
        {
            _audit.Record(Action, tenantId, "refused: not_found");
            return NotFound(new { error = "not_found" });
        }

        if (IsStatus(tenant.Status, "active"))
        {
            _audit.Record(Action, tenantId, "allowed: already_active");
            return Ok(new { tenantId, status = "active", changed = false });
        }

        if (IsStatus(tenant.Status, "pending"))
        {
            if (await _tenants.TryActivatePendingAsync(tenantId, _actor.UserId))
            {
                _audit.Record(Action, tenantId, "allowed: activated");
                return Ok(new { tenantId, status = "active", changed = true });
            }

            // The status changed between the read and the conditional write.
            tenant = await _tenants.GetByTenantIdAsync(tenantId);
            if (tenant != null && IsStatus(tenant.Status, "active"))
            {
                _audit.Record(Action, tenantId, "allowed: already_active");
                return Ok(new { tenantId, status = "active", changed = false });
            }
        }

        _audit.Record(Action, tenantId, $"refused: tenant_not_pending (status={TenantAuditLog.Sanitize(tenant?.Status)})");
        return Conflict(new { error = "tenant_not_pending", status = tenant?.Status });
    }

    private static bool IsStatus(string? status, string expected)
        => string.Equals(status, expected, StringComparison.Ordinal);
}
