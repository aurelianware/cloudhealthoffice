using System.Diagnostics;
using CloudHealthOffice.Infrastructure.Observability;

namespace CloudHealthOffice.Infrastructure.Tests;

public sealed class PhiScrubbingSpanProcessorTests : IDisposable
{
    private readonly ActivitySource _source = new("cho-scrub-tests");
    private readonly ActivityListener _listener;
    private readonly PhiScrubbingSpanProcessor _processor = new("test-service");

    public PhiScrubbingSpanProcessorTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == _source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
    }

    private Activity StartActivity() => _source.StartActivity("op")!;

    [Theory]
    [InlineData("ssn")]
    [InlineData("social_security_number")]
    [InlineData("mbi")]
    [InlineData("dob")]
    [InlineData("member_id")]
    [InlineData("email")]
    [InlineData("password")]
    [InlineData("authorization")]
    public void OnEnd_DropsExactProhibitedAttribute(string key)
    {
        using var activity = StartActivity();
        activity.SetTag(key, "sensitive-value");

        _processor.OnEnd(activity);

        activity.GetTagItem(key).Should().BeNull();
    }

    [Theory]
    [InlineData("http.request.header.authorization")]
    [InlineData("db.user.password")]
    [InlineData("net.peer.ssn")]
    [InlineData("rpc.request.token")]
    [InlineData("messaging.headers.api_key")]
    [InlineData("custom.namespace.member_id")]
    public void OnEnd_DropsDotSuffixProhibitedAttribute_EvenUnderStandardOTelPrefix(string key)
    {
        // A prohibited suffix always wins — standard OTel namespaces like http.*
        // / db.* / net.* do NOT buy a blanket pass. This is the semantics the
        // Copilot review flagged: if we allowed http.* wholesale we'd leak
        // Authorization headers.
        using var activity = StartActivity();
        activity.SetTag(key, "sensitive-value");

        _processor.OnEnd(activity);

        activity.GetTagItem(key).Should().BeNull();
    }

    [Theory]
    [InlineData("SSN")]
    [InlineData("Authorization")]
    [InlineData("Http.Request.Header.AUTHORIZATION")]
    [InlineData("cho.Member_Id")]
    public void OnEnd_IsCaseInsensitive(string key)
    {
        using var activity = StartActivity();
        activity.SetTag(key, "sensitive-value");

        _processor.OnEnd(activity);

        activity.GetTagItem(key).Should().BeNull();
    }

    [Theory]
    [InlineData("cho.tenant_id", "t-1")]
    [InlineData("cho.claim_type", "professional")]
    [InlineData("cho.adjudication_step", "ncci-bundling")]
    [InlineData("http.method", "GET")]
    [InlineData("http.status_code", "200")]
    [InlineData("db.statement", "select 1")]
    [InlineData("net.peer.name", "claims-service")]
    [InlineData("custom.benign_attribute", "value")]
    public void OnEnd_PreservesNonProhibitedAttributes(string key, string value)
    {
        using var activity = StartActivity();
        activity.SetTag(key, value);

        _processor.OnEnd(activity);

        activity.GetTagItem(key).Should().Be(value);
    }

    [Fact]
    public void OnEnd_PreservesHashedMemberId_ButScrubsRaw()
    {
        // cho.member_id_hash must not collide with the member_id denylist entry —
        // its suffix after the final '.' is "member_id_hash", not "member_id".
        using var activity = StartActivity();
        activity.SetTag("cho.member_id_hash", "abc123def4567890");
        activity.SetTag("cho.member_id", "M-999");

        _processor.OnEnd(activity);

        activity.GetTagItem("cho.member_id_hash").Should().Be("abc123def4567890");
        activity.GetTagItem("cho.member_id").Should().BeNull();
    }

    [Fact]
    public void OnEnd_DropsMultipleProhibitedAttributesInOneSpan()
    {
        using var activity = StartActivity();
        activity.SetTag("ssn", "123-45-6789");
        activity.SetTag("cho.member_id", "M-1");
        activity.SetTag("http.request.header.authorization", "Bearer x");
        activity.SetTag("cho.tenant_id", "t-1"); // should survive

        _processor.OnEnd(activity);

        activity.GetTagItem("ssn").Should().BeNull();
        activity.GetTagItem("cho.member_id").Should().BeNull();
        activity.GetTagItem("http.request.header.authorization").Should().BeNull();
        activity.GetTagItem("cho.tenant_id").Should().Be("t-1");
    }

    [Fact]
    public void OnEnd_NoOp_WhenNoProhibitedTags()
    {
        using var activity = StartActivity();
        activity.SetTag("cho.tenant_id", "t-1");
        activity.SetTag("http.method", "GET");

        _processor.OnEnd(activity);

        activity.GetTagItem("cho.tenant_id").Should().Be("t-1");
        activity.GetTagItem("http.method").Should().Be("GET");
    }

    [Fact]
    public void OnEnd_HandlesActivityWithNoTags()
    {
        using var activity = StartActivity();

        var act = () => _processor.OnEnd(activity);

        act.Should().NotThrow();
    }
}
