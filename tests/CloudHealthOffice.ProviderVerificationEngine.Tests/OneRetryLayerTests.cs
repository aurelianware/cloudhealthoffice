using System.Net;
using CloudHealthOffice.Infrastructure.Http;
using CloudHealthOffice.Infrastructure.Tests.Http;
using CloudHealthOffice.ProviderVerificationEngine.DataSources;
using CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Xunit;

namespace CloudHealthOffice.ProviderVerificationEngine.Tests;

/// <summary>
/// provider-verification-service's external clients, as Program.cs registers
/// them, with only the primary handler replaced. Each brings its own retries,
/// so the shared service-client resilience is off for them: a failing call is
/// tried only as often as its own layer allows, not that many times three.
/// The retry delays run on a <see cref="ManualClock"/>.
/// </summary>
public class OneRetryLayerTests
{
    private sealed class Failing(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }

    private static WebApplicationFactory<CloudHealthOffice.ProviderVerificationService.Program> Host(
        string clientName, HttpMessageHandler primary, ManualClock clock, params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<CloudHealthOffice.ProviderVerificationService.Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddSingleton<TimeProvider>(clock);
                services.Configure<HttpClientFactoryOptions>(clientName,
                    o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = primary));
            });
        });

    [Fact]
    public async Task AFailingNppesLookup_IsTriedOnlyByItsStandardResilienceHandler()
    {
        // AddStandardResilienceHandler: 3 retries, so 4 attempts. With the shared
        // pipeline on top as well, 4 x 3 = 12.
        var clock = new ManualClock();
        var nppes = new Failing(HttpStatusCode.ServiceUnavailable);
        await using var factory = Host(nameof(INppesAdapter), nppes, clock);
        using var scope = factory.Services.CreateScope();
        var adapter = scope.ServiceProvider.GetRequiredService<INppesAdapter>();

        await Assert.ThrowsAnyAsync<Exception>(
            () => clock.Drive(adapter.LookupByNpiAsync("1234567893"), step: TimeSpan.FromMilliseconds(250)));

        Assert.Equal(4, nppes.Calls);
        Assert.Null(factory.Services.GetRequiredService<ChoResiliencePipelines>().For(nameof(INppesAdapter)));
    }

    [Fact]
    public async Task AFailingSamApiScreen_IsTriedOnlyByTheScreenersOwnRetries()
    {
        // Sam:MaxRetries defaults to 3, so 4 attempts. With the shared pipeline
        // retrying each GET as well, 4 x 3 = 12.
        var clock = new ManualClock();
        var sam = new Failing(HttpStatusCode.ServiceUnavailable);
        await using var factory = Host(SamExclusionsApiScreener.HttpClientName, sam, clock,
            ("ProviderVerification:ExclusionScreening:Sam:Enabled", "true"),
            ("ProviderVerification:ExclusionScreening:Sam:Mode", "Api"),
            ("ProviderVerification:ExclusionScreening:Sam:ApiKey", "test-key"),
            // The screener waits in real time between its attempts; keep that short.
            ("ProviderVerification:ExclusionScreening:Sam:RetryBaseDelay", "00:00:00.010"));
        using var scope = factory.Services.CreateScope();
        var adapter = scope.ServiceProvider.GetRequiredService<IExclusionScreeningAdapter>();

        var result = await clock.Drive(adapter.ScreenProviderAsync("1234567893"), step: TimeSpan.FromMilliseconds(250));

        Assert.False(result.WasScreened);
        Assert.Equal(4, sam.Calls);
    }

    [Fact]
    public void TheExclusionDatasetDownload_IsOutsideTheSharedPipeline()
    {
        // A download may take DownloadTimeout (10 min); the sync worker retries on its own schedule.
        var clock = new ManualClock();
        using var factory = Host(ExclusionDatasetSyncBase.HttpClientName, new Failing(HttpStatusCode.OK), clock,
            ("ProviderVerification:ExclusionScreening:Leie:Enabled", "true"),
            ("ProviderVerification:ExclusionScreening:Leie:DownloadUrl", "http://127.0.0.1:9/UPDATED.csv"));

        var pipelines = factory.Services.GetRequiredService<ChoResiliencePipelines>();
        var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(ExclusionDatasetSyncBase.HttpClientName);

        Assert.Null(pipelines.For(ExclusionDatasetSyncBase.HttpClientName));
        Assert.Equal(TimeSpan.FromMinutes(11), client.Timeout); // DownloadTimeout + 1 min, its own
    }
}
