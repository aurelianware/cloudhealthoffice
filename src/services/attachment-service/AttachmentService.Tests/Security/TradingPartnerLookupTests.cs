using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text;
using AttachmentService.Services;
using AttachmentService.Tests.Support;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AttachmentService.Tests.Security;

/// <summary>
/// attachment-service reads trading partners through trading-partner-service's
/// API (it owns the TradingPartners collection), with its own service token for
/// the tenant, and a failed read is an error, never a default partner.
/// </summary>
[Collection(nameof(AttachmentServiceCollection))]
public class TradingPartnerLookupTests
{
    private const string Tenant = "tenant-a";
    private readonly AttachmentServiceFactory _factory;

    public TradingPartnerLookupTests(AttachmentServiceFactory factory)
    {
        _factory = factory;
        _factory.Reset();
    }

    private (HttpTradingPartnerLookup Lookup, Recorder Recorder, WebApplicationFactory<AttachmentService.Controllers.AttachmentsController> Host)
        Lookup(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var recorder = new Recorder(answer);
        var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.AddHttpClient(HttpTradingPartnerLookup.ClientName).ConfigurePrimaryHttpMessageHandler(() => recorder)));
        var lookup = new HttpTradingPartnerLookup(
            host.Services.GetRequiredService<IHttpClientFactory>(), host.Services, NullLogger<HttpTradingPartnerLookup>.Instance);
        return (lookup, recorder, host);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string Partners = """
        [{"id":"tp.1","tenantId":"tenant-a","tradingPartnerId":"PAYER-1","partnerName":"Payer One","status":"Active",
          "x12Config":{"senderId":"SND01","receiverId":"RCV01"}},
         {"id":"tp.2","tenantId":"tenant-a","tradingPartnerId":"PAYER-2","partnerName":"Payer Two","status":"Inactive",
          "x12Config":{"senderId":"SND02","receiverId":"RCV02"}}]
        """;

    [Fact]
    public async Task ReadsThePartnerThroughTheApi_WithItsOwnServiceTokenForTheTenant()
    {
        var (lookup, recorder, host) = Lookup(_ => Json(Partners));
        using var _ = host;

        var partner = await lookup.GetByPayerIdAsync("PAYER-1", Tenant);

        partner!.InterchangeSenderId.Should().Be("SND01");
        partner.InterchangeReceiverId.Should().Be("RCV01");
        partner.PartnerName.Should().Be("Payer One");

        var sent = recorder.Requests.Should().ContainSingle().Subject;
        sent.RequestUri!.AbsolutePath.Should().Be($"/api/TradingPartners/tenant/{Tenant}");
        sent.Headers.GetValues("X-Tenant-ID").Should().Equal(Tenant);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(sent.Headers.Authorization!.Parameter);
        jwt.Subject.Should().Be("attachment-service");
        jwt.Claims.Should().Contain(c => c.Type == ChoClaimTypes.TenantId && c.Value == Tenant);
    }

    [Theory]
    [InlineData("PAYER-2")] // inactive
    [InlineData("PAYER-9")] // not configured
    public async Task NoActivePartner_IsNull(string payerId)
    {
        var (lookup, _, host) = Lookup(_ => Json(Partners));
        using var __ = host;

        (await lookup.GetByPayerIdAsync(payerId, Tenant)).Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task FailedRead_Throws_NeverADefault(HttpStatusCode status)
    {
        var (lookup, _, host) = Lookup(_ => new HttpResponseMessage(status));
        using var __ = host;

        await FluentActions.Awaiting(() => lookup.GetByPayerIdAsync("PAYER-1", Tenant))
            .Should().ThrowAsync<TradingPartnerLookupException>();
    }

    [Fact]
    public async Task Acknowledgment_WhenTheLookupFails_Is502_AndNothingIsGenerated()
    {
        var attachment = _factory.Attachments.Seed(Tenant);
        _factory.TradingPartners.Setup(t => t.GetByPayerIdAsync("PAYER-1", Tenant))
            .ThrowsAsync(new TradingPartnerLookupException("trading-partner-service answered 503"));
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("user-1"));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.PostAsync($"/api/attachments/{attachment.Id}/acknowledgment", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("503");
        _factory.Attachments.Items[attachment.Id].Generated999.Should().BeNullOrEmpty();
    }

    public sealed class Recorder(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }
}
