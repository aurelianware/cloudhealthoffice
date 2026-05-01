using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ProviderService.Adapters;
using ProviderService.Controllers;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Controllers;

/// <summary>
/// Capability 5.6 — endpoint-shape coverage for
/// <c>GET /api/v1/networks/{id}/members/{npi}</c>. Drives the controller
/// layer with substituted services (the membership data path is covered
/// in <c>NetworkRosterServiceMembershipTests</c>); these tests verify
/// status codes, body shape, and tenant-scope guard semantics.
/// </summary>
public class NetworksControllerMembershipTests
{
    private const string TenantId = "tenant-a";
    private const string NetworkId = "net-aetna-ppo-fl-2025";
    private const string Npi = "1234567890";

    private readonly IOrganizationService _orgService = Substitute.For<IOrganizationService>();
    private readonly INetworkRosterService _rosterService = Substitute.For<INetworkRosterService>();
    private readonly NetworksController _controller;

    public NetworksControllerMembershipTests()
    {
        var adapterFactory = new OrganizationAdapterFactory(
            Array.Empty<IOrganizationAdapter>(),
            new ProviderTenantConfigCache(
                Substitute.For<IHttpClientFactory>(),
                Substitute.For<IConfiguration>(),
                NullLogger<ProviderTenantConfigCache>.Instance),
            NullLogger<OrganizationAdapterFactory>.Instance);

        _controller = new NetworksController(
            _orgService, adapterFactory, _rosterService,
            NullLogger<NetworksController>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = TenantId;
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };
    }

    [Fact]
    public async Task GetMember_returns_200_with_active_membership_when_in_window()
    {
        _orgService.GetByIdAsync(NetworkId).Returns(new Organization { OrganizationId = NetworkId });
        _rosterService.GetMembershipAsync(TenantId, NetworkId, Npi, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new NetworkMembershipResponse
            {
                NetworkId = NetworkId,
                Npi = Npi,
                ProviderId = "p-001",
                IsActiveMember = true,
                AsOfDate = DateTime.UtcNow,
                ParticipationStatus = "active",
            });

        var result = await _controller.GetMember(NetworkId, Npi, asOf: null, CancellationToken.None);
        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var body = ok!.Value as NetworkMembershipResponse;
        body!.IsActiveMember.Should().BeTrue();
        body.ProviderId.Should().Be("p-001");
    }

    [Fact]
    public async Task GetMember_returns_200_with_inactive_when_outside_window()
    {
        _orgService.GetByIdAsync(NetworkId).Returns(new Organization { OrganizationId = NetworkId });
        _rosterService.GetMembershipAsync(TenantId, NetworkId, Npi, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new NetworkMembershipResponse
            {
                NetworkId = NetworkId,
                Npi = Npi,
                ProviderId = "p-001",
                IsActiveMember = false,
                AsOfDate = DateTime.UtcNow,
                ParticipationStatus = "terminated",
            });

        var result = await _controller.GetMember(NetworkId, Npi, asOf: null, CancellationToken.None);
        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var body = ok!.Value as NetworkMembershipResponse;
        body!.IsActiveMember.Should().BeFalse();
        body.ParticipationStatus.Should().Be("terminated");
    }

    [Fact]
    public async Task GetMember_returns_404_when_network_not_in_tenant()
    {
        _orgService.GetByIdAsync(NetworkId).Returns((Organization?)null);

        var result = await _controller.GetMember(NetworkId, Npi, asOf: null, CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetMember_returns_404_when_npi_has_no_participation_in_network()
    {
        _orgService.GetByIdAsync(NetworkId).Returns(new Organization { OrganizationId = NetworkId });
        _rosterService.GetMembershipAsync(TenantId, NetworkId, Npi, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((NetworkMembershipResponse?)null);

        var result = await _controller.GetMember(NetworkId, Npi, asOf: null, CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetMember_returns_400_when_npi_blank()
    {
        var result = await _controller.GetMember(NetworkId, "  ", asOf: null, CancellationToken.None);
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetMember_passes_caller_supplied_asOf_to_service()
    {
        var asOf = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        _orgService.GetByIdAsync(NetworkId).Returns(new Organization { OrganizationId = NetworkId });
        _rosterService.GetMembershipAsync(TenantId, NetworkId, Npi, asOf, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembershipResponse
            {
                NetworkId = NetworkId, Npi = Npi, ProviderId = "p-001",
                IsActiveMember = true, AsOfDate = asOf, ParticipationStatus = "active",
            });

        await _controller.GetMember(NetworkId, Npi, asOf, CancellationToken.None);

        await _rosterService.Received(1)
            .GetMembershipAsync(TenantId, NetworkId, Npi, asOf, Arg.Any<CancellationToken>());
    }
}
