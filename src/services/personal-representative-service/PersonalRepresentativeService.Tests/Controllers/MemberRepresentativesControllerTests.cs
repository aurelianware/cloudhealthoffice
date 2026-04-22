using System.Security.Claims;
using PersonalRepresentativeService.Controllers;
using PersonalRepresentativeService.Models;
using PersonalRepresentativeService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace PersonalRepresentativeService.Tests.Controllers;

public class MemberRepresentativesControllerTests
{
    private static (PersonalRepresentativesController primary,
                    MemberRepresentativesController resolver,
                    InMemoryPersonalRepRepository repo,
                    ReversiblePersonalRepFieldEncryptor encryptor)
        BuildControllers(string tenantId = "tenant-a")
    {
        var repo = new InMemoryPersonalRepRepository();
        var publisher = new RecordingPersonalRepEventPublisher();
        var encryptor = new ReversiblePersonalRepFieldEncryptor();

        var primary = new PersonalRepresentativesController(repo, repo, encryptor, publisher);
        var resolver = new MemberRepresentativesController(repo, encryptor);

        foreach (var c in new ControllerBase[] { primary, resolver })
        {
            var http = new DefaultHttpContext();
            http.Items["TenantId"] = tenantId;
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "alice@tenant.com") }, "test"));
            c.ControllerContext = new ControllerContext { HttpContext = http };
        }
        return (primary, resolver, repo, encryptor);
    }

    [Fact]
    public async Task ListAll_ReturnsSummariesForMember()
    {
        var (primary, resolver, _, _) = BuildControllers();

        var create1 = await primary.CreateRepresentative(new CreatePersonalRepRequest
        {
            CredentialType = PersonalRepCredentialType.LegalGuardian,
            FirstName = "Alice", LastName = "Smith"
        }, CancellationToken.None);
        var id1 = ((PersonalRepresentative)((CreatedAtActionResult)create1).Value!).Id;
        await primary.Activate(id1, null, CancellationToken.None);
        await primary.AddAssociation(id1,
            new AddAssociationRequest { MemberId = "M123" }, CancellationToken.None);

        var result = await resolver.ListAll("M123", asOf: null, CancellationToken.None);
        var response = ((OkObjectResult)result).Value.Should().BeOfType<MemberRepresentativesResponse>().Subject;
        response.Items.Should().ContainSingle(s =>
            s.PersonalRepId == id1 &&
            s.DisplayName == "Alice Smith" &&
            s.CredentialType == PersonalRepCredentialType.LegalGuardian);
    }

    [Fact]
    public async Task ListActive_FiltersInactiveReps_AndRespectsCredentialTypeFilter()
    {
        var (primary, resolver, _, _) = BuildControllers();

        // Rep 1: Parent, active.
        var c1 = await primary.CreateRepresentative(new CreatePersonalRepRequest
        {
            CredentialType = PersonalRepCredentialType.Parent, FirstName = "Bob", LastName = "Parent"
        }, CancellationToken.None);
        var id1 = ((PersonalRepresentative)((CreatedAtActionResult)c1).Value!).Id;
        await primary.Activate(id1, null, CancellationToken.None);
        await primary.AddAssociation(id1, new AddAssociationRequest { MemberId = "M123" }, CancellationToken.None);

        // Rep 2: LegalGuardian, active.
        var c2 = await primary.CreateRepresentative(new CreatePersonalRepRequest
        {
            CredentialType = PersonalRepCredentialType.LegalGuardian, FirstName = "Carol", LastName = "Guardian"
        }, CancellationToken.None);
        var id2 = ((PersonalRepresentative)((CreatedAtActionResult)c2).Value!).Id;
        await primary.Activate(id2, null, CancellationToken.None);
        await primary.AddAssociation(id2, new AddAssociationRequest { MemberId = "M123" }, CancellationToken.None);

        // Rep 3: LegalGuardian, revoked — must NOT appear on /active.
        var c3 = await primary.CreateRepresentative(new CreatePersonalRepRequest
        {
            CredentialType = PersonalRepCredentialType.LegalGuardian, FirstName = "Dan", LastName = "Revoked"
        }, CancellationToken.None);
        var id3 = ((PersonalRepresentative)((CreatedAtActionResult)c3).Value!).Id;
        await primary.Activate(id3, null, CancellationToken.None);
        await primary.AddAssociation(id3, new AddAssociationRequest { MemberId = "M123" }, CancellationToken.None);
        await primary.Revoke(id3, null, CancellationToken.None);

        // No credential-type filter — two active reps.
        var all = await resolver.ListActive("M123", asOf: null, credentialTypes: null, CancellationToken.None);
        var allResp = ((OkObjectResult)all).Value.Should().BeOfType<MemberRepresentativesResponse>().Subject;
        allResp.Items.Select(i => i.PersonalRepId).Should().BeEquivalentTo(new[] { id1, id2 });
        allResp.Items.Should().NotContain(i => i.PersonalRepId == id3);

        // Credential-type filter narrows to LegalGuardian only.
        var guardianOnly = await resolver.ListActive("M123", asOf: null,
            credentialTypes: new List<PersonalRepCredentialType> { PersonalRepCredentialType.LegalGuardian },
            CancellationToken.None);
        var guardianResp = ((OkObjectResult)guardianOnly).Value.Should().BeOfType<MemberRepresentativesResponse>().Subject;
        guardianResp.Items.Select(i => i.PersonalRepId).Should().BeEquivalentTo(new[] { id2 });
    }

    /// <summary>
    /// The resolver endpoint must use the same
    /// <c>IPersonalRepFieldEncryptor.DecryptAsync</c> path the primary
    /// controller uses — no shortcut decrypt. Verified by hitting the
    /// recording encryptor's DecryptCalls counter.
    /// </summary>
    [Fact]
    public async Task ListActive_DisplayName_UsesStandardDecryptPath()
    {
        var (primary, resolver, _, encryptor) = BuildControllers();

        var create = await primary.CreateRepresentative(new CreatePersonalRepRequest
        {
            CredentialType = PersonalRepCredentialType.LegalGuardian,
            FirstName = "Eve", LastName = "Decryptable"
        }, CancellationToken.None);
        var id = ((PersonalRepresentative)((CreatedAtActionResult)create).Value!).Id;
        await primary.Activate(id, null, CancellationToken.None);
        await primary.AddAssociation(id, new AddAssociationRequest { MemberId = "M1" }, CancellationToken.None);

        var before = encryptor.DecryptCalls;
        var result = await resolver.ListActive("M1", asOf: null, credentialTypes: null, CancellationToken.None);
        var response = ((OkObjectResult)result).Value.Should().BeOfType<MemberRepresentativesResponse>().Subject;

        var summary = response.Items.Single();
        summary.DisplayName.Should().Be("Eve Decryptable");

        // The resolver decrypts FirstName + LastName — at minimum two more
        // DecryptAsync calls should have happened.
        (encryptor.DecryptCalls - before).Should().BeGreaterOrEqualTo(2,
            "resolver must go through the standard IPersonalRepFieldEncryptor.DecryptAsync path, not a shortcut");
    }

    [Fact]
    public async Task ListActive_LightweightSummary_OmitsPhoneAddressNotes()
    {
        var (primary, resolver, _, _) = BuildControllers();

        var create = await primary.CreateRepresentative(new CreatePersonalRepRequest
        {
            CredentialType = PersonalRepCredentialType.LegalGuardian,
            FirstName = "Alice",
            LastName = "Smith",
            PhoneNumber = "555-0100",
            MailingAddressLine1 = "100 Main St",
            RelationshipNotes = "sensitive notes"
        }, CancellationToken.None);
        var id = ((PersonalRepresentative)((CreatedAtActionResult)create).Value!).Id;
        await primary.Activate(id, null, CancellationToken.None);
        await primary.AddAssociation(id, new AddAssociationRequest { MemberId = "M1" }, CancellationToken.None);

        var result = await resolver.ListActive("M1", asOf: null, credentialTypes: null, CancellationToken.None);
        var summary = ((MemberRepresentativesResponse)((OkObjectResult)result).Value!).Items.Single();

        // The PersonalRepSummary shape intentionally has no phone / address
        // / notes. A reflection check locks this down against future
        // accidental field additions that would widen PHI disclosure.
        var propNames = typeof(PersonalRepSummary).GetProperties().Select(p => p.Name).ToHashSet();
        propNames.Should().NotContain("PhoneNumber");
        propNames.Should().NotContain("MailingAddressLine1");
        propNames.Should().NotContain("Email");
        propNames.Should().NotContain("RelationshipNotes");
    }

    [Fact]
    public async Task ListActive_AsOfFilter_RespectsPointInTime()
    {
        var (primary, resolver, _, _) = BuildControllers();

        var create = await primary.CreateRepresentative(new CreatePersonalRepRequest
        {
            CredentialType = PersonalRepCredentialType.LegalGuardian,
            FirstName = "Alice", LastName = "Smith"
        }, CancellationToken.None);
        var id = ((PersonalRepresentative)((CreatedAtActionResult)create).Value!).Id;
        await primary.Activate(id, null, CancellationToken.None);

        var inPast = DateTime.UtcNow.AddDays(-365);
        var addReq = new AddAssociationRequest
        {
            MemberId = "M1",
            EffectiveFrom = DateTime.UtcNow.AddDays(-30),
            EffectiveTo = DateTime.UtcNow.AddDays(-1)
        };
        await primary.AddAssociation(id, addReq, CancellationToken.None);

        // asOf BEFORE the association began → no matches.
        var past = await resolver.ListActive("M1", asOf: inPast, credentialTypes: null, CancellationToken.None);
        ((MemberRepresentativesResponse)((OkObjectResult)past).Value!).Items.Should().BeEmpty();

        // asOf NOW → association is past its EffectiveTo → still empty.
        var now = await resolver.ListActive("M1", asOf: null, credentialTypes: null, CancellationToken.None);
        ((MemberRepresentativesResponse)((OkObjectResult)now).Value!).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolver_CrossTenant_Returns404Equivalent_EmptyList()
    {
        // Cross-tenant reads must NOT leak another tenant's reps. Because
        // the list endpoint returns 200 with an empty items list on any
        // absent member (rather than 404), tenant isolation is asserted
        // as "the items list is empty when querying under the wrong
        // tenant context." 404 semantics on /{repId} are covered by
        // PersonalRepresentativesControllerTests.
        var (primaryA, _, repoA, _) = BuildControllers(tenantId: "tenant-a");
        var create = await primaryA.CreateRepresentative(new CreatePersonalRepRequest
        {
            CredentialType = PersonalRepCredentialType.Parent, FirstName = "Alice"
        }, CancellationToken.None);
        var id = ((PersonalRepresentative)((CreatedAtActionResult)create).Value!).Id;
        await primaryA.Activate(id, null, CancellationToken.None);
        await primaryA.AddAssociation(id, new AddAssociationRequest { MemberId = "M1" }, CancellationToken.None);

        var resolverB = new MemberRepresentativesController(repoA,
            new ReversiblePersonalRepFieldEncryptor());
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = "tenant-b";
        http.User = new ClaimsPrincipal(new ClaimsIdentity());
        resolverB.ControllerContext = new ControllerContext { HttpContext = http };

        var result = await resolverB.ListActive("M1", asOf: null, credentialTypes: null, CancellationToken.None);
        ((MemberRepresentativesResponse)((OkObjectResult)result).Value!).Items.Should().BeEmpty();
    }
}
