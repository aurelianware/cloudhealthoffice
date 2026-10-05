using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Security;
using IdCardService.Models;
using IdCardService.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace CloudHealthOffice.IdCardService.Tests.Security;

/// <summary>
/// The real idcard-service pipeline with the orchestrator replaced. Every CHO
/// caller needs a CHO token; the tenant and the acting user come from it. ID
/// cards are PHI: reads need members:read, order and revoke need members:write.
/// </summary>
public class IdCardPipelineAuthTests : IClassFixture<IdCardPipelineAuthTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public IIdCardOrchestrator Orchestrator { get; } = Substitute.For<IIdCardOrchestrator>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDb:ConnectionString"] = "",
                ["CosmosDb:ConnectionString"] = "",
                ["ProviderJwt:Authority"] = "",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IIdCardOrchestrator>();
                services.AddSingleton(Orchestrator);
            });
        }
    }

    private const string Tenant = "tenant-1";
    private const string OtherTenant = "tenant-2";
    private const string User = "idcard-user-7";
    private readonly Factory _factory;

    public IdCardPipelineAuthTests(Factory factory)
    {
        _factory = factory;
        _factory.Orchestrator.ClearSubstitute();
        _factory.Orchestrator.ListForMemberAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<IdCardRecord>());
        _factory.Orchestrator.CreateOrderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CreateIdCardOrderRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => new IdCardOrder
            {
                TenantId = ci.ArgAt<string>(0),
                RequestedBy = ci.ArgAt<string>(1),
                MemberId = ci.ArgAt<CreateIdCardOrderRequest>(2).MemberId,
                Status = IdCardOrderStatus.Issued
            });
        _factory.Orchestrator.RevokeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RevokeIdCardRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => new IdCardRecord
            {
                TenantId = ci.ArgAt<string>(0),
                CardId = ci.ArgAt<string>(1),
                RevokedBy = ci.ArgAt<string>(2),
                RevokedAt = DateTime.UtcNow
            });
    }

    private HttpClient Client(string tenant, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(User, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private static object Order => new { memberId = "mem-1", channel = "Digital" };

    private static object Revocation => new { reason = "Lost" };

    private void NothingRan() => Assert.Empty(_factory.Orchestrator.ReceivedCalls());

    [Fact]
    public async Task NoToken_IsRejected()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/members/mem-1/id-cards");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        NothingRan();
    }

    [Fact]
    public async Task TenantHeaderWithoutToken_IsRejected()
    {
        // Before: the local TenantMiddleware took the tenant from this header.
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var history = await client.GetAsync("/api/v1/members/mem-1/id-cards");
        var order = await client.PostAsJsonAsync("/api/v1/id-cards/orders", Order);

        Assert.Equal(HttpStatusCode.Unauthorized, history.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, order.StatusCode);
        NothingRan();
    }

    [Fact]
    public async Task TenantQueryWithoutToken_IsRejected()
    {
        // Before: ?tenantId= was the fallback tenant source.
        var response = await _factory.CreateClient().GetAsync($"/api/v1/members/mem-1/id-cards?tenantId={Tenant}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        NothingRan();
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_IsForbidden()
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.TenantAdmin));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", OtherTenant);

        var response = await client.GetAsync("/api/v1/members/mem-1/id-cards");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        NothingRan();
    }

    [Fact]
    public async Task TokenTenant_IsTheTenantUsed()
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.TenantAdmin));

        var response = await client.GetAsync($"/api/v1/members/mem-1/id-cards?tenantId={OtherTenant}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.Orchestrator.Received(1).ListForMemberAsync(Tenant, "mem-1", Arg.Any<CancellationToken>());
        await _factory.Orchestrator.DidNotReceive().ListForMemberAsync(OtherTenant, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Order_RequesterIsTokenSubject_NotTheBodyField()
    {
        var response = await Client(Tenant).PostAsJsonAsync("/api/v1/id-cards/orders",
            new { memberId = "mem-1", channel = "Digital", requestedBy = "someone-else" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await _factory.Orchestrator.Received(1).CreateOrderAsync(
            Tenant, User, Arg.Is<CreateIdCardOrderRequest>(r => r.MemberId == "mem-1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revoke_RevokerIsTokenSubject()
    {
        var response = await Client(Tenant).PostAsJsonAsync("/api/v1/id-cards/card-1/revoke", Revocation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.Orchestrator.Received(1).RevokeAsync(
            Tenant, "card-1", User, Arg.Any<RevokeIdCardRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RoleWithoutMembersRead_CannotReadCards()
    {
        // Finance holds no members permission. ID cards are PHI.
        var client = Client(Tenant, ChoRolePermissions.Finance);

        var history = await client.GetAsync("/api/v1/members/mem-1/id-cards");
        var order = await client.GetAsync("/api/v1/id-cards/order-1");

        Assert.Equal(HttpStatusCode.Forbidden, history.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, order.StatusCode);
        NothingRan();
    }

    [Theory]
    [InlineData(ChoRolePermissions.MemberServices)]   // members:read, no members:write
    [InlineData(ChoRolePermissions.ComplianceOfficer)] // *:read only
    public async Task ReadOnlyRole_CanReadHistory_ButCannotOrderOrRevoke(string role)
    {
        var client = Client(Tenant, role);

        var history = await client.GetAsync("/api/v1/members/mem-1/id-cards");
        var order = await client.PostAsJsonAsync("/api/v1/id-cards/orders", Order);
        var revoke = await client.PostAsJsonAsync("/api/v1/id-cards/card-1/revoke", Revocation);

        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, order.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
        await _factory.Orchestrator.DidNotReceiveWithAnyArgs().CreateOrderAsync(default!, default!, default!, default);
        await _factory.Orchestrator.DidNotReceiveWithAnyArgs().RevokeAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task EnrollmentSpecialist_CanOrderAndRevoke()
    {
        var client = Client(Tenant, ChoRolePermissions.EnrollmentSpecialist);

        var order = await client.PostAsJsonAsync("/api/v1/id-cards/orders", Order);
        var revoke = await client.PostAsJsonAsync("/api/v1/id-cards/card-1/revoke", Revocation);

        Assert.Equal(HttpStatusCode.Accepted, order.StatusCode);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
    }

    [Fact]
    public async Task HealthProbe_NeedsNoToken()
    {
        var response = await _factory.CreateClient().GetAsync("/health/live");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
