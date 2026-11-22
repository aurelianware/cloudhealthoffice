using FluentAssertions;
using SmartAuthService.Models;
using Xunit;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// Every v1 resource scope is also issued in its exact SMART v2 form, so a
/// SMART 2.x app asking for patient/*.rs gets the same grant a v1 app gets
/// for patient/*.read.
/// </summary>
public class SmartScopeV2FormTests
{
    [Theory]
    [InlineData("patient/*.read", "patient/*.rs")]
    [InlineData("user/Claim.write", "user/Claim.cud")]
    [InlineData("system/PayerToPayer.read", "system/PayerToPayer.rs")]
    public void AV1ResourceScope_HasItsExactV2Form(string v1, string v2)
    {
        SmartScopes.V2Form(v1).Should().Be(v2);
        SmartScopes.Registered.Should().Contain([v1, v2]);
    }

    [Theory]
    [InlineData("openid")]
    [InlineData("launch/patient")]
    [InlineData("fhirUser")]
    public void ANonResourceScope_HasNoV2Form(string scope)
        => SmartScopes.V2Form(scope).Should().BeNull();

    [Fact]
    public void TheCatalog_NamesEachScopeOnce()
        => SmartScopes.Catalog.Select(c => c.Name).Should().OnlyHaveUniqueItems();
}
