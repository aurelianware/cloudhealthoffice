using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaimsService.Audit;
using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Security;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests;

/// <summary>An in-memory claim access log for the API tests, with a switch to simulate an outage.</summary>
public sealed class InMemoryClaimAccessAuditRepository : IClaimAccessAuditRepository
{
    private readonly ConcurrentQueue<ClaimAccessEvent> _events = new();

    public bool Fail { get; set; }

    public IReadOnlyCollection<ClaimAccessEvent> All => _events.ToArray();

    public Task AppendAsync(ClaimAccessEvent accessEvent, CancellationToken ct = default)
    {
        if (Fail) throw new InvalidOperationException("audit store down");
        _events.Enqueue(accessEvent);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ClaimAccessEvent>> QueryAsync(ClaimAccessQuery q, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ClaimAccessEvent>>(_events
            .Where(e => e.TenantId == q.TenantId)
            .Where(e => q.ClaimId is null || e.ClaimId == q.ClaimId || e.ClaimIds.Contains(q.ClaimId))
            .Where(e => q.MemberId is null || e.MemberId == q.MemberId)
            .Where(e => q.ActorId is null || e.ActorId == q.ActorId)
            .Where(e => q.From is null || e.OccurredAt >= q.From)
            .Where(e => q.To is null || e.OccurredAt <= q.To)
            .OrderByDescending(e => e.OccurredAt)
            .Take(q.Limit)
            .ToList());
}

/// <summary>
/// The claim access log: every read of claim data is recorded with who, what
/// and the outcome — allowed, not found or denied — and compliance can query
/// it by claim, member or user. An audit outage never fails a read.
/// </summary>
public class ClaimAccessAuditTests : IClassFixture<ClaimsApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ClaimsApiFactory _factory;

    public ClaimAccessAuditTests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _factory.AccessAudit.Fail = false;
    }

    private HttpClient Client(string role, string user)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(user, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        return client;
    }

    private Claim Seed(string? memberId = null)
    {
        var id = "audit-claim-" + Guid.NewGuid().ToString("N")[..8];
        var claim = new Claim
        {
            Id = id, TenantId = "test-tenant", ClaimNumber = "CN-" + id, MemberId = memberId ?? "MBR-" + id,
            Status = ClaimStatus.Pended, BillingProviderNPI = "1234567890",
        };
        _factory.ClaimRepository.GetByIdAsync(id).Returns(claim);
        return claim;
    }

    private IReadOnlyList<ClaimAccessEvent> EventsFor(string claimId) =>
        _factory.AccessAudit.All.Where(e => e.ClaimId == claimId || e.ClaimIds.Contains(claimId)).ToList();

