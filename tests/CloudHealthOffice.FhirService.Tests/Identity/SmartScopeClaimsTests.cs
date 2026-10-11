using System.Security.Claims;
using FhirService.Services.Identity;
using FluentAssertions;
using Xunit;

namespace CloudHealthOffice.FhirService.Tests.Identity;

/// <summary>
/// D-32: the one scope parser. See <see cref="SmartScopeClaims"/> for the rules;
/// the HTTP-level cases are in Security/SmartScpClaimTests.
/// </summary>
public class SmartScopeClaimsTests
{
    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Bearer"));

    [Fact]
    public void ScopeOnly_IsItsSet()
        => SmartScopeClaims.Read(Principal(("scope", "patient/*.read  openid ")))
            .Should().Match<SmartScopeClaims.Result>(r =>
                !r.Conflict && r.Scopes.SetEquals(new[] { "patient/*.read", "openid" }));

    [Fact]
    public void ScpOnly_StringOrArray_IsItsSet()
    {
        var asString = SmartScopeClaims.Read(Principal(("scp", "system/*.read system/Claim.write")));
        var asArray = SmartScopeClaims.Read(Principal(("scp", "system/Claim.write"), ("scp", "system/*.read")));

        asString.Conflict.Should().BeFalse();
        asString.Scopes.Should().BeEquivalentTo(["system/*.read", "system/Claim.write"]);
        asArray.Scopes.Should().BeEquivalentTo(asString.Scopes);
    }

    [Theory]
    [InlineData("a b", "b a")]
    [InlineData("a  b ", " a b")]
    [InlineData("a a b", "b a")]
    public void BothClaims_SameSet_Agree(string scope, string scp)
    {
        var result = SmartScopeClaims.Read(Principal(("scope", scope), ("scp", scp)));

        result.Conflict.Should().BeFalse();
        result.Scopes.Should().BeEquivalentTo(["a", "b"]);
    }

    [Theory]
    [InlineData("a", "a b")]
    [InlineData("a b", "a")]
    [InlineData("a", "b")]
    [InlineData("a", "A")]
    [InlineData("", "a")]
    [InlineData("a", "")]
    public void BothClaims_DifferentSets_Conflict_AndGrantNothing(string scope, string scp)
    {
        var result = SmartScopeClaims.Read(Principal(("scope", scope), ("scp", scp)));

        result.Conflict.Should().BeTrue();
        result.Scopes.Should().BeEmpty("neither the union nor the intersection is granted");
    }

    [Fact]
    public void BothClaimsEmpty_Agree_AndGrantNothing()
    {
        var result = SmartScopeClaims.Read(Principal(("scope", ""), ("scp", " ")));
        result.Conflict.Should().BeFalse();
        result.Scopes.Should().BeEmpty();
    }

    [Fact]
    public void NeitherClaim_GrantsNothing()
    {
        var result = SmartScopeClaims.Read(Principal(("sub", "x")));
        result.Conflict.Should().BeFalse();
        result.Scopes.Should().BeEmpty();
    }

    [Fact]
    public void TheMappedWsScopeClaim_IsNotAScopeClaim()
        // With inbound mapping off nothing produces it; a token that writes
        // it literally does not get it read as a grant.
        => SmartScopeClaims.Parse(Principal(("http://schemas.microsoft.com/identity/claims/scope", "system/*.read")))
            .Should().BeEmpty();
}
