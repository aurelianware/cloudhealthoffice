using Microsoft.Extensions.Logging.Abstractions;
using PersonalRepresentativeService.Models;
using PersonalRepresentativeService.Services;

namespace PersonalRepresentativeService.Tests.Services;

public class PersonalRepActivationControlsUnitTests
{
    [Theory]
    [InlineData(PersonalRepCredentialType.LegalGuardian, true)]
    [InlineData(PersonalRepCredentialType.HealthcarePowerOfAttorney, true)]
    [InlineData(PersonalRepCredentialType.HealthcareSurrogate, true)]
    [InlineData(PersonalRepCredentialType.Parent, false)]
    [InlineData(PersonalRepCredentialType.Conservator, false)]
    [InlineData(PersonalRepCredentialType.Other, false)]
    public void RequiresProofOfAuthority_ByCredentialType(PersonalRepCredentialType type, bool required)
        => PersonalRepActivationControls.RequiresProofOfAuthority(type).Should().Be(required);

    [Theory]
    [InlineData("""{"configuration":{"personalRepresentativeControls":{"requireSecondPerson":false}}}""", false)]
    [InlineData("""{"configuration":{"personalRepresentativeControls":{"requireSecondPerson":true}}}""", true)]
    [InlineData("""{"configuration":{"personalRepresentativeControls":{}}}""", true)]
    [InlineData("""{"configuration":{"personalRepresentativeControls":{"requireSecondPerson":"false"}}}""", true)]
    [InlineData("""{"configuration":{"personalRepresentativeControls":null}}""", true)]
    [InlineData("""{"configuration":{"paymentControls":{"enforceSeparationOfDuties":false}}}""", true)]
    [InlineData("""{"configuration":null}""", true)]
    [InlineData("""{}""", true)]
    [InlineData("""[]""", true)]
    public void ParseRequired_OnlyExplicitFalseTurnsTheRuleOff(string tenantJson, bool required)
        => TenantPersonalRepControls.ParseRequired(tenantJson).Should().Be(required);

    [Fact]
    public async Task TenantControls_UnreadableBody_FailsClosed()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(TenantPersonalRepControls.HttpClientName))
            .Returns(() => new HttpClient(new Fakes.ResponderHandler(_ =>
                new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("not json") }))
            { BaseAddress = new Uri("http://tenant-service") });
        var controls = new TenantPersonalRepControls(factory.Object, NullLogger<TenantPersonalRepControls>.Instance);

        (await controls.IsSecondPersonRequiredAsync("tenant-x")).Should().BeTrue();
    }

    private static string Doc(string tenant = "t1", string id = "d1", string member = "M1",
        string related = "[]", string pending = "null")
        => $$"""{"id":"{{id}}","tenantId":"{{tenant}}","memberId":"{{member}}","relatedMemberIds":{{related}},"pendingUploadBlobPath":{{pending}}}""";

    [Fact]
    public void Evaluate_Verified_WhenOwnerOrRelatedCoversEveryMember()
        => MemberDocumentProofOfAuthorityDocuments.Evaluate(Doc(related: """["M2"]"""), "t1", "d1", new[] { "M1", "M2" })
            .Status.Should().Be(ProofOfAuthorityDocumentStatus.Verified);

    [Fact]
    public void Evaluate_WrongTenant()
        => MemberDocumentProofOfAuthorityDocuments.Evaluate(Doc(tenant: "t2"), "t1", "d1", new[] { "M1" })
            .Status.Should().Be(ProofOfAuthorityDocumentStatus.WrongTenant);

    [Fact]
    public void Evaluate_MissingTenant_IsWrongTenant()
        => MemberDocumentProofOfAuthorityDocuments.Evaluate("""{"id":"d1","memberId":"M1"}""", "t1", "d1", new[] { "M1" })
            .Status.Should().Be(ProofOfAuthorityDocumentStatus.WrongTenant);

    [Fact]
    public void Evaluate_OtherId_IsNotFound()
        => MemberDocumentProofOfAuthorityDocuments.Evaluate(Doc(id: "d2"), "t1", "d1", new[] { "M1" })
            .Status.Should().Be(ProofOfAuthorityDocumentStatus.NotFound);

    [Fact]
    public void Evaluate_NotLinked_NamesTheMember()
    {
        var check = MemberDocumentProofOfAuthorityDocuments.Evaluate(Doc(), "t1", "d1", new[] { "M1", "M3" });
        check.Status.Should().Be(ProofOfAuthorityDocumentStatus.NotLinkedToMember);
        check.UnlinkedMemberId.Should().Be("M3");
    }

    [Fact]
    public void Evaluate_PendingUpload_IsNotFinalized()
        => MemberDocumentProofOfAuthorityDocuments.Evaluate(Doc(pending: "\"t1/staging/d1\""), "t1", "d1", new[] { "M1" })
            .Status.Should().Be(ProofOfAuthorityDocumentStatus.NotFinalized);
}
