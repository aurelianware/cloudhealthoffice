using MemberService.Models;
using MemberService.Services;
using MemberService.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemberService.Tests.Services;

public class RelationshipShimTests
{
    private const string Tenant = "tenant-t1";

    private static (RelationshipShim shim,
                    FamilyRelationshipService svc,
                    InMemoryMemberRepository members,
                    InMemoryFamilyRelationshipRepository rels) Build()
    {
        var members = new InMemoryMemberRepository();
        var rels = new InMemoryFamilyRelationshipRepository();
        var svc = new FamilyRelationshipService(rels, members);
        var shim = new RelationshipShim(svc, NullLogger<RelationshipShim>.Instance);
        return (shim, svc, members, rels);
    }

    private static Member MakeDep(string id, string sub, string code = "19") => new()
    {
        Id = Guid.NewGuid().ToString(),
        TenantId = Tenant,
        MemberId = id,
        GroupNumber = "GRP",
        IsSubscriber = false,
#pragma warning disable CS0618 // legacy fields — the shim's entire job is to bridge these
        SubscriberMemberId = sub,
        RelationshipCode = code,
#pragma warning restore CS0618
        FirstName = "F",
        LastName = "L",
        DateOfBirth = new DateTime(2010, 1, 1),
        EffectiveDate = new DateTime(2024, 1, 1),
    };

    private static Member MakeSub(string id) => new()
    {
        Id = Guid.NewGuid().ToString(),
        TenantId = Tenant,
        MemberId = id,
        GroupNumber = "GRP",
        IsSubscriber = true,
        FirstName = "S",
        LastName = "L",
        DateOfBirth = new DateTime(1980, 1, 1),
        EffectiveDate = new DateTime(2024, 1, 1),
    };

    [Fact]
    public async Task ShimCreatesSymmetricPair_OnDependentWithSubscriberFk()
    {
        var (shim, _, members, rels) = Build();
        await members.CreateAsync(MakeSub("SUB-1"));
        var dep = MakeDep("DEP-1", "SUB-1");
        await members.CreateAsync(dep);

        await shim.EnsureRelationshipAsync(dep, actor: "834-import");

        rels.Rows.Should().HaveCount(2);
        rels.Rows.Should().Contain(r => r.SubjectMemberId == "DEP-1" && r.RelatedMemberId == "SUB-1" && r.RelationshipCode == "19");
        rels.Rows.Should().Contain(r => r.SubjectMemberId == "SUB-1" && r.RelatedMemberId == "DEP-1" && r.RelationshipCode == "G8");
    }

    [Fact]
    public async Task ShimIsIdempotent_AcrossRepeatedCalls()
    {
        var (shim, _, members, rels) = Build();
        await members.CreateAsync(MakeSub("SUB-1"));
        var dep = MakeDep("DEP-1", "SUB-1");
        await members.CreateAsync(dep);

        await shim.EnsureRelationshipAsync(dep, actor: "834-import");
        await shim.EnsureRelationshipAsync(dep, actor: "834-import");
        await shim.EnsureRelationshipAsync(dep, actor: "834-import");

        // Still exactly one pair (2 rows) after three runs — replayed 834 batches don't duplicate.
        rels.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task ShimSkips_WhenSubscriberFkMissing()
    {
        var (shim, _, members, rels) = Build();
        var dep = MakeDep("DEP-1", sub: "");
        await members.CreateAsync(dep);

        await shim.EnsureRelationshipAsync(dep, actor: "834-import");
        rels.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ShimSkips_WhenMemberIsSubscriber()
    {
        var (shim, _, members, rels) = Build();
        var sub = MakeSub("SUB-1");
        await members.CreateAsync(sub);

        await shim.EnsureRelationshipAsync(sub, actor: "834-import");
        rels.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ShimSwallows_WhenSubscriberNotFoundInTenant()
    {
        // Shim must not block a legitimate 834 write if the graph side fails — it
        // logs and returns. The backfill tool reconciles later.
        var (shim, _, members, rels) = Build();
        var dep = MakeDep("DEP-ORPHAN", "SUB-DOES-NOT-EXIST");
        await members.CreateAsync(dep);

        var act = () => shim.EnsureRelationshipAsync(dep, actor: "834-import");
        await act.Should().NotThrowAsync();
        rels.Rows.Should().BeEmpty();
    }
}
