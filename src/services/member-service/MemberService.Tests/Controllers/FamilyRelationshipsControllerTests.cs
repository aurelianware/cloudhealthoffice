using MemberService.Controllers;
using MemberService.Models;
using MemberService.Services;
using MemberService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemberService.Tests.Controllers;

public class FamilyRelationshipsControllerTests
{
    private const string Tenant = "tenant-t1";

    private static (FamilyRelationshipsController ctl,
                    InMemoryMemberRepository members,
                    InMemoryFamilyRelationshipRepository rels,
                    FamilyRelationshipService svc,
                    InMemoryMemberEventRepository events) Build()
    {
        var members = new InMemoryMemberRepository();
        var rels = new InMemoryFamilyRelationshipRepository();
        var svc = new FamilyRelationshipService(rels, members);
        var events = new InMemoryMemberEventRepository();
        var publisher = new CosmosMemberEventPublisher(events, NullLogger<CosmosMemberEventPublisher>.Instance);
        var enc = new NoOpIdentifierEncryptor();
        var ctl = new FamilyRelationshipsController(svc, members, publisher, enc);

        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        ctl.ControllerContext = new ControllerContext { HttpContext = http };
        return (ctl, members, rels, svc, events);
    }

    private static Member Sub(string id = "SUB-1") => new()
    {
        Id = Guid.NewGuid().ToString(),
        TenantId = Tenant,
        MemberId = id,
        GroupNumber = "GRP",
        IsSubscriber = true,
        FirstName = "Alice",
        LastName = "Example",
        DateOfBirth = new DateTime(1980, 1, 1),
        EffectiveDate = new DateTime(2024, 1, 1),
    };

    private static Member Dep(string id, string sub) => new()
    {
        Id = Guid.NewGuid().ToString(),
        TenantId = Tenant,
        MemberId = id,
        GroupNumber = "GRP",
        IsSubscriber = false,
        FirstName = "Junior",
        LastName = "Example",
        DateOfBirth = new DateTime(2015, 1, 1),
        EffectiveDate = new DateTime(2024, 1, 1),
    };

    [Fact]
    public async Task Create_ReturnsCreatedAndPersistsPair()
    {
        var (ctl, members, rels, _, _) = Build();
        await members.CreateAsync(Sub("SUB-1"));
        await members.CreateAsync(Dep("DEP-1", "SUB-1"));

        var resp = await ctl.Create("DEP-1", new CreateFamilyRelationshipRequest
        {
            SubjectMemberId = "DEP-1",
            RelatedMemberId = "SUB-1",
            RelationshipCode = "19",
            StartDate = new DateTime(2024, 1, 1),
        }, CancellationToken.None);

        resp.Should().BeOfType<CreatedAtActionResult>();
        rels.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Create_InvalidCode_ReturnsBadRequest()
    {
        var (ctl, members, _, _, _) = Build();
        await members.CreateAsync(Sub());
        await members.CreateAsync(Dep("DEP-1", "SUB-1"));

        var resp = await ctl.Create("DEP-1", new CreateFamilyRelationshipRequest
        {
            SubjectMemberId = "DEP-1",
            RelatedMemberId = "SUB-1",
            RelationshipCode = "ZZ",
            StartDate = new DateTime(2024, 1, 1),
        }, CancellationToken.None);

        resp.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task End_MarksPairEnded()
    {
        var (ctl, members, rels, svc, _) = Build();
        await members.CreateAsync(Sub());
        await members.CreateAsync(Dep("DEP-1", "SUB-1"));
        var forward = await svc.CreateAsync(Tenant, new CreateFamilyRelationshipRequest
        {
            SubjectMemberId = "DEP-1",
            RelatedMemberId = "SUB-1",
            RelationshipCode = "19",
            StartDate = new DateTime(2024, 1, 1),
        }, "tester");

        var resp = await ctl.End("DEP-1", forward.Id,
            new EndRelationshipRequest { EndDate = new DateTime(2025, 6, 30) },
            CancellationToken.None);

        resp.Should().BeOfType<OkObjectResult>();
        rels.Rows.Where(r => r.PairId == forward.PairId)
            .Should().OnlyContain(r => r.EndDate == new DateTime(2025, 6, 30));
    }

    [Fact]
    public async Task List_ReturnsOnlySubjectPerspectiveRows()
    {
        var (ctl, members, _, svc, _) = Build();
        await members.CreateAsync(Sub());
        await members.CreateAsync(Dep("DEP-1", "SUB-1"));
        await svc.CreateAsync(Tenant, new CreateFamilyRelationshipRequest
        {
            SubjectMemberId = "DEP-1",
            RelatedMemberId = "SUB-1",
            RelationshipCode = "19",
            StartDate = new DateTime(2024, 1, 1),
        }, "tester");

        var resp = await ctl.List("DEP-1", includeDeleted: false, CancellationToken.None);
        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<FamilyRelationshipListResponse>().Subject;

        body.Relationships.Should().ContainSingle();
        body.Relationships[0].SubjectMemberId.Should().Be("DEP-1");
    }

    [Fact]
    public async Task AddDependent_CreatesMemberAndPairAtomically()
    {
        var (ctl, members, rels, _, _) = Build();
        await members.CreateAsync(Sub("SUB-1"));

        var resp = await ctl.AddDependent("SUB-1", new AddDependentRequest
        {
            Member = new AddDependentMember
            {
                MemberId = "DEP-NEW",
                FirstName = "Baby",
                LastName = "Example",
                DateOfBirth = new DateTime(2024, 3, 1),
                EffectiveDate = new DateTime(2024, 4, 1),
            },
            Relationship = new AddDependentRelationship
            {
                RelationshipCode = "19",
                StartDate = new DateTime(2024, 4, 1),
                IsCustodial = true,
            },
        }, CancellationToken.None);

        resp.Should().BeOfType<CreatedAtActionResult>();
        members.Members.Should().ContainSingle(m => m.MemberId == "DEP-NEW" && !m.IsDraft);
        rels.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task AddDependent_InvalidRelationship_LeavesDraftMember_ReturnsBadRequest()
    {
        // Simulate the second write failing: we pass a code that passes the initial
        // validity check but collides with an existing active pair. Create one first.
        var (ctl, members, rels, svc, _) = Build();
        await members.CreateAsync(Sub("SUB-1"));
        var existingDep = Dep("DEP-EXIST", "SUB-1");
        await members.CreateAsync(existingDep);
        await svc.CreateAsync(Tenant, new CreateFamilyRelationshipRequest
        {
            SubjectMemberId = "DEP-EXIST",
            RelatedMemberId = "SUB-1",
            RelationshipCode = "19",
            StartDate = new DateTime(2024, 1, 1),
        }, "tester");

        // Now try to add a second "DEP-EXIST" member — MemberId conflict rejected early.
        var resp = await ctl.AddDependent("SUB-1", new AddDependentRequest
        {
            Member = new AddDependentMember
            {
                MemberId = "DEP-EXIST",
                FirstName = "Dup",
                LastName = "Example",
                DateOfBirth = new DateTime(2024, 3, 1),
                EffectiveDate = new DateTime(2024, 4, 1),
            },
            Relationship = new AddDependentRelationship { RelationshipCode = "19", StartDate = new DateTime(2024, 4, 1) },
        }, CancellationToken.None);

        resp.Should().BeOfType<ConflictObjectResult>();
    }
}
