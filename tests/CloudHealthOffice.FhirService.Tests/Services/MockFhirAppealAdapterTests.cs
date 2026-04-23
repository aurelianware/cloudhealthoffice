using CloudHealthOffice.Appeals.Contracts;
using FhirService.Services;
using FluentAssertions;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// Regression guard for tenant-scoping on the mock adapter seed.
/// Fix 1: GetAppealAsync and SearchAppealsAsync must never return records
/// that belong to a different tenant.
/// </summary>
public sealed class MockFhirAppealAdapterTests
{
    private static readonly MockFhirAppealAdapter Adapter = new();

    [Fact]
    public async Task GetAppealAsync_returns_null_for_cross_tenant_access()
    {
        // apl-001 belongs to "test-tenant"; accessing from "other-tenant" must return null.
        var result = await Adapter.GetAppealAsync("apl-001", tenantId: "other-tenant");
        result.Should().BeNull();
    }

    [Fact]
    public async Task SearchAppealsAsync_returns_empty_for_cross_tenant_access()
    {
        // "other-tenant" has apl-100; searching from "test-tenant" must not expose it.
        var (items, total) = await Adapter.SearchAppealsAsync(
            new AppealSearchQuery { PageSize = 100 }, tenantId: "test-tenant");

        items.Should().NotContain(a => a.TenantId == "other-tenant");
        items.Should().AllSatisfy(a => a.TenantId.Should().Be("test-tenant"));
    }

    [Fact]
    public async Task SearchAppealsAsync_scopes_to_correct_tenant()
    {
        // "other-tenant" has apl-100; querying from that tenant should see only its own records.
        var (items, _) = await Adapter.SearchAppealsAsync(
            new AppealSearchQuery { PageSize = 100 }, tenantId: "other-tenant");

        items.Should().NotContain(a => a.Id == "apl-001",
            because: "apl-001 belongs to test-tenant, not other-tenant");
        items.Should().OnlyContain(a => a.TenantId == "other-tenant");
    }
}
