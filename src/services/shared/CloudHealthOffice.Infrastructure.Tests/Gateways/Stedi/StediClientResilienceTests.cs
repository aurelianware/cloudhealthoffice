using System.Net;
using System.Text;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Infrastructure.Tests.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

/// <summary>
/// The Stedi clients as eligibility-service registers them
/// (<c>AddChoAuthentication</c>, which adds the shared client resilience, then
/// <c>AddChoHealthcareGateways</c>), with only the primary HTTP handler
/// replaced: a Stedi call may take as long as Stedi's own timeout allows.
/// The fake Stedi "takes" time by moving a <see cref="ManualClock"/>; no test
/// waits in real time.
/// </summary>
public class StediClientResilienceTests
{
    private const string CreateJson =
        "{\"attachmentId\":\"att-remote-1\",\"uploadUrl\":\"https://uploads.stedi.test/att-remote-1\"}";

    /// <summary>A fake Stedi: answers in order, each answer after taking its time on the clock.</summary>
    private sealed class FakeStedi : HttpMessageHandler
    {
        private readonly ManualClock _clock;
        private readonly Queue<(TimeSpan Takes, HttpStatusCode Status, string Body)> _script = new();
        public List<HttpMethod> Calls { get; } = new();
        public (TimeSpan Takes, HttpStatusCode Status, string Body) Otherwise { get; set; } = (TimeSpan.Zero, HttpStatusCode.OK, "{}");

        public FakeStedi(ManualClock clock) => _clock = clock;

        public FakeStedi Then(TimeSpan takes, HttpStatusCode status, string body = "{}")
        {
            _script.Enqueue((takes, status, body));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            (TimeSpan Takes, HttpStatusCode Status, string Body) next;
            lock (Calls)
            {
                Calls.Add(request.Method);
                next = _script.Count > 0 ? _script.Dequeue() : Otherwise;
            }
            _clock.Advance(next.Takes);
            ct.ThrowIfCancellationRequested(); // a timeout that fell due meanwhile cancelled the call
            return Task.FromResult(new HttpResponseMessage(next.Status)
            {
                Content = new StringContent(next.Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "eligibility-service";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static ServiceProvider EligibilityServiceStedi(FakeStedi stedi, ManualClock clock)
    {
        var settings = new Dictionary<string, string?>(ChoDevelopmentAuth.Configuration("eligibility-service"))
        {
            ["HealthcareTransactions:DefaultGateway"] = "Stedi",
            ["HealthcareTransactions:Gateways:Stedi:ApiKey"] = "test-key",
            ["HealthcareTransactions:Gateways:Stedi:BaseUrl"] = "https://healthcare.us.stedi.com",
            ["HealthcareTransactions:Gateways:Stedi:Environment"] = "sandbox",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddChoAuthentication(configuration, new Env("Development"));
        services.AddChoHealthcareGateways(configuration);
        services.AddSingleton<TimeProvider>(clock);
        foreach (var name in new[]
                 {
                     StediEligibilityApiClient.HttpClientName, StediClaimAttachmentApiClient.HttpClientName,
                     StediClaimAttachmentApiClient.UploadHttpClientName, StediClaimAcknowledgmentApiClient.CoreHttpClientName,
                 })
        {
            services.Configure<HttpClientFactoryOptions>(name,
                o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = stedi));
        }
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task AnAttachmentUploadThatTakes25Seconds_Succeeds()
    {
        var clock = new ManualClock();
        var stedi = new FakeStedi(clock)
            .Then(TimeSpan.FromSeconds(1), HttpStatusCode.Created, CreateJson)
            .Then(TimeSpan.FromSeconds(25), HttpStatusCode.OK);
        using var provider = EligibilityServiceStedi(stedi, clock);
        var content = provider.GetRequiredService<IClaimAttachmentContentStore>();
        var stored = await content.StoreAsync(new ClaimAttachmentStoreRequest
        {
            TenantId = "tenant-alpha", TransmissionId = "tx-1", AttachmentId = "att-1", ContentType = "application/pdf",
        }, new MemoryStream("%PDF-1.4 synthetic"u8.ToArray()));

        var result = await clock.Drive(provider.GetRequiredService<StediClaimAttachmentApiClient>().SubmitAsync(
            new ClaimAttachmentSubmissionRequest { ContentType = "application/pdf", Content = stored },
            stored, CancellationToken.None));

        result.Response.AttachmentId.Should().Be("att-remote-1");
        stedi.Calls.Should().Equal(HttpMethod.Post, HttpMethod.Put);
    }

    [Fact]
    public async Task AnEligibilityCallThatTakes25Seconds_Succeeds()
    {
        // The 270/271 path Cloud Dental Office uses in production.
        var clock = new ManualClock();
        var stedi = new FakeStedi(clock).Then(TimeSpan.FromSeconds(25), HttpStatusCode.OK, "{}");
        using var provider = EligibilityServiceStedi(stedi, clock);

        var result = await clock.Drive(provider.GetRequiredService<StediEligibilityApiClient>()
            .SendEligibilityAsync(new StediEligibilityRequestDto(), CancellationToken.None));

        result.RetryCount.Should().Be(0);
        stedi.Calls.Should().ContainSingle();
    }
}
