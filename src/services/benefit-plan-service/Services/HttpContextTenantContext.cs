using CloudHealthOffice.BenefitEngine.Services;

namespace BenefitPlanService.Services;

/// <summary>
/// Supplies the current tenant ID to the benefit engine by reading
/// from <c>HttpContext.Items["TenantId"]</c>, which is populated by
/// <see cref="BenefitPlanService.Middleware.TenantMiddleware"/>.
/// </summary>
public class HttpContextTenantContext : IBenefitEngineTenantContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextTenantContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string TenantId =>
        _httpContextAccessor.HttpContext?.Items["TenantId"] as string
        ?? throw new InvalidOperationException(
               "TenantId not found in HttpContext. Ensure TenantMiddleware runs before the benefit engine.");
}
