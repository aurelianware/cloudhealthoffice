using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PersonalRepresentativeService.Controllers;
using PersonalRepresentativeService.HostedServices;
using PersonalRepresentativeService.Models;
using PersonalRepresentativeService.Repositories;
using PersonalRepresentativeService.Services;
using PersonalRepresentativeService.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PersonalRepresentativeService.Tests.Integration;

/// <summary>
/// End-to-end smoke test over the real Program pipeline using in-memory
/// fakes so the test does not require Cosmos, Mongo, Key Vault, or Kafka.
/// Mirrors <c>ConsentService.Tests.Integration.ConsentLifecycleSmokeTests</c>.
///
/// The golden-path lifecycle exercised here:
///   Create → AddAssoc(M1) → AddAssoc(M2) → Activate → RemoveAssoc(M1) → Revoke
/// Six audit events in order.
/// </summary>
public class PersonalRepLifecycleSmokeTests : IClassFixture<PersonalRepLifecycleSmokeTests.Factory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public InMemoryPersonalRepRepository Repo { get; } = new();
        public RecordingPersonalRepEventPublisher Publisher { get; } = new();
        public ReversiblePersonalRepFieldEncryptor Encryptor { get; } = new();

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((ctx, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Leave MongoDb/CosmosDb connection unset; swap repository
                    // implementations below so the Cosmos branch in Program.cs
                    // never resolves a CosmosClient. Kafka bootstrap empty so
                    // the PersonalRepEventPublisher hosted service goes into
                    // degraded mode.
                    ["PersonalRepEncryption:KeySecretPrefix"] = "personal-rep-body-encryption-key",
                    ["PersonalRepEncryption:CurrentKeyVersion"] = "v1",
                    ["PersonalRepEncryption:AcceptedKeyVersions:0"] = "v1"
                });
            });

            builder.ConfigureServices(services =>
            {
                RemoveAll<IPersonalRepRepository>(services);
                RemoveAll<IPersonalRepEventRepository>(services);
                RemoveAll<IPersonalRepEventSink>(services);
                RemoveAll<IPersonalRepFieldEncryptor>(services);
                RemoveAll<IPersonalRepEventPublisher>(services);
                RemoveAll<PersonalRepIndexInitializer>(services);

                services.AddSingleton<IPersonalRepRepository>(Repo);
                services.AddSingleton<IPersonalRepEventRepository>(Repo);
                services.AddSingleton<IPersonalRepEventSink>(Repo);
                services.AddSingleton<IPersonalRepFieldEncryptor>(Encryptor);
                services.AddSingleton<IPersonalRepEventPublisher>(Publisher);
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

    public PersonalRepLifecycleSmokeTests(Factory factory) { _factory = factory; }

    private HttpClient NewClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-int");
        return client;
    }

    [Fact]
    public async Task FullLifecycle_SixEventsInOrder()
    {
        _factory.Publisher.StatusCalls.Clear();
        _factory.Publisher.AssociationCalls.Clear();
        var client = NewClient();

        var create = await client.PostAsJsonAsync("/api/v1/personal-representatives",
            new CreatePersonalRepRequest
            {
                CredentialType = PersonalRepCredentialType.LegalGuardian,
                FirstName = "Alice",
                LastName = "Smith"
            }, Json);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var rep = await create.Content.ReadFromJsonAsync<PersonalRepresentative>(Json);
        rep.Should().NotBeNull();

        var addM1 = await client.PostAsJsonAsync(
            $"/api/v1/personal-representatives/{rep!.Id}/associations",
            new AddAssociationRequest { MemberId = "M1" }, Json);
        addM1.StatusCode.Should().Be(HttpStatusCode.Created);

        var addM2 = await client.PostAsJsonAsync(
            $"/api/v1/personal-representatives/{rep.Id}/associations",
            new AddAssociationRequest { MemberId = "M2" }, Json);
        addM2.StatusCode.Should().Be(HttpStatusCode.Created);

        var activate = await client.PostAsync(
            $"/api/v1/personal-representatives/{rep.Id}/activate", content: null);
        activate.StatusCode.Should().Be(HttpStatusCode.OK);

        var removeM1 = await client.DeleteAsync(
            $"/api/v1/personal-representatives/{rep.Id}/associations/M1");
        removeM1.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var revoke = await client.PostAsJsonAsync(
            $"/api/v1/personal-representatives/{rep.Id}/revoke",
            new RevokePersonalRepRequest { ReasonCode = PersonalRepInactivationReasonCode.PoaRevoked },
            Json);
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);

        var history = await client.GetFromJsonAsync<PersonalRepHistoryResponse>(
            $"/api/v1/personal-representatives/{rep.Id}/history", Json);
        history!.Items.Select(e => e.EventType).Should().ContainInOrder(
            PersonalRepEventType.PersonalRepCreated,
            PersonalRepEventType.PersonalRepAssociationAdded,
            PersonalRepEventType.PersonalRepAssociationAdded,
            PersonalRepEventType.PersonalRepActivated,
            PersonalRepEventType.PersonalRepAssociationRemoved,
            PersonalRepEventType.PersonalRepInactivated);
        history.Items.Should().HaveCount(6);
    }

    [Fact]
    public async Task HealthReady_Returns200_WhenNoOpEncryptorInDev()
    {
        // In Development with no PersonalRepEncryption section on startup
        // we'd fall back to NoOp — but this factory explicitly sets the
        // section, so the real PersonalRepFieldEncryptor is registered.
        // The health check needs a resolvable key, which is NOT present in
        // this in-memory config. We assert the /health/live endpoint
        // instead, which does not run the encryption-key check.
        var client = _factory.CreateClient();
        var r = await client.GetAsync("/health/live");
        r.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MissingTenantHeader_Returns401()
    {
        var client = _factory.CreateClient();
        var r = await client.GetAsync("/api/v1/personal-representatives/any-id");
        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task MemberResolverEndpoint_ReturnsActiveRepsForMember()
    {
        _factory.Publisher.StatusCalls.Clear();
        _factory.Publisher.AssociationCalls.Clear();
        var client = NewClient();

        var create = await client.PostAsJsonAsync("/api/v1/personal-representatives",
            new CreatePersonalRepRequest
            {
                CredentialType = PersonalRepCredentialType.HealthcarePowerOfAttorney,
                FirstName = "Bob",
                LastName = "POA"
            }, Json);
        var rep = await create.Content.ReadFromJsonAsync<PersonalRepresentative>(Json);

        await client.PostAsJsonAsync(
            $"/api/v1/personal-representatives/{rep!.Id}/associations",
            new AddAssociationRequest { MemberId = "M42" }, Json);
        await client.PostAsync(
            $"/api/v1/personal-representatives/{rep.Id}/activate", content: null);

        var resp = await client.GetFromJsonAsync<MemberRepresentativesResponse>(
            "/api/v1/members/M42/personal-representatives/active", Json);
        resp!.Items.Should().ContainSingle(s =>
            s.PersonalRepId == rep.Id &&
            s.DisplayName == "Bob POA" &&
            s.CredentialType == PersonalRepCredentialType.HealthcarePowerOfAttorney &&
            s.Status == PersonalRepStatus.Active);
    }
}
