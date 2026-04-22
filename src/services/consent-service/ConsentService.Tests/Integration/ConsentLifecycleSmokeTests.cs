using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConsentService.Controllers;
using ConsentService.HostedServices;
using ConsentService.Models;
using ConsentService.Repositories;
using ConsentService.Services;
using ConsentService.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ConsentService.Tests.Integration;

/// <summary>
/// End-to-end smoke test over the real Program pipeline using in-memory fakes
/// so the test does not require Cosmos, Mongo, Key Vault, or Kafka.
/// Mirrors the pattern used by <c>MemberService.Tests.Integration.MemberFhirSmokeTests</c>.
/// </summary>
public class ConsentLifecycleSmokeTests : IClassFixture<ConsentLifecycleSmokeTests.Factory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public InMemoryConsentRepository Repo { get; } = new();
        public RecordingConsentEventPublisher Publisher { get; } = new();
        public ReversibleConsentFieldEncryptor Encryptor { get; } = new();

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((ctx, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Leave MongoDb/CosmosDb connection unset; we swap the repository
                    // implementations below so the Cosmos branch in Program.cs never
                    // resolves a CosmosClient. Kafka bootstrap stays empty so the
                    // ConsentEventPublisher hosted service goes into degraded mode.
                    ["ConsentEncryption:KeySecretPrefix"] = "consent-body-encryption-key",
                    ["ConsentEncryption:CurrentKeyVersion"] = "v1",
                    ["ConsentEncryption:AcceptedKeyVersions:0"] = "v1"
                });
            });

            builder.ConfigureServices(services =>
            {
                RemoveAll<IConsentRepository>(services);
                RemoveAll<IConsentEventRepository>(services);
                RemoveAll<IConsentEventSink>(services);
                RemoveAll<IConsentFieldEncryptor>(services);
                RemoveAll<IConsentEventPublisher>(services);
                RemoveAll<ConsentIndexInitializer>(services);

                // Don't remove the concrete ConsentEventPublisher singleton —
                // AddHostedService resolves it via a factory, and with no
                // Kafka:BootstrapServers it starts in degraded-mode no-op.
                services.AddSingleton<IConsentRepository>(Repo);
                services.AddSingleton<IConsentEventRepository>(Repo);
                services.AddSingleton<IConsentEventSink>(Repo);
                services.AddSingleton<IConsentFieldEncryptor>(Encryptor);
                services.AddSingleton<IConsentEventPublisher>(Publisher);
            });

            return base.CreateHost(builder);
        }

        private static void RemoveAll<T>(IServiceCollection services)
        {
            var toRemove = services.Where(d =>
                d.ServiceType == typeof(T) || d.ImplementationType == typeof(T)).ToList();
            foreach (var d in toRemove) services.Remove(d);
        }
    }

    private readonly Factory _factory;

    public ConsentLifecycleSmokeTests(Factory factory) { _factory = factory; }

    private HttpClient NewClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-int");
        return client;
    }

    [Fact]
    public async Task FullLifecycle_ThreeEventsInOrder()
    {
        _factory.Publisher.Calls.Clear();
        var client = NewClient();

        var create = await client.PostAsJsonAsync("/api/v1/members/M1/consents", new CreateConsentRequest
        {
            ConsentType = ConsentType.GeneralAuthorization,
            GrantedBy = "alice",
            Reason = "continuity of care"
        }, Json);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var consent = await create.Content.ReadFromJsonAsync<Consent>(Json);
        consent.Should().NotBeNull();

        var activate = await client.PostAsync($"/api/v1/members/M1/consents/{consent!.Id}/activate", content: null);
        activate.StatusCode.Should().Be(HttpStatusCode.OK);

        var revoke = await client.PostAsJsonAsync(
            $"/api/v1/members/M1/consents/{consent.Id}/revoke",
            new RevokeConsentRequest { ReasonCode = ConsentRevocationReasonCode.MemberRequest },
            Json);
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);

        var history = await client.GetFromJsonAsync<ConsentHistoryResponse>(
            $"/api/v1/members/M1/consents/{consent.Id}/history", Json);
        history!.Items.Select(e => e.EventType).Should().ContainInOrder(
            ConsentEventType.ConsentCreated,
            ConsentEventType.ConsentActivated,
            ConsentEventType.ConsentRevoked);
    }

    [Fact]
    public async Task MissingTenantHeader_Returns401()
    {
        var client = _factory.CreateClient();
        var r = await client.GetAsync("/api/v1/members/M1/consents");
        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
