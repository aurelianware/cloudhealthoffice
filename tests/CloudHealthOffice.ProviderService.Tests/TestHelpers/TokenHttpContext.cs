using System.Security.Claims;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;

namespace CloudHealthOffice.ProviderService.Tests.TestHelpers;

/// <summary>
/// An <see cref="HttpContext"/> as <c>UseChoAuthentication()</c> leaves it for
/// a controller: the token's tenant in <c>Items["TenantId"]</c> and the token
/// subject on <see cref="HttpContext.User"/>.
/// </summary>
public static class TokenHttpContext
{
    public const string DefaultUserId = "token-user";

    public static DefaultHttpContext For(string tenantId, string userId = DefaultUserId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = tenantId;
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ChoClaimTypes.Subject, userId), new Claim(ChoClaimTypes.TenantId, tenantId)],
            authenticationType: "test"));
        return ctx;
    }
}
