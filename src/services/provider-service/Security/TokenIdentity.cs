using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace ProviderService.Security;

/// <summary>
/// The tenant and the acting user of a request, read only from the validated
/// CHO token (<c>UseChoAuthentication()</c> puts the token's tenant in
/// <c>HttpContext.Items["TenantId"]</c>). Headers, query strings and request
/// bodies never supply either one.
/// </summary>
internal static class TokenIdentity
{
    /// <summary>The token's tenant. A missing tenant is an error (401), never a default.</summary>
    public static string TokenTenantId(this ControllerBase controller)
        => controller.HttpContext.GetTenantId();

    /// <summary>The token subject. A request without one has no actor to record.</summary>
    public static string TokenActorId(this ControllerBase controller)
    {
        var sub = controller.User?.FindFirst(ChoClaimTypes.Subject)?.Value;
        if (string.IsNullOrEmpty(sub))
            throw new UnauthorizedAccessException("No authenticated actor on this request.");
        return sub;
    }
}
