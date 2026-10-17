using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Audit;
using CloudHealthOffice.Infrastructure.Security;
using MemberService.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MemberService.Tests.Integration;

/// <summary>
/// HIPAA access accounting through the real pipeline: every read of a member
/// record is logged with who read it, a refused read is logged as Denied, a
/// search logs the members it returned, the log itself needs audit:read (and
/// reading it is logged), and a log outage never blocks the read.
/// </summary>
public class MemberAccessAuditPipelineTests : IClassFixture<MemberFhirSmokeTests.Factory>
{
    private const string Tenant = "tenant-audit";
    private const string MemberId = "AUD-001";

    private readonly WebApplicationFactory<Program> _host;
    private readonly RecordAccessStoreInMemory _log = new();

    public MemberAccessAuditPipelineTests(MemberFhirSmokeTests.Factory factory)
    {
        if (!factory.MemberRepo.Members.Any(m => m.MemberId == MemberId && m.TenantId == Tenant))
        {
            factory.MemberRepo.Members.Add(new Member
            {
                TenantId = Tenant,
                MemberId = MemberId,
                FirstName = "Audra",
                LastName = "Tally",
                DateOfBirth = new DateTime(1975, 3, 4),
                EffectiveDate = new DateTime(2024, 1, 1),
                GroupNumber = "GRP",
                IsSubscriber = true,
            });
        }
        _host = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IRecordAccessStore>();
            services.AddSingleton<IRecordAccessStore>(_log);
        }));
    }

    private HttpClient As(string role, string user = "")
    {
        var client = _host.CreateDefaultClient(new ChoDevelopmentTokenHandler(user == "" ? "user-" + role : user, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    [Fact]
    public async Task Reading_a_member_records_who_read_it()
    {
        var response = await As(ChoRolePermissions.MemberServices, "csr-7").GetAsync($"/api/v1/members/{MemberId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var e = _log.All.Should().ContainSingle(x => x.RecordId == MemberId).Subject;
        e.TenantId.Should().Be(Tenant);
        e.RecordType.Should().Be("Member");
        e.Action.Should().Be("View");
        e.Outcome.Should().Be(RecordAccessOutcomes.Allowed);
        e.MemberId.Should().Be(MemberId);
        e.ActorId.Should().Be("csr-7");
        e.ActorRoles.Should().Contain(ChoRolePermissions.MemberServices);
        e.Route.Should().NotContain("?");
    }

    [Fact]
    public async Task A_refused_read_is_recorded_as_denied()
    {
        var response = await As(ChoRolePermissions.Finance, "fin-1").GetAsync($"/api/v1/members/{MemberId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var e = _log.All.Should().ContainSingle(x => x.ActorId == "fin-1").Subject;
        e.Outcome.Should().Be(RecordAccessOutcomes.Denied);
        e.StatusCode.Should().Be(403);
        e.RecordId.Should().Be(MemberId);
    }

    [Fact]
    public async Task A_search_records_the_members_it_returned()
    {
        var response = await As(ChoRolePermissions.MemberServices, "csr-search").GetAsync("/api/v1/members/search?lastName=Tally");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var e = _log.All.Should().ContainSingle(x => x.ActorId == "csr-search").Subject;
        e.Action.Should().Be(RecordAccessActions.Search);
        e.RecordIds.Should().Contain(MemberId);
        e.Route.Should().NotContain("Tally");
    }

    [Fact]
    public async Task The_access_log_needs_audit_read_and_reading_it_is_logged()
    {
        await As(ChoRolePermissions.MemberServices, "csr-9").GetAsync($"/api/v1/members/{MemberId}");

        (await As(ChoRolePermissions.MemberServices).GetAsync($"/api/v1/members/{MemberId}/access-log"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var response = await As(ChoRolePermissions.ComplianceOfficer, "co-1").GetAsync($"/api/v1/members/{MemberId}/access-log");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entries = await response.Content.ReadFromJsonAsync<List<RecordAccessEvent>>();
        entries.Should().Contain(x => x.ActorId == "csr-9" && x.Action == "View");

        _log.All.Should().Contain(x => x.ActorId == "co-1" && x.Action == RecordAccessActions.AccessLog);
    }

    [Fact]
    public async Task The_log_search_by_member_needs_a_narrowing_filter()
    {
        var co = As(ChoRolePermissions.ComplianceOfficer);

        (await co.GetAsync("/api/v1/members/access-log")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await co.GetAsync($"/api/v1/members/access-log?memberId={MemberId}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_log_outage_never_blocks_the_read()
    {
        _log.Fail = true;
        try
        {
            var response = await As(ChoRolePermissions.MemberServices).GetAsync($"/api/v1/members/{MemberId}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            _log.Fail = false;
        }
    }
}
