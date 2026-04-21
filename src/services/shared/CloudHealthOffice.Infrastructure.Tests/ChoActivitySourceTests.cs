using System.Diagnostics;
using CloudHealthOffice.Infrastructure.Observability;

namespace CloudHealthOffice.Infrastructure.Tests;

public class ChoActivitySourceTests
{
    [Fact]
    public void Name_IsCloudHealthOffice()
    {
        ChoActivitySource.Name.Should().Be("CloudHealthOffice");
    }

    [Fact]
    public void Instance_HasCorrectName()
    {
        ChoActivitySource.Instance.Name.Should().Be("CloudHealthOffice");
    }

    [Fact]
    public void Instance_HasVersion()
    {
        ChoActivitySource.Instance.Version.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void HashIdentifier_ReturnsConsistentHash()
    {
        var hash1 = ChoActivitySource.HashIdentifier("member-123");
        var hash2 = ChoActivitySource.HashIdentifier("member-123");

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void HashIdentifier_ReturnsDifferentHashForDifferentIds()
    {
        var hash1 = ChoActivitySource.HashIdentifier("member-123");
        var hash2 = ChoActivitySource.HashIdentifier("member-456");

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void HashIdentifier_Returns16CharacterHex()
    {
        var hash = ChoActivitySource.HashIdentifier("any-member-id");

        hash.Should().HaveLength(16);
        hash.Should().MatchRegex("^[0-9a-f]{16}$");
    }

    [Fact]
    public void HashIdentifier_ReturnsLowercase()
    {
        var hash = ChoActivitySource.HashIdentifier("TEST-MEMBER");

        hash.Should().Be(hash.ToLowerInvariant());
    }

    [Fact]
    public void StartActivity_WithListener_SetsTenantTag()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ChoActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = ChoActivitySource.StartActivity("test-op", tenantId: "t-1");

        activity.Should().NotBeNull();
        activity!.GetTagItem("cho.tenant_id").Should().Be("t-1");
    }

    [Fact]
    public void StartActivity_WithListener_HashesClaimIdAndSetsClaimType()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ChoActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = ChoActivitySource.StartActivity(
            "adjudicate",
            claimId: "CLM-001",
            claimType: "professional");

        activity.Should().NotBeNull();
        activity!.GetTagItem("cho.claim_id").Should().BeNull("raw claim IDs must not be exported");
        activity.GetTagItem("cho.claim_id_hash").Should().Be(ChoActivitySource.HashIdentifier("CLM-001"));
        activity.GetTagItem("cho.claim_type").Should().Be("professional");
    }

    [Fact]
    public void StartActivity_WithListener_HashesMemberId()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ChoActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = ChoActivitySource.StartActivity("test", memberId: "MBR-12345");

        activity.Should().NotBeNull();
        var tagValue = activity!.GetTagItem("cho.member_id_hash")?.ToString();
        tagValue.Should().NotBeNull();
        tagValue.Should().NotBe("MBR-12345", "member ID should be hashed, not stored as plaintext");
        tagValue.Should().Be(ChoActivitySource.HashIdentifier("MBR-12345"));
    }

    [Fact]
    public void StartActivity_WithListener_OmitsNullTags()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ChoActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = ChoActivitySource.StartActivity("test");

        activity.Should().NotBeNull();
        activity!.GetTagItem("cho.tenant_id").Should().BeNull();
        activity.GetTagItem("cho.claim_id").Should().BeNull();
        activity.GetTagItem("cho.claim_id_hash").Should().BeNull();
        activity.GetTagItem("cho.claim_type").Should().BeNull();
        activity.GetTagItem("cho.member_id_hash").Should().BeNull();
    }

    [Fact]
    public void StartActivity_UsesSpecifiedKind()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ChoActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = ChoActivitySource.StartActivity("server-op", ActivityKind.Server);

        activity.Should().NotBeNull();
        activity!.Kind.Should().Be(ActivityKind.Server);
    }

    [Fact]
    public void StartActivity_DefaultKind_IsInternal()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ChoActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = ChoActivitySource.StartActivity("internal-op");

        activity.Should().NotBeNull();
        activity!.Kind.Should().Be(ActivityKind.Internal);
    }
}