    [Fact]
    public async Task Reading_a_claim_records_who_what_and_whose()
    {
        var claim = Seed();

        var response = await Client(ChoRolePermissions.ClaimsExaminer, "examiner-7").GetAsync($"/api/claims/{claim.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var e = Assert.Single(EventsFor(claim.Id));
        Assert.Equal((ClaimAccessActions.View, ClaimAccessOutcomes.Allowed, 200), (e.Action, e.Outcome, e.StatusCode));
        Assert.Equal(claim.ClaimNumber, e.ClaimNumber);
        Assert.Equal(claim.MemberId, e.MemberId);
        Assert.Equal("examiner-7", e.ActorId);
        Assert.Contains(ChoRolePermissions.ClaimsExaminer, e.ActorRoles);
        Assert.Equal("test-tenant", e.TenantId);
        Assert.Equal("api/Claims/{id}", e.Route, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_claim_that_does_not_exist_is_recorded_as_not_found()
    {
        var id = "missing-" + Guid.NewGuid().ToString("N")[..8];

        await Client(ChoRolePermissions.ClaimsExaminer, "examiner-7").GetAsync($"/api/claims/{id}");

        Assert.Equal(ClaimAccessOutcomes.NotFound, Assert.Single(EventsFor(id)).Outcome);
    }

    [Fact]
    public async Task A_denied_read_is_recorded_as_denied()
    {
        var claim = Seed();

        var response = await Client(ChoRolePermissions.ComplianceViewer, "viewer-1").GetAsync($"/api/claims/{claim.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var e = Assert.Single(EventsFor(claim.Id));
        Assert.Equal((ClaimAccessOutcomes.Denied, 403, "viewer-1"), (e.Outcome, e.StatusCode, e.ActorId));
    }

    [Fact]
    public async Task A_search_records_every_claim_it_returned_and_the_member_searched_for()
    {
        var a = Seed("MBR-SEARCH");
        var b = Seed("MBR-SEARCH");
        _factory.ClaimRepository.SearchAsync("MBR-SEARCH", null, null, null, null, null, 1, 50)
            .Returns(new[] { a, b });

        await Client(ChoRolePermissions.MemberServices, "rep-3").GetAsync("/api/claims/search?memberId=MBR-SEARCH");

        var e = Assert.Single(_factory.AccessAudit.All, x => x.ActorId == "rep-3" && x.Action == ClaimAccessActions.Search);
        Assert.Equal(new[] { a.Id, b.Id }, e.ClaimIds);
        Assert.Equal(2, e.ResultCount);
        Assert.Equal("MBR-SEARCH", e.MemberId);
        Assert.Null(e.ClaimId);
    }

    [Fact]
    public async Task Notes_and_edits_reads_are_recorded_against_the_claim()
    {
        var claim = Seed();
        var client = Client(ChoRolePermissions.ClaimsExaminer, "examiner-8");

        await client.GetAsync($"/api/claims/{claim.Id}/notes");
        await client.GetAsync($"/api/claims/{claim.Id}/edits");

        Assert.Equal(new[] { ClaimAccessActions.Notes, ClaimAccessActions.Edits },
            EventsFor(claim.Id).Select(e => e.Action).OrderByDescending(a => a).ToArray());
    }

    [Fact]
    public async Task An_audit_outage_never_fails_the_read()
    {
        var claim = Seed();
        _factory.AccessAudit.Fail = true;

        var response = await Client(ChoRolePermissions.ClaimsExaminer, "examiner-9").GetAsync($"/api/claims/{claim.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Compliance_can_see_who_read_a_claim_and_reading_the_log_is_recorded_too()
    {
        var claim = Seed();
        await Client(ChoRolePermissions.ClaimsExaminer, "examiner-10").GetAsync($"/api/claims/{claim.Id}");
        await Client(ChoRolePermissions.ProviderRelations, "pr-1").GetAsync($"/api/claims/{claim.Id}");

        var log = await Client(ChoRolePermissions.ComplianceOfficer, "compliance-1")
            .GetFromJsonAsync<List<ClaimAccessEvent>>($"/api/claims/{claim.Id}/access-log", Json);

        Assert.Equal(new[] { "examiner-10", "pr-1" }, log!.Select(e => e.ActorId).OrderBy(a => a).ToArray());
        Assert.Contains(EventsFor(claim.Id), e => e.Action == ClaimAccessActions.AccessLog && e.ActorId == "compliance-1");
    }

    [Fact]
    public async Task The_log_needs_audit_read_and_a_filter()
    {
        var claim = Seed();

        var asExaminer = await Client(ChoRolePermissions.ClaimsExaminer, "examiner-11").GetAsync($"/api/claims/{claim.Id}/access-log");
        var noFilter = await Client(ChoRolePermissions.ComplianceOfficer, "compliance-2").GetAsync("/api/claims/access-log");
        var byMember = await Client(ChoRolePermissions.ComplianceOfficer, "compliance-2").GetAsync($"/api/claims/access-log?memberId={claim.MemberId}");

        Assert.Equal(HttpStatusCode.Forbidden, asExaminer.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noFilter.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byMember.StatusCode);
    }

    [Fact]
    public void Subjects_are_found_in_a_claim_a_list_or_a_page_object()
    {
        var claim = new Claim { Id = "c1", ClaimNumber = "N1", MemberId = "M1" };

        Assert.Equal(new ClaimSubject("c1", "N1", "M1"), Assert.Single(ClaimSubjects.From(claim)));
        Assert.Equal(2, ClaimSubjects.From(new[] { claim, claim }).Count);
        Assert.Single(ClaimSubjects.From(new { claims = new[] { claim }, totalCount = 1 }));
        Assert.Single(ClaimSubjects.From(new { ClaimId = "c9", MemberId = "M9" }));
        Assert.Empty(ClaimSubjects.From(new { Id = "not-a-claim" }));
        Assert.Empty(ClaimSubjects.From("text"));
    }

    [Fact]
    public async Task Recently_viewed_lists_the_callers_own_claims_newest_first_once_each()
    {
        var a = Seed();
        var b = Seed();
        var me = Client(ChoRolePermissions.ClaimsExaminer, "examiner-recent");
        await me.GetAsync($"/api/claims/{a.Id}");
        await me.GetAsync($"/api/claims/{b.Id}");
        await me.GetAsync($"/api/claims/{a.Id}");
        await Client(ChoRolePermissions.ClaimsExaminer, "someone-else").GetAsync($"/api/claims/{Seed().Id}");

        var recent = await me.GetFromJsonAsync<List<global::ClaimsService.Controllers.RecentClaimView>>("/api/claims/recently-viewed", Json);

        Assert.NotNull(recent);
        Assert.Equal(new[] { a.Id, b.Id }, recent.Select(r => r.ClaimId).ToArray());
        Assert.Equal(a.ClaimNumber, recent[0].ClaimNumber);
    }
}
