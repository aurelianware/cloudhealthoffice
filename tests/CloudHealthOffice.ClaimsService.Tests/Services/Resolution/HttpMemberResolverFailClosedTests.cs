using System.Net;
using System.Text;
using ClaimsService.Services.Resolution;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Resolution;

/// <summary>
/// Eligibility fails closed: member-service's 404 is a definitive "no such
/// member" (null); anything else that is not an answer throws
/// <see cref="MemberUnavailableException"/> so adjudication pends instead of
/// treating the member as eligible.
/// </summary>
public class HttpMemberResolverFailClosedTests
{
    private sealed class Stub(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(answer());
    }

    private sealed class Throwing(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw ex;
    }

    private static HttpMemberResolver Resolver(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(HttpMemberResolver.HttpClientName, c => c.BaseAddress = new Uri("http://member-service/"))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        return new HttpMemberResolver(provider.GetRequiredService<IHttpClientFactory>(), NullLogger<HttpMemberResolver>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task AFoundMember_IsReturned()
    {
        var member = await Resolver(new Stub(() => Json(HttpStatusCode.OK, """{"memberId":"M1","status":"Active"}""")))
            .GetMemberAsync("t1", "M1");

        member!.MemberId.Should().Be("M1");
    }

    [Fact]
    public async Task NotFound_IsADefinitiveNull()
    {
        (await Resolver(new Stub(() => new HttpResponseMessage(HttpStatusCode.NotFound))).GetMemberAsync("t1", "M1"))
            .Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task AnyOtherStatus_IsUnavailable_NotNull(HttpStatusCode status)
    {
        var act = () => Resolver(new Stub(() => new HttpResponseMessage(status))).GetMemberAsync("t1", "M1");

        await act.Should().ThrowAsync<MemberUnavailableException>();
    }

    [Fact]
    public async Task AnUnreadableOrEmptyBody_IsUnavailable()
    {
        await FluentActions.Awaiting(() => Resolver(new Stub(() => Json(HttpStatusCode.OK, "{not json"))).GetMemberAsync("t1", "M1"))
            .Should().ThrowAsync<MemberUnavailableException>();
        await FluentActions.Awaiting(() => Resolver(new Stub(() => Json(HttpStatusCode.OK, "null"))).GetMemberAsync("t1", "M1"))
            .Should().ThrowAsync<MemberUnavailableException>();
    }

    [Fact]
    public async Task ATransportErrorOrTimeout_IsUnavailable()
    {
        await FluentActions.Awaiting(() => Resolver(new Throwing(new HttpRequestException("refused"))).GetMemberAsync("t1", "M1"))
            .Should().ThrowAsync<MemberUnavailableException>();
        await FluentActions.Awaiting(() => Resolver(new Throwing(new TaskCanceledException("timeout"))).GetMemberAsync("t1", "M1"))
            .Should().ThrowAsync<MemberUnavailableException>();
    }

    [Fact]
    public async Task TheCache_NeverRemembersAnOutage()
    {
        var calls = 0;
        var inner = new Stub(() => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Json(HttpStatusCode.OK, """{"memberId":"M1","status":"Active"}"""));
        var cached = new CachingMemberResolver(Resolver(inner), new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

        await FluentActions.Awaiting(() => cached.GetMemberAsync("t1", "M1")).Should().ThrowAsync<MemberUnavailableException>();
        (await cached.GetMemberAsync("t1", "M1"))!.MemberId.Should().Be("M1");
    }
}
