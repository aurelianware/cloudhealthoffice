using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Contacts;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Testing.Mongo;
using ContactService.Models;
using ContactService.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace ContactService.Tests;

[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ContactPipelineTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private IMongoDatabase _db = null!;
    private WebApplicationFactory<Program> _factory = null!;
    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("contact_pipeline");
        var gateway = new Mock<IContactWorkflowGateway>();
        gateway.Setup(g => g.OpenCaseAsync(It.IsAny<Contact>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Contact c, CancellationToken _) => new ContactCaseResult {
                Id = ContactIntakeIdentity.For(c.TenantId, c.Id), ReceivedAt = c.StartedAt,
                Kind = "Grievance", Status = "Received", DueAt = c.StartedAt.AddDays(30)
            });
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
            builder.UseEnvironment("Development");
            builder.UseSetting("MongoDb:ConnectionString", mongo.ConnectionString);
            builder.UseSetting("MongoDb:DatabaseName", _db.DatabaseNamespace.DatabaseName);
            builder.ConfigureServices(services => {
                services.RemoveAll<IContactWorkflowGateway>();
                services.AddSingleton(gateway.Object);
            });
        });
        return Task.CompletedTask;
    }
    public async Task DisposeAsync() { await _factory.DisposeAsync(); await mongo.DropDatabaseAsync(_db); }
    private HttpClient Client(string role = "MemberServices", string tenant = "synthetic-tenant")
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("synthetic-rep", role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }
    private static async Task<Contact> Start(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/contacts", new StartContactRequest {
            MemberId = "synthetic-member", Caller = new ContactCaller { Type = CallerType.Member }
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Contact>())!;
    }

    [Fact]
    public async Task Development_host_requires_authentication_and_service_entitlement()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/contacts/mine")).StatusCode);
        using var examiner = Client("ClaimsExaminer");
        Assert.Equal(HttpStatusCode.Forbidden, (await examiner.GetAsync("/api/contacts/mine")).StatusCode);
    }

    [Fact]
    public async Task Case_intent_and_receipt_survive_http_reload_and_contact_closure()
    {
        using var client = Client();
        var c = await Start(client);
        var patch = await client.PatchAsJsonAsync("/api/contacts/" + c.Id, new UpdateContactRequest {
            Revision = c.Revision, Classification = ContactClassification.Grievance, Summary = "Synthetic access concern"
        });
        patch.EnsureSuccessStatusCode(); c = (await patch.Content.ReadFromJsonAsync<Contact>())!;
        var response = await client.PostAsJsonAsync($"/api/contacts/{c.Id}/case", new {
            c.Revision, Intent = new ContactCaseIntent { State = "CA", LineOfBusiness = "Medicaid" }
        });
        response.EnsureSuccessStatusCode();
        c = (await response.Content.ReadFromJsonAsync<Contact>())!;
        Assert.NotNull(c.CaseIntent);
        Assert.Equal(c.StartedAt, c.OpenedCase!.ReceivedAt);
        var closed = await client.PostAsJsonAsync($"/api/contacts/{c.Id}/close", new CloseContactRequest {
            Revision = c.Revision, Classification = c.Classification, Resolution = ContactResolution.ResolvedOnCall
        });
        closed.EnsureSuccessStatusCode();
        var reload = await client.GetFromJsonAsync<Contact>("/api/contacts/" + c.Id);
        Assert.Equal(ContactStatus.Closed, reload!.Status);
        Assert.Equal(c.OpenedCase.Id, reload.OpenedCase!.Id);
        using var otherTenant = Client(tenant: "other-synthetic-tenant");
        Assert.Equal(HttpStatusCode.NotFound, (await otherTenant.GetAsync("/api/contacts/" + c.Id)).StatusCode);
    }

    [Fact]
    public async Task Unclassified_contact_cannot_close_over_http()
    {
        using var client = Client();var c = await Start(client);
        var response = await client.PostAsJsonAsync($"/api/contacts/{c.Id}/close", new CloseContactRequest {
            Revision = c.Revision, Resolution = ContactResolution.ResolvedOnCall
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
