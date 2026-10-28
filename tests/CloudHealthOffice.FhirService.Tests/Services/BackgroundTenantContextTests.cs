using System.Net;
using FhirService.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// Background work (a bulk export) calls CHO services as fhir-service, for the
/// one tenant its context names, through the real outbound pipeline: the same
/// handlers every factory client gets.
/// </summary>
public class BackgroundTenantContextTests : IClassFixture<BackgroundTenantContextTests.Host>
{
    private const string Upstream = "http://member-service.probe.test/";

    public sealed class Host : FhirTestWebAppFactory
    {
        public List<HttpRequestMessage> Sent { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // A Services:* URL makes its host a CHO host for the outbound policy.
            builder.UseSetting("Services:ProbeServiceUrl", Upstream);
            builder.ConfigureServices(services => services
                .AddHttpClient("probe")
                .ConfigurePrimaryHttpMessageHandler(() => new Capture(Sent)));
        }
    }

    private readonly Host _host;

    public BackgroundTenantContextTests(Host host) => _host = host;

    private async Task<HttpRequestMessage> SendAsync(HttpContext? context)
    {
        var accessor = _host.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = context;
        try
        {
            _host.Sent.Clear();
            var client = _host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("probe");
            using var request = new HttpRequestMessage(HttpMethod.Get, Upstream + "api/members");
            (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
            return _host.Sent.Single();
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    [Fact]
    public async Task InABackgroundContext_CallsCarryTheServiceTokenForItsTenant()
    {
        var sent = await SendAsync(BackgroundTenantContext.Create(_host.Services, "tenant-bg"));

        sent.Headers.Authorization?.Scheme.Should().Be("Bearer");
        sent.Headers.GetValues("X-Tenant-ID").Should().Equal("tenant-bg");
    }

    [Fact]
    public void AnOrdinaryContextNamingATenant_IsNotTreatedAsBackground()
    {
        // The marker, not the tenant item, is what makes a context background
        // work: a request whose tenant resolved but whose caller holds no CHO
        // token is not lent fhir-service's identity by this handler.
        var context = new DefaultHttpContext();
        context.Items["TenantId"] = "tenant-bg";

        BackgroundTenantContext.IsBackground(context).Should().BeFalse();
        BackgroundTenantContext.IsBackground(null).Should().BeFalse();
        BackgroundTenantContext.IsBackground(BackgroundTenantContext.Create(_host.Services, "t")).Should().BeTrue();
    }

    private sealed class Capture(List<HttpRequestMessage> sent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            sent.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
