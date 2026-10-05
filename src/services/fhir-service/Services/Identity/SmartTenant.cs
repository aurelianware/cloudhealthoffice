using System.Security.Claims;
using CloudHealthOffice.Infrastructure.Security;

namespace FhirService.Services.Identity;

/// <summary>
/// Where a SMART caller's tenant comes from. Only the validated token speaks;
/// a request header never selects the tenant of a SMART caller (it may only
/// echo it, which the shared TenantMiddleware checks).
///
/// In order:
/// <list type="number">
///   <item>The trusted issuer's mapped tenant claim. If the token also carries
///   a conventional <c>tenant_id</c> that disagrees, the token is refused.</item>
///   <item>The conventional <c>tenant_id</c> / <c>extension_TenantId</c>
///   claim.</item>
///   <item>When the trusted issuer is confined to exactly one tenant
///   (<c>Tenants</c> has one entry), that tenant. Configuration decides here,
///   not the caller.</item>
/// </list>
/// Otherwise there is no tenant and the request is refused with 401. Before,
/// the <c>X-Tenant-ID</c> header filled that gap, so a SMART caller whose
/// token named no tenant could choose any tenant.
/// </summary>
internal static class SmartTenant
{
    internal readonly record struct Result(string? Tenant, string? ClaimToAdd, bool Conflict);

    internal static Result Resolve(ClaimsPrincipal principal, AuthenticatedCaller? caller, TrustedIssuerRegistry registry)
    {
        var tokenTenant = principal.FindFirst(ChoClaimTypes.TenantId)?.Value
                          ?? principal.FindFirst("extension_TenantId")?.Value;

        if (caller == null)
            return new Result(tokenTenant, null, false);

        if (!string.IsNullOrEmpty(caller.TenantClaim))
        {
            if (!string.IsNullOrEmpty(tokenTenant)
                && !string.Equals(tokenTenant, caller.TenantClaim, StringComparison.Ordinal))
            {
                return new Result(null, null, Conflict: true);
            }

            return new Result(caller.TenantClaim,
                string.IsNullOrEmpty(tokenTenant) ? caller.TenantClaim : null, false);
        }

        if (!string.IsNullOrEmpty(tokenTenant))
            return new Result(tokenTenant, null, false);

        var issuer = registry.Resolve(caller.Issuer);
        if (issuer is { Tenants.Count: 1 })
            return new Result(issuer.Tenants[0], issuer.Tenants[0], false);

        return new Result(null, null, false);
    }
}
