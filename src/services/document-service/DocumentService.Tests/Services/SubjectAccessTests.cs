using CloudHealthOffice.Infrastructure.Security;
using DocumentService.Models;
using DocumentService.Services;

namespace DocumentService.Tests.Services;

public class SubjectAccessTests
{
    private sealed class Actor(params string[] roles) : ICurrentActor
    {
        private readonly HashSet<string> _permissions = ChoRolePermissions.Expand(roles);
        public bool IsAuthenticated => true;
        public string UserId => "u";
        public string? DisplayName => null;
        public string? Email => null;
        public string TenantId => "t";
        public bool IsService => false;
        public IReadOnlyCollection<string> Roles => roles;
        public bool HasPermission(string permission) => ChoRolePermissions.Satisfies(_permissions, permission);
    }

    private static readonly DocumentLink Claim1 = new() { Kind = DocumentLinkKind.Claim, Id = "CLM-1" };

    private static CatalogDocument Doc(params string[] sensitivity) => new()
    {
        Id = "d",
        Links = [Claim1],
        Sensitivity = sensitivity.ToList()
    };

    [Theory]
    [InlineData("Claim:CLM-1", DocumentLinkKind.Claim, "CLM-1")]
    [InlineData("claimline:CLM-1:2", DocumentLinkKind.ClaimLine, "CLM-1:2")]
    [InlineData(" Appeal : A-9 ", DocumentLinkKind.Appeal, "A-9")]
    public void TryParseLink_AcceptsKindColonId(string value, DocumentLinkKind kind, string id)
    {
        Assert.True(SubjectAccess.TryParseLink(value, out var link));
        Assert.Equal(new DocumentLink { Kind = kind, Id = id }, link);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Claim")]
    [InlineData("Claim:")]
    [InlineData(":CLM-1")]
    [InlineData("1:CLM-1")]        // an enum number is not a kind
    [InlineData("Unicorn:1")]
    [InlineData("Claim:a b")]
    [InlineData("Claim:../x")]
    [InlineData("Claim:a/b")]
    public void TryParseLink_RejectsAnythingElse(string? value) =>
        Assert.False(SubjectAccess.TryParseLink(value, out _));

    [Fact]
    public void EveryLinkKind_HasAReadPermission()
    {
        foreach (var kind in Enum.GetValues<DocumentLinkKind>())
            Assert.Contains(':', SubjectAccess.ReadPermissionFor(kind));
    }

    [Fact]
    public void CallDocuments_UseTheSameReadPermissionAsContacts()
    {
        Assert.True(SubjectAccess.CanReadSubject(new Actor(ChoRolePermissions.MemberServices), DocumentLinkKind.Call));
        Assert.True(SubjectAccess.CanReadSubject(new Actor(ChoRolePermissions.ProviderRelations), DocumentLinkKind.Call));
        Assert.False(SubjectAccess.CanReadSubject(new Actor(ChoRolePermissions.ClaimsExaminer), DocumentLinkKind.Call));
    }

    [Theory]
    [InlineData("TenantAdmin")]
    [InlineData("PlatformAdmin")]
    [InlineData("ClaimsSupervisor")]
    public void SensitiveDocuments_AreNotOpenedByWildcardsOrBroadRoles(string role)
    {
        var doc = Doc(SensitivityTags.BehavioralHealth);
        Assert.False(SubjectAccess.DecideSensitivity(new Actor(role), doc).Allowed, role);
    }

    [Fact]
    public void Decide_NeedsTheSubjectsReadPermission_AndTheLink()
    {
        Assert.True(SubjectAccess.Decide(new Actor(ChoRolePermissions.ClaimsExaminer), Doc(), Claim1).Allowed);
        Assert.False(SubjectAccess.Decide(new Actor(ChoRolePermissions.EnrollmentSpecialist), Doc(), Claim1).Allowed);
        Assert.False(SubjectAccess.Decide(new Actor(ChoRolePermissions.ClaimsExaminer), Doc(),
            new DocumentLink { Kind = DocumentLinkKind.Claim, Id = "CLM-2" }).Allowed);
    }

    [Fact]
    public void Sensitive_NeedsReadSensitive_WhichStarReadDoesNotGrant()
    {
        var doc = Doc(SensitivityTags.Hiv);
        Assert.False(SubjectAccess.Decide(new Actor(ChoRolePermissions.ClaimsExaminer), doc, Claim1).Allowed);
        Assert.True(SubjectAccess.Decide(new Actor(ChoRolePermissions.UMCoordinator), doc, Claim1).Allowed);

        // No wildcard reaches documents:read-sensitive; only the exact grant.
        Assert.False(ChoRolePermissions.Matches("*:read", ChoRolePermissions.DocumentsReadSensitive));
        Assert.False(ChoRolePermissions.Matches("*:*", ChoRolePermissions.DocumentsReadSensitive));
        Assert.False(ChoRolePermissions.Matches("documents:*", ChoRolePermissions.DocumentsReadSensitive));
        Assert.True(ChoRolePermissions.Matches(ChoRolePermissions.DocumentsReadSensitive, ChoRolePermissions.DocumentsReadSensitive));
    }

    [Fact]
    public void Part2_IsWithheldEvenFromTenantAdmins()
    {
        var decision = SubjectAccess.Decide(new Actor(ChoRolePermissions.TenantAdmin), Doc(SensitivityTags.Part2Sud), Claim1);
        Assert.False(decision.Allowed);
        Assert.Contains("Part 2", decision.Reason);
    }
}
