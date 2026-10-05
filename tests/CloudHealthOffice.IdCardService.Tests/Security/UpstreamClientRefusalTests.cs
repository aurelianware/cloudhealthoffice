using System.Net;
using IdCardService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CloudHealthOffice.IdCardService.Tests.Security;

/// <summary>
/// A 401/403 from a CHO service means this service could not authenticate. It
/// is never read as "not found", "no active coverage" or "no snapshot". Only a
/// 404 means absent.
/// </summary>
public class UpstreamClientRefusalTests
{
    private sealed class Stub(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }

    private static (IHttpClientFactory Http, Stub Stub, IConfiguration Cfg, UpstreamAuthorization Auth) Parts(HttpStatusCode status)
    {
        var stub = new Stub(status);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(stub, disposeHandler: false));
        var cfg = new ConfigurationBuilder().Build();
        var auth = new UpstreamAuthorization(new HttpContextAccessor(), new ServiceCollection().BuildServiceProvider());
        return (http, stub, cfg, auth);
    }

    public static TheoryData<HttpStatusCode> Refusals => new() { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Coverage_Refusal_Throws(HttpStatusCode status)
    {
        var (http, stub, cfg, auth) = Parts(status);
        var client = new CoverageClient(http, cfg, NullLogger<CoverageClient>.Instance, auth);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetActiveAsync("tenant-1", "mem-1"));

        Assert.Equal(status, ex.StatusCode);
        Assert.Equal("tenant-1", stub.Last!.Headers.GetValues("X-Tenant-ID").Single());
    }

    [Fact]
    public async Task Coverage_NotFound_IsNoActiveCoverage()
    {
        var (http, _, cfg, auth) = Parts(HttpStatusCode.NotFound);
        var client = new CoverageClient(http, cfg, NullLogger<CoverageClient>.Instance, auth);

        Assert.Null(await client.GetActiveAsync("tenant-1", "mem-1"));
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Sponsor_Refusal_Throws(HttpStatusCode status)
    {
        var (http, stub, cfg, auth) = Parts(status);
        var client = new SponsorClient(http, cfg, NullLogger<SponsorClient>.Instance, auth);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("tenant-1", "G-1"));
        Assert.Equal("tenant-1", stub.Last!.Headers.GetValues("X-Tenant-ID").Single());
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task BenefitPlan_Refusal_Throws(HttpStatusCode status)
    {
        var (http, stub, cfg, auth) = Parts(status);
        var client = new BenefitPlanClient(http, cfg, NullLogger<BenefitPlanClient>.Instance, auth);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("tenant-1", "P-1"));
        Assert.Equal("tenant-1", stub.Last!.Headers.GetValues("X-Tenant-ID").Single());
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Member_Refusal_Throws(HttpStatusCode status)
    {
        var (http, stub, cfg, auth) = Parts(status);
        var client = new MemberClient(http, cfg, NullLogger<MemberClient>.Instance, auth);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("tenant-1", "mem-1"));
        Assert.Equal("tenant-1", stub.Last!.Headers.GetValues("X-Tenant-ID").Single());
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Eligibility_Refusal_Throws(HttpStatusCode status)
    {
        var (http, stub, cfg, auth) = Parts(status);
        var client = new EligibilityClient(http, cfg, NullLogger<EligibilityClient>.Instance, auth);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetSnapshotAsync("tenant-1", "mem-1", null));
        Assert.Equal("tenant-1", stub.Last!.Headers.GetValues("X-Tenant-ID").Single());
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task MemberDocument_Refusal_Throws(HttpStatusCode status)
    {
        var (http, stub, cfg, auth) = Parts(status);
        var client = new MemberDocumentClient(http, cfg, NullLogger<MemberDocumentClient>.Instance, auth);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.UploadPdfAsync("tenant-1", "mem-1", new byte[] { 1 }, "f.pdf", "IdCard", null, "user-1"));
        Assert.Equal("tenant-1", stub.Last!.Headers.GetValues("X-Tenant-ID").Single());
    }
}
