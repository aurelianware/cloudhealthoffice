using System.Net;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests.Tenancy;

/// <summary>
/// The one rule claims, benefit-plan, provider, eligibility and id-card use to
/// read a tenant's platform from tenant-service.
/// </summary>
public class TenantPlatformLookupTests
{
    private static Task<TenantPlatformLookupResult> Fetch(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        Func<JsonElement, (string, Dictionary<string, string>)?>? alternate = null)
        => TenantPlatformLookup.FetchAsync(
            new HttpClient(new Handler(respond)), "http://tenant-service/api/v1", "tenant-1", "providerPlatform",
            NullLogger.Instance, alternateParser: alternate);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task ConfiguredPlatform_IsReturnedWithSettings_AndCacheable()
    {
        var result = await Fetch(_ => Json(
            """{"configuration":{"providerPlatform":{"platform":"qnxt","platformSettings":{"a":"1","n":2}}}}"""));

        result.Outcome.Should().Be(TenantPlatformOutcome.Configured);
        result.Platform.Should().Be("qnxt");
        result.Settings.Should().Contain("a", "1").And.Contain("n", "2");
        result.IsCacheable.Should().BeTrue();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"configuration":{}}""")]
    [InlineData("""{"configuration":{"providerPlatform":null}}""")]
    [InlineData("""{"configuration":{"providerPlatform":{"platform":""}}}""")]
    public async Task AnswerWithoutPlatform_IsTheDefault_AndCacheable(string body)
    {
        var result = await Fetch(_ => Json(body));

        result.Outcome.Should().Be(TenantPlatformOutcome.NotConfigured);
        result.Platform.Should().Be("cho");
        result.IsCacheable.Should().BeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Refusal_IsReported_NotCacheable(HttpStatusCode status)
    {
        var result = await Fetch(_ => new HttpResponseMessage(status));

        result.Outcome.Should().Be(TenantPlatformOutcome.Refused);
        result.StatusCode.Should().Be(status);
        result.IsCacheable.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task NonSuccess_IsUnavailable_DefaultNotCacheable(HttpStatusCode status)
    {
        var result = await Fetch(_ => new HttpResponseMessage(status));

        result.Outcome.Should().Be(TenantPlatformOutcome.Unavailable);
        result.Platform.Should().Be("cho");
        result.IsCacheable.Should().BeFalse();
    }

    [Fact]
    public async Task TransportFailureOrUnreadableBody_IsUnavailable()
    {
        (await Fetch(_ => throw new HttpRequestException("down"))).Outcome.Should().Be(TenantPlatformOutcome.Unavailable);
        (await Fetch(_ => Json("not json"))).Outcome.Should().Be(TenantPlatformOutcome.Unavailable);
    }

    [Fact]
    public async Task Request_NamesTheTenant_AndEncodesIt()
    {
        HttpRequestMessage? seen = null;
        await TenantPlatformLookup.FetchAsync(
            new HttpClient(new Handler(r => { seen = r; return Json("{}"); })),
            "http://tenant-service/api/v1/", "a/b?c", "claimsPlatform", NullLogger.Instance);

        seen!.RequestUri!.AbsoluteUri.Should().Be("http://tenant-service/api/v1/tenants/a%2Fb%3Fc");
        seen.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be("a/b?c");
    }

    [Fact]
    public async Task AlternateShape_IsUsedWhenTheCanonicalBlockIsAbsent()
    {
        var result = await Fetch(
            _ => Json("""{"configuration":{"customSettings":{"providerPlatform":"facets"}}}"""),
            config => config.GetProperty("customSettings").TryGetProperty("providerPlatform", out var p)
                ? (p.GetString()!, new Dictionary<string, string>())
                : null);

        result.Outcome.Should().Be(TenantPlatformOutcome.Configured);
        result.Platform.Should().Be("facets");
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }
}
