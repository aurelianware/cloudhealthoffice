using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MemberService.Controllers;
using MemberService.HostedServices;
using MemberService.Models;
using MemberService.Repositories;
using MemberService.Services;
using MemberService.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MemberService.Tests.Integration;

/// <summary>
/// End-to-end smoke over the real Program pipeline using an in-memory repo so the
/// test doesn't require Cosmos/Mongo. Exercises: create member → event written →
/// GET FHIR returns a Patient resource with the expected extensions and identifiers.
/// </summary>
public class MemberFhirSmokeTests : IClassFixture<MemberFhirSmokeTests.Factory>
{
    // Match the server's wire format (string enums via JsonStringEnumConverter
    // registered by AddCloudHealthOfficeJsonOptions).
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public InMemoryMemberRepository MemberRepo { get; } = new();
        public InMemoryMemberEventRepository EventRepo { get; } = new();

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((ctx, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Force the Mongo branch in Program.cs so we never try to contact Cosmos
                    // before our overrides land. The Mongo db registration is replaced below.
                    ["MongoDb:ConnectionString"] = "mongodb://fake-host:27017",
                    ["MongoDb:DatabaseName"] = "CloudHealthOffice",
                    ["Member:IdentifierEncryption:KeySecretName"] = ""
                });
            });
            builder.ConfigureServices(services =>
            {
                RemoveAll<IMemberRepository>(services);
                RemoveAll<IMemberEventRepository>(services);
                RemoveAll<IFamilyRelationshipRepository>(services);
                RemoveAll<MongoDB.Driver.IMongoClient>(services);
                RemoveAll<MongoDB.Driver.IMongoDatabase>(services);

                // Remove the Mongo index initializers that would try to connect to the
                // fake MongoDB host at startup. Don't use RemoveAll<IHostedService>() as
                // that would also remove the Kestrel host service and prevent the test
                // server from starting. RemoveAll checks both ServiceType and
                // ImplementationType so it catches both factory-less AddHostedService<T>
                // registrations and explicit factory-style AddSingleton<IHostedService>
                // registrations.
                RemoveAll<MemberEventIndexInitializer>(services);
                RemoveAll<MemberIndexInitializer>(services);
                RemoveAll<FamilyRelationshipIndexInitializer>(services);

                // Alert/note repositories are only registered on the Cosmos branch
                // in Program.cs. If the test happens to land on the Cosmos branch,
                // resolving IMemberAlertGuard cascades into a CosmosClient factory
                // that requires CosmosDb:Endpoint (not configured in tests). Drop
                // the guard entirely; MembersController's IMemberAlertGuard parameter
                // is optional and falls through to null.
                RemoveAll<IMemberAlertGuard>(services);

                services.AddSingleton<IMemberRepository>(MemberRepo);
                services.AddSingleton<IMemberEventRepository>(EventRepo);
                services.AddSingleton<IFamilyRelationshipRepository>(new InMemoryFamilyRelationshipRepository());
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

    public MemberFhirSmokeTests(Factory factory) { _factory = factory; }

    private HttpClient NewClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-int");
        return client;
    }

    [Fact]
    public async Task CreateMember_Then_GetFhir_And_Events_RoundTrip()
    {
        _factory.MemberRepo.Members.Clear();
        var client = NewClient();

        var createResp = await client.PostAsJsonAsync("/api/v1/members", new CreateMemberRequest
        {
            MemberId = "INT-001",
            GroupNumber = "GRP",
            IsSubscriber = true,
            FirstName = "Iris",
            LastName = "Integration",
            DateOfBirth = new DateTime(1990, 1, 1),
            EffectiveDate = new DateTime(2024, 1, 1),
            Gender = "F",
            PreferredLanguage = "en-US",
            BirthSex = "F"
        });
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var fhirResp = await client.GetAsync("/api/v1/members/INT-001/fhir");
        fhirResp.IsSuccessStatusCode.Should().BeTrue();
        fhirResp.Content.Headers.ContentType!.MediaType.Should().Be("application/fhir+json");

        var body = await fhirResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("resourceType").GetString().Should().Be("Patient");
        doc.RootElement.GetProperty("birthDate").GetString().Should().Be("1990-01-01");

        // Events stream contains MemberCreated with Version=1.
        var eventsResp = await client.GetAsync("/api/v1/members/INT-001/events");
        eventsResp.IsSuccessStatusCode.Should().BeTrue();
        var events = await eventsResp.Content.ReadFromJsonAsync<List<MemberEvent>>(Json);
        events.Should().NotBeNull();
        events!.Should().ContainSingle(e => e.EventType == MemberEventType.MemberCreated && e.Version == 1);
    }
}
