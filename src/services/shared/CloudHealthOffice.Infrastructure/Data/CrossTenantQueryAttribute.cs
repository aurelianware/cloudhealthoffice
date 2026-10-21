namespace CloudHealthOffice.Infrastructure.Data;

/// <summary>
/// Marks a method (or a whole type) whose MongoDB queries deliberately span
/// tenants: a background job that finds due work across all tenants and then
/// processes each record under that record's own <c>TenantId</c>, a migration,
/// or a lookup by a globally unique, server-generated key whose result is
/// checked against the caller's tenant before use.
///
/// <para>
/// Every other query on a collection whose documents carry a <c>TenantId</c>
/// must be scoped to a tenant. <c>tests/CloudHealthOffice.Architecture.Tests</c>
/// fails the build for any query that is neither scoped nor marked, so a
/// cross-tenant query is always an explicit, reviewed decision. Say why in
/// <paramref name="reason"/>.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class CrossTenantQueryAttribute : Attribute
{
    public CrossTenantQueryAttribute(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Reason = reason;
    }

    /// <summary>Why this query spans tenants and how the result stays tenant-safe.</summary>
    public string Reason { get; }
}
