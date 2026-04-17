using MemberService.Controllers;
using MemberService.Models;
using MemberService.Services;
using MemberService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemberService.Tests.Controllers;

public class IdentifiersControllerTests
{
    private const string Tenant = "tenant-test";

    private static (IdentifiersController ctl,
                    InMemoryMemberRepository repo,
                    InMemoryMemberEventRepository events,
                    NoOpIdentifierEncryptor enc) Build()
    {
        var repo = new InMemoryMemberRepository();
        var events = new InMemoryMemberEventRepository();
        var enc = new NoOpIdentifierEncryptor();
        var fp = new NoOpIdentifierFingerprinter();
        var publisher = new CosmosMemberEventPublisher(events, NullLogger<CosmosMemberEventPublisher>.Instance);
        var ctl = new IdentifiersController(repo, enc, fp, publisher);
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        ctl.ControllerContext = new ControllerContext { HttpContext = http };
        return (ctl, repo, events, enc);
    }

    private static Member SeedMember(InMemoryMemberRepository repo, string memberId = "M-001") =>
        repo.CreateAsync(new Member
        {
            TenantId = Tenant,
            MemberId = memberId,
            GroupNumber = "GRP",
            IsSubscriber = true,
            FirstName = "A", LastName = "B",
            DateOfBirth = new DateTime(2000, 1, 1),
            EffectiveDate = new DateTime(2024, 1, 1)
        }).Result;

    [Fact]
    public async Task Add_Typed_PersistsAndEmitsEvent()
    {
        var (ctl, repo, events, _) = Build();
        SeedMember(repo);

        var resp = await ctl.Add("M-001", new AddIdentifierRequest
        {
            Type = MemberIdentifierType.MedicareMbi,
            Value = "1EG4-TE5-MK73"
        }, CancellationToken.None);

        resp.Should().BeOfType<CreatedAtActionResult>();
        repo.Members[0].Identifiers.Should().ContainSingle(i => i.Type == MemberIdentifierType.MedicareMbi
            && i.System == FhirIdentifierSystems.MedicareMbi);
        events.All.Should().ContainSingle();
    }

    [Fact]
    public async Task Add_Legacy_RequiresSlug()
    {
        var (ctl, repo, _, _) = Build();
        SeedMember(repo);

        var act = async () => await ctl.Add("M-001",
            new AddIdentifierRequest { Type = MemberIdentifierType.Legacy, Value = "X" },
            CancellationToken.None);

        await act.Should().ThrowAsync<System.ComponentModel.DataAnnotations.ValidationException>();
    }

    [Fact]
    public async Task Add_LegacyWithSlug_UsesScopedUri()
    {
        var (ctl, repo, _, _) = Build();
        SeedMember(repo);

        await ctl.Add("M-001", new AddIdentifierRequest
        {
            Type = MemberIdentifierType.Legacy,
            LegacySlug = "acme-v1",
            Value = "ACME-001"
        }, CancellationToken.None);

        repo.Members[0].Identifiers[0].System.Should().Be("urn:cho:legacy:acme-v1");
    }

    [Fact]
    public async Task Add_Duplicate_Returns409()
    {
        var (ctl, repo, _, _) = Build();
        SeedMember(repo);

        var req = new AddIdentifierRequest
        {
            Type = MemberIdentifierType.Portal,
            Value = "portal-uid"
        };
        await ctl.Add("M-001", req, CancellationToken.None);
        var second = await ctl.Add("M-001", req, CancellationToken.None);

        second.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Add_UnknownMember_Returns404()
    {
        var (ctl, _, _, _) = Build();
        var resp = await ctl.Add("missing",
            new AddIdentifierRequest { Type = MemberIdentifierType.Portal, Value = "x" },
            CancellationToken.None);
        resp.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task List_Redacts_EncryptedValues()
    {
        var (ctl, repo, _, _) = Build();
        var m = SeedMember(repo);
        m.Identifiers.Add(new MemberIdentifier
        {
            Type = MemberIdentifierType.SSN,
            System = FhirIdentifierSystems.SSN,
            Value = "ciphertext-foo",
            IsEncrypted = true
        });

        var resp = await ctl.List("M-001");
        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var list = (List<IdentifierResponse>)ok.Value!;
        list.Should().ContainSingle(i => i.Type == MemberIdentifierType.SSN)
            .Which.Value.Should().Be("[REDACTED]");
    }

    [Fact]
    public async Task Remove_ByPlaintext_Removes()
    {
        var (ctl, repo, _, _) = Build();
        var m = SeedMember(repo);
        m.Identifiers.Add(new MemberIdentifier
        {
            Type = MemberIdentifierType.Portal,
            System = FhirIdentifierSystems.PortalId,
            Value = "portal-uid"
        });

        var resp = await ctl.Remove("M-001", FhirIdentifierSystems.PortalId, "portal-uid", CancellationToken.None);
        resp.Should().BeOfType<NoContentResult>();
        repo.Members[0].Identifiers.Should().BeEmpty();
    }

    [Fact]
    public async Task Remove_Unknown_Returns404()
    {
        var (ctl, repo, _, _) = Build();
        SeedMember(repo);
        var resp = await ctl.Remove("M-001", "urn:cho:portal-id", "no-such-value", CancellationToken.None);
        resp.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Operations_AreTenantIsolated()
    {
        var (ctl, repo, _, _) = Build();
        repo.Members.Add(new Member
        {
            TenantId = "other-tenant",
            MemberId = "M-001",
            GroupNumber = "GRP", IsSubscriber = true,
            FirstName = "X", LastName = "Y",
            DateOfBirth = new DateTime(2000,1,1), EffectiveDate = new DateTime(2024,1,1)
        });

        var resp = await ctl.Add("M-001",
            new AddIdentifierRequest { Type = MemberIdentifierType.Portal, Value = "v" },
            CancellationToken.None);
        resp.Should().BeOfType<NotFoundResult>();
    }
}
