namespace ClaimsService.Services.Adjudication;

/// <summary>
/// Scoped tenant context for the adjudication pipeline. Background
/// Service Bus consumers create their own DI scope per message and have
/// no <see cref="Microsoft.AspNetCore.Http.HttpContext"/>; without an
/// ambient way to carry the tenant id, downstream HTTP clients cannot
/// set the <c>X-Tenant-ID</c> header that benefit-plan-service /
/// member-service / etc. require.
///
/// <para>
/// Lifetime is Scoped so each orchestrator run gets a fresh holder
/// inside its own scope; consumers (HTTP shims) read the same instance
/// within that scope.
/// </para>
/// </summary>
public interface IAdjudicationTenantContext
{
    /// <summary>The tenant id for the current adjudication run, or null if not set.</summary>
    string? TenantId { get; set; }
}

/// <summary>
/// Default in-memory holder used by the orchestrator scope. Not thread-
/// safe — one instance per scope, set once before stages run, read by
/// stage-side HTTP shims within the same scope.
/// </summary>
public sealed class AdjudicationTenantContext : IAdjudicationTenantContext
{
    public string? TenantId { get; set; }
}
