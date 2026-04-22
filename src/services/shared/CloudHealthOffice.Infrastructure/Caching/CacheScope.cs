namespace CloudHealthOffice.Infrastructure.Caching;

/// <summary>
/// How a cache key should be scoped by <c>CacheKeyGuard</c>.
///
/// Default (<see cref="Tenant"/>): the guard requires an ambient tenant on
/// <c>IHttpContextAccessor.HttpContext.Items["TenantId"]</c> and prepends it
/// so no two tenants collide. This is the right choice for every cache that
/// stores tenant-scoped data.
///
/// <see cref="Global"/> is an explicit opt-out — rare, for platform-wide
/// configuration with no per-tenant variance (feature flags, terminology
/// tables). Must be passed deliberately at the call site; the guard will not
/// infer it from a missing tenant context.
/// </summary>
public enum CacheScope
{
    Tenant = 0,
    Global = 1
}
