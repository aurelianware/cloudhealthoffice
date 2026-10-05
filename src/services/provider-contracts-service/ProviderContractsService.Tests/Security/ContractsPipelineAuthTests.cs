using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProviderContractsService.Models;
using ProviderContractsService.Repositories;

namespace ProviderContractsService.Tests.Security;

/// <summary>
/// The real provider-contracts-service pipeline with an in-memory repository
/// that, like the Mongo one, scopes everything to the request tenant.
/// Every caller needs a CHO token; the tenant comes from it. Reads need
/// contracts:read, writes contracts:write.
/// </summary>
public class ContractsPipelineAuthTests : IClassFixture<ContractsPipelineAuthTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public InMemoryContracts Store { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            // Program.cs reads this while building, so it goes in as a host setting.
            builder.UseSetting("MongoDb:ConnectionString", "mongodb://fake-host:27017");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IProviderContractRepository>();
                services.AddSingleton(Store);
                services.AddScoped<IProviderContractRepository, TenantScopedRepository>();
            });
        }
    }

    /// <summary>Contracts per tenant, plus every tenant a call was made in.</summary>
    public sealed class InMemoryContracts
    {
        public ConcurrentDictionary<(string Tenant, string Id), ProviderContract> Contracts { get; } = new();
        public ConcurrentQueue<string> TenantsSeen { get; } = new();
        public int Writes;

        public void Reset()
        {
            Contracts.Clear();
            TenantsSeen.Clear();
            Writes = 0;
        }
    }

    // Reads the request tenant where the Mongo repository does (HttpContext.Items,
    // set by the tenant middleware), so these tests ran unchanged against the
    // old pipeline to show each defect.
    private sealed class TenantScopedRepository(InMemoryContracts store, IHttpContextAccessor accessor) : IProviderContractRepository
    {
        private string Tenant
        {
            get
            {
                var tenant = accessor.HttpContext?.Items["TenantId"] as string
                    ?? throw new InvalidOperationException("No tenant on this request.");
                store.TenantsSeen.Enqueue(tenant);
                return tenant;
            }
        }

        public Task<ProviderContract?> GetByIdAsync(string id)
            => Task.FromResult(store.Contracts.TryGetValue((Tenant, id), out var c) ? Clone(c) : null);

        public Task<ProviderContract?> GetByContractNumberAsync(string number)
        {
            var tenant = Tenant;
            return Task.FromResult(store.Contracts.Values
                .Where(c => c.TenantId == tenant && c.ContractNumber == number)
                .Select(Clone).FirstOrDefault());
        }

        public Task<IEnumerable<ProviderContract>> SearchAsync(string? providerNpi = null, LineOfBusiness? lob = null,
            ProviderContractStatus? status = null, PaymentMethodology? paymentMethodology = null,
            NetworkParticipationStatus? networkStatus = null, int page = 1, int pageSize = 50)
        {
            var tenant = Tenant;
            return Task.FromResult<IEnumerable<ProviderContract>>(store.Contracts.Values
                .Where(c => c.TenantId == tenant).Select(Clone).ToList());
        }

        public Task<ProviderContract> CreateAsync(ProviderContract contract)
        {
            contract.TenantId = Tenant;
            store.Contracts[(contract.TenantId, contract.Id)] = Clone(contract);
            Interlocked.Increment(ref store.Writes);
            return Task.FromResult(contract);
        }

        public Task<ProviderContract> UpdateAsync(ProviderContract contract)
        {
            contract.TenantId = Tenant;
            store.Contracts[(contract.TenantId, contract.Id)] = Clone(contract);
            Interlocked.Increment(ref store.Writes);
            return Task.FromResult(contract);
        }

        private static ProviderContract Clone(ProviderContract c)
            => System.Text.Json.JsonSerializer.Deserialize<ProviderContract>(
                System.Text.Json.JsonSerializer.Serialize(c))!;
    }

    // The service reads enums as names, like the portal sends them.
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

    private const string Tenant = "tenant-1";
    private const string OtherTenant = "tenant-2";
    private const string User = "contracts-user-7";
    private readonly Factory _factory;

    public ContractsPipelineAuthTests(Factory factory)
    {
        _factory = factory;
        _factory.Store.Reset();
    }

    private HttpClient Client(string tenant, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(User, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private HttpClient BearerClient(string token)
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private ProviderContract Seed(string tenant, string id = "c-1",
        ProviderContractStatus status = ProviderContractStatus.Draft)
    {
        var contract = new ProviderContract
        {
            Id = id,
            TenantId = tenant,
            ContractNumber = $"CTR-{id}",
            ProviderNPI = "1234567890",
            ProviderName = "Dr. Chen",
            ProviderTin = "123456789",
            ProviderType = ProviderType.Individual,
            LineOfBusiness = LineOfBusiness.Commercial,
            PaymentMethodology = PaymentMethodology.FullCapitation,
            Status = status,
            EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedBy = "original-user",
            Amendments =
            [
                new ContractAmendment
                {
                    Id = "a-1",
                    EffectiveDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                    AmendmentType = "RateChange",
                    Description = "Original amendment",
                    ApprovedBy = "original-approver"
                }
            ]
        };
        _factory.Store.Contracts[(tenant, id)] = contract;
        return contract;
    }

    private ProviderContract Stored(string tenant, string id = "c-1") => _factory.Store.Contracts[(tenant, id)];

    /// <summary>The body the portal's ProviderContractsService sends: no tenant, no contract number.</summary>
    private static Dictionary<string, object?> PortalCreateBody(string? tenantId = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["contractNumber"] = "",
            ["providerNPI"] = "1234567890",
            ["providerName"] = "Dr. Chen",
            ["providerType"] = "Individual",
            ["lineOfBusiness"] = "Commercial",
            ["paymentMethodology"] = "FullCapitation",
            ["networkStatus"] = "Participating",
            ["effectiveDate"] = "2026-01-01T00:00:00Z",
            ["status"] = "Active",
            ["createdBy"] = "someone-else"
        };
        if (tenantId != null)
            body["tenantId"] = tenantId;
        return body;
    }

    // ── No token ──────────────────────────────────────────────────────────

    [Fact]
    public async Task NoToken_IsRejected()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/contracts");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Store.TenantsSeen.Should().BeEmpty();
    }

    [Theory]
    [InlineData("X-Tenant-ID")]
    [InlineData("X-Dev-Tenant-ID")]
    public async Task TenantHeaderWithoutToken_IsRejected(string header)
    {
        // Before: the local TenantMiddleware took the tenant from either header
        // with no authentication, so anyone could read and change any tenant's
        // provider contracts (including the full TIN on GET {id}).
        Seed(OtherTenant);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(header, OtherTenant);

        var responses = new[]
        {
            await client.GetAsync("/api/v1/contracts"),
            await client.GetAsync("/api/v1/contracts/c-1"),
            await client.GetAsync("/api/v1/contracts/number/CTR-c-1"),
            await client.GetAsync("/api/v1/contracts/c-1/rate-configs"),
            await client.PostAsJsonAsync("/api/v1/contracts", PortalCreateBody()),
            await client.PutAsJsonAsync("/api/v1/contracts/c-1", PortalCreateBody()),
            await client.PutAsync("/api/v1/contracts/c-1/activate", null),
            await client.PutAsJsonAsync("/api/v1/contracts/c-1/terminate", new { reason = "x" }),
            await client.PostAsJsonAsync("/api/v1/contracts/c-1/amendments",
                new { effectiveDate = "2026-02-01T00:00:00Z", amendmentType = "RateChange", description = "x" }),
        };

        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Unauthorized);
        _factory.Store.TenantsSeen.Should().BeEmpty();
        _factory.Store.Writes.Should().Be(0);
        Stored(OtherTenant).Status.Should().Be(ProviderContractStatus.Draft);
    }

    [Fact]
    public async Task NoTenantAnywhere_IsRejected_NotDefaulted()
    {
        // Before: with no tenant the old middleware used "default-tenant".
        Seed("default-tenant");
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/contracts/c-1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Store.TenantsSeen.Should().NotContain("default-tenant");
    }

    [Fact]
    public async Task HeaderThatDisagreesWithToken_IsForbidden()
    {
        Seed(OtherTenant);
        var client = BearerClient(ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.ProviderRelations));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", OtherTenant);

        var response = await client.GetAsync("/api/v1/contracts/c-1");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Store.TenantsSeen.Should().BeEmpty();
    }

    // ── Tenant from the token ─────────────────────────────────────────────

    [Fact]
    public async Task TokenTenant_ScopesReads()
    {
        Seed(OtherTenant);
        var client = BearerClient(ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.ProviderRelations));

        var get = await client.GetAsync("/api/v1/contracts/c-1");
        var list = await client.GetFromJsonAsync<List<ProviderContract>>("/api/v1/contracts?tenantId=" + OtherTenant, JsonOptions);

        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        list.Should().BeEmpty();
        _factory.Store.TenantsSeen.Should().OnlyContain(t => t == Tenant);
    }

    [Fact]
    public async Task Create_TakesTenantFromToken_NotBody()
    {
        var client = Client(Tenant, ChoRolePermissions.ProviderRelations);

        var response = await client.PostAsJsonAsync("/api/v1/contracts", PortalCreateBody(tenantId: OtherTenant));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<ProviderContract>(JsonOptions);
        created!.TenantId.Should().Be(Tenant);
        created.Status.Should().Be(ProviderContractStatus.Draft);
        _factory.Store.Contracts.Keys.Should().OnlyContain(k => k.Tenant == Tenant);
    }

    [Fact]
    public async Task Create_PortalShapedBody_WithoutTenant_IsAccepted()
    {
        // Before: ProviderContract.TenantId was [Required], so a body without a
        // tenant (the portal never sends one) was rejected with 400; a caller had
        // to name a tenant in the body. ContractNumber is generated when empty.
        var client = Client(Tenant, ChoRolePermissions.ProviderRelations);

        var response = await client.PostAsJsonAsync("/api/v1/contracts", PortalCreateBody());

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var created = await response.Content.ReadFromJsonAsync<ProviderContract>(JsonOptions);
        created!.TenantId.Should().Be(Tenant);
        created.ContractNumber.Should().Be($"CTR-1234567890-{DateTime.UtcNow.Year}");
    }

    // ── Writes cannot bypass lifecycle or actor rules ─────────────────────

    [Fact]
    public async Task Put_CannotChangeStatusOrAmendments()
    {
        // Before: PUT replaced the whole document, so a body with
        // "status": "Active" activated a Draft contract without the activate
        // transition, and a body could rewrite amendments and who approved them.
        Seed(Tenant);
        var client = Client(Tenant, ChoRolePermissions.ProviderRelations);
        var body = Seed(Tenant, "body-only");
        body.Id = "c-1";
        body.ProviderName = "Dr. Updated";
        body.Status = ProviderContractStatus.Active;
        body.Amendments[0].ApprovedBy = "someone-else";
        body.Amendments[0].Description = "Rewritten";

        var response = await client.PutAsJsonAsync("/api/v1/contracts/c-1", body, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var stored = Stored(Tenant);
        stored.ProviderName.Should().Be("Dr. Updated");
        stored.Status.Should().Be(ProviderContractStatus.Draft);
        stored.Amendments.Should().ContainSingle();
        stored.Amendments[0].ApprovedBy.Should().Be("original-approver");
        stored.Amendments[0].Description.Should().Be("Original amendment");
        stored.CreatedBy.Should().Be("original-user");
    }

    [Fact]
    public async Task AddAmendment_ApprovedByIsTheTokenUser_NotTheBody()
    {
        // Before: ApprovedBy was whatever the request body said.
        Seed(Tenant);
        var client = Client(Tenant, ChoRolePermissions.ProviderRelations);

        var response = await client.PostAsJsonAsync("/api/v1/contracts/c-1/amendments", new
        {
            effectiveDate = "2026-06-01T00:00:00Z",
            amendmentType = "RateChange",
            description = "Mid-term rate change",
            approvedBy = "someone-else"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var added = Stored(Tenant).Amendments.Single(a => a.Description == "Mid-term rate change");
        added.ApprovedBy.Should().Be(User);
    }

    // ── Permissions ───────────────────────────────────────────────────────

    [Fact]
    public async Task ProviderRelations_CanReadAndWrite()
    {
        Seed(Tenant);
        var client = Client(Tenant, ChoRolePermissions.ProviderRelations);

        (await client.GetAsync("/api/v1/contracts/c-1")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/contracts/c-1/rate-configs")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PutAsync("/api/v1/contracts/c-1/activate", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        Stored(Tenant).Status.Should().Be(ProviderContractStatus.Active);
    }

    [Theory]
    [InlineData(ChoRolePermissions.Finance)]
    [InlineData(ChoRolePermissions.ComplianceOfficer)]
    public async Task ReadOnlyRoles_CanRead_ButNotWrite(string role)
    {
        Seed(Tenant);
        var client = Client(Tenant, role);

        var read = await client.GetAsync("/api/v1/contracts/c-1");
        var create = await client.PostAsJsonAsync("/api/v1/contracts", PortalCreateBody());
        var activate = await client.PutAsync("/api/v1/contracts/c-1/activate", null);
        var terminate = await client.PutAsJsonAsync("/api/v1/contracts/c-1/terminate", new { reason = "x" });

        read.StatusCode.Should().Be(HttpStatusCode.OK);
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        activate.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        terminate.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Store.Writes.Should().Be(0);
        Stored(Tenant).Status.Should().Be(ProviderContractStatus.Draft);
    }

    [Theory]
    [InlineData(ChoRolePermissions.ClaimsExaminer)]
    [InlineData(ChoRolePermissions.MemberServices)]
    [InlineData(ChoRolePermissions.EnrollmentSpecialist)]
    [InlineData(ChoRolePermissions.FinanceApprover)]
    public async Task RolesWithoutContractsRead_AreForbidden(string role)
    {
        Seed(Tenant);
        var client = Client(Tenant, role);

        var list = await client.GetAsync("/api/v1/contracts");
        var get = await client.GetAsync("/api/v1/contracts/c-1");

        list.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        get.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Store.TenantsSeen.Should().BeEmpty();
    }

    [Fact]
    public async Task ServiceToken_CanReadInItsTenant()
    {
        Seed(Tenant);
        var token = ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("capitation-service", Tenant);
        var client = BearerClient(token);

        var response = await client.GetAsync("/api/v1/contracts/c-1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Store.TenantsSeen.Should().OnlyContain(t => t == Tenant);
    }

    // ── TIN visibility ────────────────────────────────────────────────────

    private static async Task<string?> TinOf(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.GetProperty("providerTin").GetString();
    }

    [Theory]
    [InlineData(ChoRolePermissions.Finance)]
    [InlineData(ChoRolePermissions.ComplianceOfficer)]
    public async Task ReadOnlyRoles_GetMaskedTin(string role)
    {
        // Before: GET {id} returned the full TIN to every contracts:read holder.
        Seed(Tenant);
        var client = Client(Tenant, role);

        (await TinOf(await client.GetAsync("/api/v1/contracts/c-1"))).Should().Be("***-**-6789");
        (await TinOf(await client.GetAsync("/api/v1/contracts/number/CTR-c-1"))).Should().Be("***-**-6789");
    }

    [Fact]
    public async Task ContractsWriter_GetsFullTin()
    {
        Seed(Tenant);
        var client = Client(Tenant, ChoRolePermissions.ProviderRelations);

        (await TinOf(await client.GetAsync("/api/v1/contracts/c-1"))).Should().Be("123456789");
    }

    [Fact]
    public async Task ServiceToken_GetsFullTin()
    {
        Seed(Tenant);
        var token = ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("capitation-service", Tenant);

        (await TinOf(await BearerClient(token).GetAsync("/api/v1/contracts/c-1"))).Should().Be("123456789");
    }

    [Fact]
    public async Task Search_PageSizeAboveMax_Is400()
    {
        Seed(Tenant);
        var client = Client(Tenant, ChoRolePermissions.ProviderRelations);

        (await client.GetAsync("/api/v1/contracts?pageSize=201")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/v1/contracts?pageSize=200")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Put_CannotSetVerificationOrTermination()
    {
        Seed(Tenant);
        var client = Client(Tenant, ChoRolePermissions.ProviderRelations);
        var body = PortalCreateBody();
        body["providerName"] = "Dr. Updated";
        body["integrityScore"] = 100;
        body["integrityRating"] = "High";
        body["lastVerifiedAt"] = "2026-09-01T00:00:00Z";
        body["terminationDate"] = "2026-06-30T00:00:00Z";
        body["terminationReason"] = "Set by PUT";

        var response = await client.PutAsJsonAsync("/api/v1/contracts/c-1", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var stored = Stored(Tenant);
        stored.ProviderName.Should().Be("Dr. Updated");
        stored.IntegrityScore.Should().BeNull();
        stored.IntegrityRating.Should().BeNull();
        stored.LastVerifiedAt.Should().BeNull();
        stored.TerminationDate.Should().BeNull();
        stored.TerminationReason.Should().BeNull();
        stored.Status.Should().Be(ProviderContractStatus.Draft);
    }

    [Fact]
    public async Task HealthEndpoint_NeedsNoToken()
    {
        var response = await _factory.CreateClient().GetAsync("/health/live");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }
}
