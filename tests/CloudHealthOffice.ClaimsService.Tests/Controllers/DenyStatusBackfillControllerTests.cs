using System.Net;
using System.Net.Http.Json;
using ClaimsService.Models;
using CloudHealthOffice.Infrastructure.Security;
using NSubstitute;

namespace CloudHealthOffice.ClaimsService.Tests.Controllers;

/// <summary>The Deny backfill endpoint is opt-in: 503 and no repository call until enabled.</summary>
public class DenyStatusBackfillControllerTests : IClassFixture<ClaimsApiFactory>
{
    private readonly ClaimsApiFactory _factory;

    public DenyStatusBackfillControllerTests(ClaimsApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Disabled_Is503_AndTouchesNothing()
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("ops-1", ChoRolePermissions.TenantAdmin));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        _factory.ClaimRepository.ClearReceivedCalls();

        var response = await client.PostAsJsonAsync("/api/claims/admin/lifecycle/deny-backfill", new { dryRun = false });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("deny_backfill_disabled", await response.Content.ReadAsStringAsync());
        await _factory.ClaimRepository.DidNotReceiveWithAnyArgs().FindLegacyUnremittedDeniedAsync(default, default);
        await _factory.ClaimRepository.DidNotReceiveWithAnyArgs().TryReopenLegacyDeniedAsync(default!, default!, default);
    }
}
