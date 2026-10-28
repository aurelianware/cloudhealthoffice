using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Testing.Mongo;
using FluentAssertions;
using MongoDB.Driver;

namespace CloudHealthOffice.ClaimsService.Tests.Repositories;

/// <summary>
/// The Provider Access attribution question against a real mongod
/// (CMS-0057-F PROV-02). This is where the attribution RULE lives, so this is
/// where it is proved: which claims evidence a treatment relationship, which do
/// not, and where the lookback window cuts.
///
/// What is worth pinning on the store: tenant isolation (a provider treating
/// the same member under another plan is another relationship), that billing
/// and rendering both count, that a void does not, that claim history is read
/// at the chain head, and that the window is judged on the END of the service
/// period so a long admission that ran into the window still counts.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ProviderAttributionRepositoryTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-attribution";
    private const string OtherTenant = "tenant-attribution-other";
    private const string Member = "MEM-1";
    private const string RenderedMember = "MEM-RENDERED";
    private const string OtherTenantMember = "MEM-OTHER-TENANT-ONLY";

    /// <summary>The asking provider.</summary>
    private const string Npi = "1234567893";

    /// <summary>A provider who never treated anyone in this fixture.</summary>
    private const string StrangerNpi = "1999999984";

    /// <summary>The group that billed for the asking provider's work.</summary>
    private const string GroupNpi = "1700000001";

    /// <summary>
    /// The decision instant every case is judged at, and a 24-month floor
    /// beneath it. Fixed rather than relative to "now" so the boundary cases
    /// mean the same thing in a year's time.
    /// </summary>
    private static readonly DateTime Now = new(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Since = Now.AddMonths(-24);

    private IMongoDatabase _database = null!;
    private ProviderAttributionRepositoryMongo _repository = null!;

    public async Task InitializeAsync()
    {
        _database = mongo.CreateDatabase("provider_attribution");
        _repository = new ProviderAttributionRepositoryMongo(_database);
        await _database.GetCollection<Claim>("Claims").InsertManyAsync(Seed());
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_database);

    private static Claim Claim(
        string id,
        string tenant = Tenant,
        string member = Member,
        string? billing = Npi,
        string? rendering = null,
        DateTime? from = null,
        DateTime? to = null)
    {
        var start = from ?? Now.AddMonths(-3);
        return new Claim
        {
            Id = id,
            ClaimVersionId = id,
            TenantId = tenant,
            ClaimNumber = "CLM-" + id,
            MemberId = member,
            Status = ClaimStatus.Paid,
            VersionState = ClaimVersionState.Paid,
            BillingProviderNPI = billing ?? string.Empty,
            RenderingProviderNPI = rendering,
            TotalChargeAmount = 100m,
            ServiceDateFrom = start,
            ServiceDateTo = to ?? start,
            SubmittedDate = start,
            LastUpdatedDate = start,
        };
    }

    private static IEnumerable<Claim> Seed()
    {
        // Treatment inside the window, billed by the asking provider.
        yield return Claim("IN-WINDOW");

        // Rendered — not billed — by the asking provider. A group bills, an
        // individual renders; both have a relationship with the member, and
        // this member has no other claim so each side is isolated.
        yield return Claim("RENDERED", member: RenderedMember, billing: GroupNpi, rendering: Npi);

        // The same provider and the same member, in a different tenant, and
        // nowhere else. Asked from this tenant it must not attribute.
        yield return Claim("OTHER-TENANT", tenant: OtherTenant, member: OtherTenantMember);

        // Withdrawn: the submitter says the service did not happen.
        var voided = Claim("VOIDED", member: "MEM-VOID");
        voided.Status = ClaimStatus.Voided;
        yield return voided;

        // Denied. The plan refusing to pay is not the plan saying the visit
        // did not occur, so this still evidences treatment.
        var denied = Claim("DENIED", member: "MEM-DENIED");
        denied.Status = ClaimStatus.Denied;
        denied.VersionState = ClaimVersionState.Denied;
        yield return denied;

        // History, not a current record: superseded by an adjustment, and a
        // draft that was never submitted.
        var superseded = Claim("SUPERSEDED", member: "MEM-SUPERSEDED");
        superseded.SupersededAt = Now.AddMonths(-2);
        yield return superseded;

        var draft = Claim("DRAFT", member: "MEM-DRAFT");
        draft.VersionState = ClaimVersionState.Draft;
        yield return draft;

        // Wholly before the window.
        yield return Claim(
            "STALE", member: "MEM-STALE",
            from: Now.AddMonths(-40), to: Now.AddMonths(-39));

        // An admission that began before the window and ran into it. The
        // relationship is at its strongest here, so the window is judged on
        // the end of the service period rather than the start.
        yield return Claim(
            "SPANNING", member: "MEM-SPANNING",
            from: Now.AddMonths(-25), to: Now.AddMonths(-23));

        // Exactly on the floor: the window is inclusive of its own boundary.
        yield return Claim("ON-FLOOR", member: "MEM-ON-FLOOR", from: Since, to: Since);

        // One day short of it.
        yield return Claim(
            "BELOW-FLOOR", member: "MEM-BELOW-FLOOR",
            from: Since.AddDays(-1), to: Since.AddDays(-1));
    }

    private Task<bool> Attributed(string member, string provider = Npi, string tenant = Tenant)
        => _repository.HasTreatedAsync(tenant, provider, member, Since);

    [Fact]
    public async Task AClaimTheProviderBilledInsideTheWindowIsATreatmentRelationship()
        => (await Attributed(Member)).Should().BeTrue();

    [Fact]
    public async Task BothTheRenderingProviderAndTheBillingGroupAreAttributed()
    {
        // This member's only claim was billed by a group and rendered by the
        // asking provider, so each side is tested in isolation. A rule that
        // looked at the billing NPI alone would lose the clinician who
        // actually saw the member — the commonest Provider Access caller.
        (await Attributed(RenderedMember)).Should().BeTrue();
        (await Attributed(RenderedMember, provider: GroupNpi)).Should().BeTrue();
    }

    [Fact]
    public async Task AProviderWhoNeverTreatedTheMemberIsNotAttributed()
        => (await Attributed(Member, provider: StrangerNpi)).Should().BeFalse();

    [Fact]
    public async Task AnotherTenantsClaimDoesNotAttributeHere()
    {
        // One claim, in OtherTenant, naming this provider and this member.
        // The relationship is real there and must not be visible here: a
        // provider treating the same person under another plan has no
        // Provider Access to this plan's record of them.
        (await Attributed(OtherTenantMember, tenant: OtherTenant)).Should().BeTrue();
        (await Attributed(OtherTenantMember)).Should().BeFalse();
    }

    [Fact]
    public async Task AVoidedClaimDoesNotEvidenceTreatment()
        => (await Attributed("MEM-VOID")).Should().BeFalse();

    [Fact]
    public async Task ADeniedClaimStillEvidencesTreatment()
        => (await Attributed("MEM-DENIED")).Should().BeTrue();

    [Fact]
    public async Task HistoryIsNotRead()
    {
        (await Attributed("MEM-SUPERSEDED")).Should().BeFalse();
        (await Attributed("MEM-DRAFT")).Should().BeFalse();
    }

    [Fact]
    public async Task TreatmentOlderThanTheWindowDoesNotAttribute()
        => (await Attributed("MEM-STALE")).Should().BeFalse();

    [Fact]
    public async Task AnAdmissionThatRanIntoTheWindowAttributes()
        => (await Attributed("MEM-SPANNING")).Should().BeTrue();

    [Fact]
    public async Task TheWindowIncludesItsOwnFloorAndExcludesTheDayBefore()
    {
        (await Attributed("MEM-ON-FLOOR")).Should().BeTrue();
        (await Attributed("MEM-BELOW-FLOOR")).Should().BeFalse();
    }

    [Fact]
    public async Task BlankIdsAttributeNoOne()
    {
        (await Attributed(Member, provider: "   ")).Should().BeFalse();
        (await Attributed("  ")).Should().BeFalse();
    }

    [Fact]
    public async Task ThePanelListsExactlyTheMembersTheQuestionAttributes()
    {
        // The bulk panel is the boolean rule without the member: everyone it
        // would say yes for, and no one it would refuse.
        var panel = await _repository.ListTreatedMembersAsync(Tenant, Npi, Since);

        panel.Should().Equal(
            "MEM-1", "MEM-DENIED", "MEM-ON-FLOOR", "MEM-RENDERED", "MEM-SPANNING");
        foreach (var member in panel)
            (await Attributed(member)).Should().BeTrue();
    }

    [Fact]
    public async Task ThePanelIsPerTenantAndPerProvider()
    {
        (await _repository.ListTreatedMembersAsync(OtherTenant, Npi, Since)).Should().Equal(OtherTenantMember);
        (await _repository.ListTreatedMembersAsync(Tenant, GroupNpi, Since)).Should().Equal(RenderedMember);
        (await _repository.ListTreatedMembersAsync(Tenant, StrangerNpi, Since)).Should().BeEmpty();
        (await _repository.ListTreatedMembersAsync(Tenant, "  ", Since)).Should().BeEmpty();
    }
}
