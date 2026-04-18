using MemberService.Models;
using MemberService.Services;
using MemberService.Tests.Fakes;

namespace MemberService.Tests.Services;

public class FamilyRelationshipServiceTests
{
    private const string Tenant = "tenant-t1";
    private const string OtherTenant = "tenant-t2";

    private static (FamilyRelationshipService svc,
                    InMemoryMemberRepository members,
                    InMemoryFamilyRelationshipRepository rels) Build()
    {
        var members = new InMemoryMemberRepository();
        var rels = new InMemoryFamilyRelationshipRepository();
        var svc = new FamilyRelationshipService(rels, members);
        return (svc, members, rels);
    }

    private static Member Make(string memberId, string tenant = Tenant, bool isSubscriber = true)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = tenant,
            MemberId = memberId,
            GroupNumber = "GRP",
            IsSubscriber = isSubscriber,
            FirstName = "F",
            LastName = "L",
            DateOfBirth = new DateTime(1990, 1, 1),
            EffectiveDate = new DateTime(2024, 1, 1),
        };

    private static CreateFamilyRelationshipRequest Req(string subject, string related, string code = "19")
        => new()
        {
            SubjectMemberId = subject,
            RelatedMemberId = related,
            RelationshipCode = code,
            StartDate = new DateTime(2024, 1, 1),
        };

    [Fact]
    public async Task Create_WritesSymmetricPair_WithSharedPairId()
    {
        var (svc, members, rels) = Build();
        await members.CreateAsync(Make("SUB-1"));
        await members.CreateAsync(Make("DEP-1", isSubscriber: false));

        var forward = await svc.CreateAsync(Tenant, Req("DEP-1", "SUB-1", "19"), "tester");

        rels.Rows.Should().HaveCount(2);
        var pair = rels.Rows.Where(r => r.PairId == forward.PairId).ToList();
        pair.Should().HaveCount(2);
        pair.Select(r => r.SubjectMemberId).Should().BeEquivalentTo(new[] { "DEP-1", "SUB-1" });
        pair.Select(r => r.RelationshipCode).Should().BeEquivalentTo(new[] { "19", "G8" });
    }

    [Fact]
    public async Task Create_UnknownRelationshipCode_Rejected()
    {
        var (svc, members, _) = Build();
        await members.CreateAsync(Make("SUB-1"));
        await members.CreateAsync(Make("DEP-1", isSubscriber: false));

        var act = () => svc.CreateAsync(Tenant, Req("DEP-1", "SUB-1", "ZZ"), "tester");
        await act.Should().ThrowAsync<FamilyRelationshipValidationException>()
            .WithMessage("*Unknown relationshipCode*");
    }

    [Fact]
    public async Task Create_SelfRelationship_Rejected()
    {
        var (svc, members, _) = Build();
        await members.CreateAsync(Make("M-1"));

        var act = () => svc.CreateAsync(Tenant, Req("M-1", "M-1", "18"), "tester");
        await act.Should().ThrowAsync<FamilyRelationshipValidationException>()
            .WithMessage("*cannot have a relationship to themselves*");
    }

    [Fact]
    public async Task Create_DuplicateActivePair_Rejected()
    {
        var (svc, members, _) = Build();
        await members.CreateAsync(Make("SUB-1"));
        await members.CreateAsync(Make("DEP-1", isSubscriber: false));

        await svc.CreateAsync(Tenant, Req("DEP-1", "SUB-1", "19"), "tester");

        // Typed exception so shim / backfill idempotency paths don't rely on
        // brittle error-message matching.
        var act = () => svc.CreateAsync(Tenant, Req("DEP-1", "SUB-1", "19"), "tester");
        await act.Should().ThrowAsync<DuplicateFamilyRelationshipException>();
    }

    [Fact]
    public async Task Create_Rejects_SecondActivePair_EvenWithFutureEndDate()
    {
        var (svc, members, _) = Build();
        await members.CreateAsync(Make("SUB-1"));
        await members.CreateAsync(Make("DEP-1", isSubscriber: false));

        // First pair has a future EndDate — still "active" per model.IsActive.
        var req = Req("DEP-1", "SUB-1", "19");
        req.EndDate = DateTime.UtcNow.AddYears(5);
        await svc.CreateAsync(Tenant, req, "tester");

        var act = () => svc.CreateAsync(Tenant, Req("DEP-1", "SUB-1", "19"), "tester");
        await act.Should().ThrowAsync<DuplicateFamilyRelationshipException>();
    }

    [Fact]
    public async Task Create_CrossTenant_Rejected()
    {
        var (svc, members, _) = Build();
        await members.CreateAsync(Make("SUB-1", tenant: Tenant));
        await members.CreateAsync(Make("DEP-1", tenant: OtherTenant, isSubscriber: false));

        // Subject in Tenant, but related member only exists in OtherTenant → lookup fails.
        var act = () => svc.CreateAsync(Tenant, Req("SUB-1", "DEP-1", "G8"), "tester");
        await act.Should().ThrowAsync<FamilyRelationshipValidationException>()
            .WithMessage("*not found in tenant*");
    }

    [Fact]
    public async Task End_SetsEndDateOnBothRowsOfPair()
    {
        var (svc, members, rels) = Build();
        await members.CreateAsync(Make("SUB-1"));
        await members.CreateAsync(Make("DEP-1", isSubscriber: false));
        var forward = await svc.CreateAsync(Tenant, Req("DEP-1", "SUB-1", "19"), "tester");

        var ended = await svc.EndAsync(Tenant, forward.Id, new DateTime(2025, 6, 30), "tester");

        ended.EndDate.Should().Be(new DateTime(2025, 6, 30));
        rels.Rows.Where(r => r.PairId == forward.PairId)
            .Should().OnlyContain(r => r.EndDate == new DateTime(2025, 6, 30));
    }

    [Fact]
    public async Task SoftDelete_WithinWindow_MarksBothRowsDeleted()
    {
        var (svc, members, rels) = Build();
        await members.CreateAsync(Make("SUB-1"));
        await members.CreateAsync(Make("DEP-1", isSubscriber: false));
        var forward = await svc.CreateAsync(Tenant, Req("DEP-1", "SUB-1", "19"), "tester");

        var deleted = await svc.SoftDeleteAsync(Tenant, forward.Id, "wrong code", "admin");

        deleted.DeletedAt.Should().NotBeNull();
        rels.Rows.Where(r => r.PairId == forward.PairId)
            .Should().OnlyContain(r => r.DeletedAt != null && r.DeletedBy == "admin");
    }

    [Fact]
    public async Task SoftDelete_OutsideWindow_Rejected()
    {
        var (svc, members, rels) = Build();
        await members.CreateAsync(Make("SUB-1"));
        await members.CreateAsync(Make("DEP-1", isSubscriber: false));
        var forward = await svc.CreateAsync(Tenant, Req("DEP-1", "SUB-1", "19"), "tester");

        // Age out the pair.
        foreach (var r in rels.Rows.Where(r => r.PairId == forward.PairId))
        {
            r.CreatedDate = DateTime.UtcNow.AddDays(-3);
        }

        var act = () => svc.SoftDeleteAsync(Tenant, forward.Id, "too late", "admin");
        await act.Should().ThrowAsync<FamilyRelationshipValidationException>()
            .WithMessage("*only permitted within*");
    }

    [Fact]
    public async Task Derivation_ReturnsSubscriberMemberId_FromActiveEdge()
    {
        var (svc, members, _) = Build();
        await members.CreateAsync(Make("SUB-1"));
        await members.CreateAsync(Make("DEP-1", isSubscriber: false));
        await svc.CreateAsync(Tenant, Req("DEP-1", "SUB-1", "19"), "tester");

        var derived = await svc.DeriveSubscriberMemberIdAsync(Tenant, "DEP-1");
        derived.Should().Be("SUB-1");
    }

    [Fact]
    public async Task Derivation_IgnoresEndedRelationships()
    {
        var (svc, members, _) = Build();
        await members.CreateAsync(Make("SUB-1"));
        await members.CreateAsync(Make("DEP-1", isSubscriber: false));
        var forward = await svc.CreateAsync(Tenant, Req("DEP-1", "SUB-1", "19"), "tester");
        await svc.EndAsync(Tenant, forward.Id, DateTime.UtcNow.AddDays(-1), "tester");

        var derived = await svc.DeriveSubscriberMemberIdAsync(Tenant, "DEP-1");
        derived.Should().BeNull();
    }

    [Fact]
    public void RelationshipCodes_IsValid_RejectsUnknown()
    {
        FamilyRelationshipCodes.IsValid("19").Should().BeTrue();
        FamilyRelationshipCodes.IsValid("01").Should().BeTrue();
        FamilyRelationshipCodes.IsValid(null).Should().BeFalse();
        FamilyRelationshipCodes.IsValid("").Should().BeFalse();
        FamilyRelationshipCodes.IsValid("ZZ").Should().BeFalse();
    }

    [Fact]
    public void InverseCode_Child_MapsToParent()
    {
        FamilyRelationshipCodes.Invert("19").Should().Be("G8");
        FamilyRelationshipCodes.Invert("G8").Should().Be("19");
        FamilyRelationshipCodes.Invert("01").Should().Be("01"); // spouse self-inverse
    }
}
