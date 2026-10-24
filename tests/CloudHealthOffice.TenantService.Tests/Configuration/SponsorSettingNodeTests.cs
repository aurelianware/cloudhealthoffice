using CloudHealthOffice.PlanConfiguration;

namespace CloudHealthOffice.TenantService.Tests.Configuration;

/// <summary>
/// The node names a consumer has to build for itself
/// (plan-configuration.md §12.16).
///
/// <para>
/// These are literal-string assertions on purpose. sponsor-service owns both
/// formats and publishes no contracts package, so nothing here can call its
/// builder; pinning the exact strings is what makes a change on either side
/// fail loudly instead of quietly resolving every policy-level setting to the
/// tenant default. If one of these fails, check
/// <c>SponsorPolicy.IdFor</c> in sponsor-service before changing the test.
/// </para>
/// </summary>
public class SponsorSettingNodeTests
{
    [Fact]
    public void A_policy_node_is_tenant_group_and_policy_number()
    {
        Assert.Equal(
            "t1|GRP0001|POL-9",
            SponsorSettingNodes.ForPolicy("t1", "GRP0001", "POL-9"));
    }

    [Fact]
    public void The_policy_number_is_upper_cased_the_way_sponsor_service_stores_it()
    {
        // sponsor-service normalises a policy number with Trim().ToUpperInvariant()
        // before building the id, so a lower-case one from a coverage record
        // has to be folded the same way or it names a node that does not exist.
        Assert.Equal(
            "t1|GRP0001|POL-9",
            SponsorSettingNodes.ForPolicy("t1", "GRP0001", "  pol-9 "));
    }

    [Fact]
    public void A_group_number_keeps_its_case()
    {
        // Unlike the policy number, sponsor-service does not fold a group
        // number's case. Folding it here would stop matching values already
        // stored against a mixed-case group.
        Assert.Equal("t1|Grp0001|POL-9", SponsorSettingNodes.ForPolicy("t1", "Grp0001", "POL-9"));
        Assert.Equal("Grp0001", SponsorSettingNodes.ForSponsor(" Grp0001 "));
    }

    [Theory]
    [InlineData("", "GRP0001", "POL-9")]
    [InlineData("t1", "", "POL-9")]
    [InlineData("t1", "GRP0001", "")]
    [InlineData("t1", "GRP0001", "   ")]
    public void A_policy_node_with_a_part_missing_is_no_node_at_all(
        string tenantId, string? groupNumber, string? policyNumber)
    {
        // A half-built id matches nothing while looking like a real scope, so
        // it must be absent rather than partial: an absent policy level
        // resolves at the sponsor, a wrong one resolves at the tenant.
        Assert.Null(SponsorSettingNodes.ForPolicy(tenantId, groupNumber, policyNumber));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_sponsor_with_no_group_number_is_no_node_either(string? groupNumber)
    {
        Assert.Null(SponsorSettingNodes.ForSponsor(groupNumber));
    }
}
