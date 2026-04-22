using System.Net;
using System.Net.Http.Json;
using ConsentService.Controllers;
using ConsentService.Models;
using ConsentService.Repositories;
using ConsentService.Services;
using ConsentService.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ConsentService.Tests.Integration;

/// <summary>
/// End-to-end lifecycle: Create -> Activate -> Revoke through
/// <see cref="WebApplicationFactory{Program}"/>. Substitutes the repository,
/// encryptor, and publisher with in-memory fakes so the test does not need
/// Cosmos, Mongo, Key Vault, or Kafka to run.
/// </summary>
public class ConsentLifecycleSmokeTests : IClassFixture<ConsentLifecycleSmokeTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public readonly InMemoryConsentRepository Repo = new();
        public readonly RecordingConsentEventPublisher Publisher = new();
        public readonly ReversibleConsentFieldEncryptor Encryptor = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ASPNETCORE_ENVIRONMENT", "Development");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IConsentRepository>();
                services.RemoveAll<IConsentEventRepository>();
                services.RemoveAll<IConsentEventSink>();
                services.RemoveAll<IConsentFieldEncryptor>();
                services.RemoveAll<IConsentEventPublisher>();

                services.AddSingleton<IConsentRepository>(Repo);
                services.AddSingleton<IConsentEventRepository>(Repo);
                services.AddSingleton<IConsentEventSink>(Repo);
                services.AddSingleton<IConsentFieldEncryptor>(Encryptor);
                services.AddSingleton<IConsentEventPublisher>(Publisher);

                // The default consent-encryption-key readiness check would
                // fail in the test harness (no secret store). Override with
                // a pass-through so the test can reach /health/ready.
                services.Configure<HealthCheckServiceOptions>(opt =>
                {
                    opt.Registrations.Clear();
                    opt.Registrations.Add(new HealthCheckRegistration(
                        "self",
                        _ => new AlwaysHealthyCheck(),
                        HealthStatus.Unhealthy,
                        new[] { "live", "ready" }));
                });
            });
        }

        private sealed class AlwaysHealthyCheck : IHealthCheck
        {
            public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
                => Task.FromResult(HealthCheckResult.Healthy());
        }
    }

    private readonly Factory _factory;

    public ConsentLifecycleSmokeTests(Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task FullLifecycle_ThreeEventsInOrder()
    {
        _factory.Publisher.Calls.Clear();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-a");

        // Create
        var create = await client.PostAsJsonAsync("/api/v1/members/M1/consents", new CreateConsentRequest
        {
            ConsentType = ConsentType.GeneralAuthorization,
            GrantedBy = "alice",
            Reason = "continuity of care"
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var consent = await create.Content.ReadFromJsonAsync<Consent>();
        consent.Should().NotBeNull();

        // Activate
        var activate = await client.PostAsync($"/api/v1/members/M1/consents/{consent!.Id}/activate", content: null);
        activate.StatusCode.Should().Be(HttpStatusCode.OK);

        // Revoke
        var revoke = await client.PostAsJsonAsync(
            $"/api/v1/members/M1/consents/{consent.Id}/revoke",
            new RevokeConsentRequest { ReasonCode = ConsentRevocationReasonCode.MemberRequest });
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);

        // Audit trail
        var history = await client.GetFromJsonAsync<ConsentHistoryResponse>(
            $"/api/v1/members/M1/consents/{consent.Id}/history");
        history!.Items.Select(e => e.EventType).Should().ContainInOrder(
            ConsentEventType.ConsentCreated,
            ConsentEventType.ConsentActivated,
            ConsentEventType.ConsentRevoked);
    }

    [Fact]
    public async Task HealthEndpoints_AreReachable()
    {
        var client = _factory.CreateClient();

        (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MissingTenantHeader_Returns401()
    {
        var client = _factory.CreateClient();
        // No X-Tenant-ID header.
        var r = await client.GetAsync("/api/v1/members/M1/consents");
        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
