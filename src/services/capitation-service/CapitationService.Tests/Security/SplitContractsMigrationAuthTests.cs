using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using CapitationService.Migrations;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CapitationService.Tests.Security;

/// <summary>
/// The split-contracts CLI used a bare <c>new HttpClient()</c> with no token
/// for every tenant's documents, so provider-contracts-service (which requires
/// CHO tokens) rejected every call. It now uses a factory client with the
/// shared outbound handler: each call names its document's tenant and carries
/// a capitation-service service token for that tenant.
/// </summary>
public class SplitContractsMigrationAuthTests
{
    private const string Url = "http://provider-contracts-service:8080";

    private sealed class StubHandler : HttpMessageHandler
    {
        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            if (request.Headers.Authorization == null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            return Task.FromResult(request.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new { id = "pc-" + Guid.NewGuid() }) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) });
        }
    }

    private static IConfiguration Config(bool withServiceToken) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(ChoDevelopmentAuth.Configuration(withServiceToken ? "capitation-service" : null))
            .Build();

    private static (string? Tenant, string? Subject, string[] Roles) Token(HttpRequestMessage request)
    {
        var auth = request.Headers.Authorization;
        if (auth?.Parameter is not { Length: > 0 } raw)
            return (null, null, []);
        var token = new JsonWebToken(raw);
        return (token.Claims.FirstOrDefault(c => c.Type == ChoClaimTypes.TenantId)?.Value,
                token.Subject,
                token.Claims.Where(c => c.Type == ChoClaimTypes.Role).Select(c => c.Value).ToArray());
    }

    private static SplitCapitationContracts.OldCapitationContract Doc(string tenant, string number) => new()
    {
        Id = Guid.NewGuid().ToString(), TenantId = tenant, ContractNumber = number,
        ProviderNPI = "1234567890", ProviderName = "Dr. Smith", Status = "Active",
        EffectiveDate = new DateTime(2026, 1, 1)
    };

    [Fact]
    public async Task EachCall_CarriesAServiceTokenForItsDocumentsTenant()
    {
        var stub = new StubHandler();
        await using var services = SplitCapitationContracts.BuildServices(Config(withServiceToken: true), "Development", Url,
            client => client.ConfigurePrimaryHttpMessageHandler(() => stub));
        var factory = services.GetRequiredService<IHttpClientFactory>();

        (await SplitCapitationContracts.PreflightAsync(factory, "tenant-a")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SplitCapitationContracts.PreflightAsync(factory, "tenant-b")).StatusCode.Should().Be(HttpStatusCode.OK);
        var idA = await SplitCapitationContracts.CreateProviderContractAsync(factory, Doc("tenant-a", "CAP-A"));
        var idB = await SplitCapitationContracts.CreateProviderContractAsync(factory, Doc("tenant-b", "CAP-B"));

        idA.Should().StartWith("pc-");
        idB.Should().StartWith("pc-");
        var requests = stub.Requests.ToList();
        requests.Should().HaveCount(4);
        foreach (var (request, tenant) in requests.Zip(new[] { "tenant-a", "tenant-b", "tenant-a", "tenant-b" }))
        {
            request.Headers.GetValues("X-Tenant-ID").Should().ContainSingle().Which.Should().Be(tenant);
            var token = Token(request);
            token.Tenant.Should().Be(tenant, "each call's token is minted for the tenant it names");
            token.Subject.Should().Be("capitation-service");
            token.Roles.Should().Contain(ChoServiceRole.Name);
        }
    }

    [Fact]
    public async Task DocumentWithoutTenant_IsNotSent()
    {
        var stub = new StubHandler();
        await using var services = SplitCapitationContracts.BuildServices(Config(withServiceToken: true), "Development", Url,
            client => client.ConfigurePrimaryHttpMessageHandler(() => stub));

        var act = () => SplitCapitationContracts.CreateProviderContractAsync(
            services.GetRequiredService<IHttpClientFactory>(), Doc("", "CAP-X"));

        await act.Should().ThrowAsync<InvalidOperationException>();
        stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public void WithoutAServiceTokenKey_TheMigrationRefusesToStart()
    {
        var act = () => SplitCapitationContracts.BuildServices(Config(withServiceToken: false), "Development", Url);

        act.Should().Throw<InvalidOperationException>().WithMessage("*ServiceToken*");
    }

    [Fact]
    public void DevelopmentKeys_AreRefusedOutsideDevelopment()
    {
        var act = () => SplitCapitationContracts.BuildServices(Config(withServiceToken: true), "Production", Url);

        act.Should().Throw<InvalidOperationException>().WithMessage("*symmetric*");
    }
}
