using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace IdCardService.Controllers;

[ApiController]
public abstract class TenantAwareControllerBase : ControllerBase
{
    /// <summary>
    /// The tenant from the caller's validated CHO token (set by the shared
    /// TenantMiddleware). Never from a header, query string, route or body; a
    /// request without one never reaches a CHO-token action.
    /// </summary>
    protected string TenantId => Actor.TenantId;

    /// <summary>The acting user (token subject). Every write records this.</summary>
    protected string ActorId => Actor.UserId;

    private ICurrentActor Actor => HttpContext.RequestServices.GetRequiredService<ICurrentActor>();
}
