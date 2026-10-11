using System.Net;
using ClaimsService.Models;
using ClaimsService.Services.Adjudication.Stages;
using CloudHealthOffice.Infrastructure.Security;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Security;

/// <summary>
/// #1299 S-B: <c>POST api/claims/admin/accumulators/prior-auth-unavailable-pends/rerun</c>
/// through the real pipeline. It needs <c>settings:manage</c> (claims
/// examiners and supervisors are refused) and works only on the token's
/// tenant.
/// </summary>
public class PriorAuthRerunEndpointTests : IClassFixture<ClaimsApiFactory>
{
    private const string Path = "/api/claims/admin/accumulators/prior-auth-unavailable-pends/rerun";
    private const string Tenant = "tenant-rerun";
    private readonly ClaimsApiFactory _factory;

    public PriorAuthRerunEndpointTests(ClaimsApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData(ChoRolePermissions.ClaimsExaminer)]
    [InlineData(ChoRolePermissions.ClaimsSupervisor)]
    public async Task WithoutSettingsManage_Returns403_AndRunsNothing(string role)
    {
        _factory.ClaimRepository.ClearReceivedCalls();
        using var client = _factory.CreateTenantClient(Tenant, "user-1", role);

        var response = await client.PostAsync(Path, null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _factory.ClaimRepository.DidNotReceiveWithAnyArgs().FindPendedByPendCodeAsync(default!, default, default);
    }

    [Fact]
    public async Task WithoutToken_Returns401()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync(Path, null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TenantAdmin_RunsTheRerun_InTheTokensTenant()
    {
        var claim = new Claim
        {
            Id = "claim-rerun-" + Guid.NewGuid().ToString("N")[..8],
            TenantId = Tenant,
            ClaimNumber = "CLM-RERUN",
            MemberId = "MEM-1",
            Status = ClaimStatus.Pended,
            PendDetails = new PendDetails
            {
                PendCode = BenefitCalculationStage.PriorAuthorizationUnavailablePendCode,
                PendReason = "PA lookup unavailable",
            },
        };
        _factory.ClaimRepository.FindPendedByPendCodeAsync(
                BenefitCalculationStage.PriorAuthorizationUnavailablePendCode, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { claim });
        _factory.ClaimRepository.FindPendedByPendCodeAsync(
                BenefitCalculationStage.PlanUnavailablePendCode, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Claim>());
        _factory.ClaimRepository.ClearReceivedCalls();
        using var client = _factory.CreateTenantClient(Tenant, "admin-1", ChoRolePermissions.TenantAdmin);

        var response = await client.PostAsync(Path + "?limit=10", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.ClaimRepository.Received().TryAcquireResolutionLockAsync(
            Tenant, claim.Id, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<DateTime>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }
}
