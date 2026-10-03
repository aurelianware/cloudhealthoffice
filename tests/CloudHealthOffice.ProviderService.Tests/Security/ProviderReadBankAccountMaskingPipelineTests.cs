using System.Net;
using System.Net.Http.Headers;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.ProviderService.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using ProviderService.Adapters;
using ProviderService.Controllers;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Security;

/// <summary>
/// Provider reads (by id, by NPI, search, list, versions, write responses)
/// never return a full bank-account, routing or tax number inside the bank
/// account, whoever reads them. Through the real pipeline and serializer;
/// the provider store is a mock and the bank-account store the in-memory fake.
/// </summary>
public class ProviderReadBankAccountMaskingPipelineTests : IClassFixture<ProviderReadBankAccountMaskingPipelineTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<ProvidersController>
    {
        public Mock<IProviderRepository> Providers { get; } = new();
        public Mock<IProviderVersioningService> Versioning { get; } = new();
        public InMemoryProviderBankAccountRepository BankAccounts { get; set; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("MongoDb:ConnectionString", "mongodb://fake-host:27017");
            builder.UseSetting("Services:TenantService", "http://tenant-service.cloudhealthoffice/api/v1");
            builder.UseSetting("ProviderVerification:BaseUrl", "http://provider-verification-service");
            builder.ConfigureServices(services =>
            {
                foreach (var hosted in services
                             .Where(d => d.ServiceType == typeof(IHostedService)
                                         && d.ImplementationType?.Namespace?.StartsWith("ProviderService") == true)
                             .ToList())
                {
                    services.Remove(hosted);
                }

                services.RemoveAll<IProviderRepository>();
                services.AddSingleton(_ => Providers.Object);
                services.RemoveAll<IProviderVersioningService>();
                services.AddSingleton(_ => Versioning.Object);
                services.RemoveAll<IProviderBankAccountRepository>();
                services.AddScoped<IProviderBankAccountRepository>(_ => BankAccounts);
                // tenant-service answers 404: the tenant uses the CHO directory (ChoProviderAdapter).
                services.AddHttpClient(ProviderTenantConfigCache.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new NotFoundHandler());
            });
        }
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
    }

    private const string Tenant = "tenant-1";
    private const string Npi = "1234567890";
    private const string RowAccount = "111122223333";
    private const string RowRouting = "091000019";
    private const string RowTaxId = "12-3456789";
    private const string ApprovedAccount = "999988887777";
    private const string ApprovedRouting = "021000089";
    private const string ProviderTin = "98-7654321";

    private readonly Factory _factory;

    public ProviderReadBankAccountMaskingPipelineTests(Factory factory)
    {
        _factory = factory;
        _factory.Providers.Reset();
        _factory.Versioning.Reset();
        _factory.BankAccounts = new InMemoryProviderBankAccountRepository();
        _factory.Services.GetRequiredService<ProviderTenantConfigCache>().Clear();

        _factory.Providers.Setup(r => r.GetByIdAsync("p-1")).ReturnsAsync(() => Row());
        _factory.Providers.Setup(r => r.GetByNPIAsync(Npi)).ReturnsAsync(() => Row());
        _factory.Providers.Setup(r => r.SearchAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<LineOfBusiness?>(), It.IsAny<ProviderType?>(), It.IsAny<bool?>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(() => new[] { Row() });
        _factory.Versioning.Setup(v => v.ListVersionsAsync("p-1", It.IsAny<int>(), It.IsAny<string?>()))
            .ReturnsAsync(() => ((IReadOnlyList<Provider>)new[] { Row() }, (string?)null));
        _factory.Versioning.Setup(v => v.GetVersionAsync("p-1", "v-1")).ReturnsAsync(() => Row());
        _factory.Versioning.Setup(v => v.AmendActiveProviderAsync("p-1", It.IsAny<string>()))
            .ReturnsAsync(() => { var d = Row(); d.VersionId = "v-2"; d.VersionState = ProviderVersionState.Draft; return d; });
    }

    /// <summary>A provider row carrying a full bank account, as rows written before dual control do.</summary>
    private static Provider Row() => new()
    {
        Id = "p-1", ProviderId = "p-1", TenantId = Tenant, NPI = Npi, TaxId = ProviderTin,
        ProviderType = ProviderType.Individual, FirstName = "Ada", LastName = "Lovelace",
        PrimarySpecialty = "Internal Medicine", TaxonomyCode = "207R00000X",
        VersionId = "v-1", VersionNumber = 1, VersionState = ProviderVersionState.Active, Status = ProviderStatus.Active,
        BankAccount = new ProviderBankAccount
        {
            EftEnabled = true,
            PreferredDisbursementMethod = DisbursementMethod.NachaCredit,
            RoutingNumber = RowRouting,
            AccountNumber = RowAccount,
            RoutingNumberLast4 = "0019",
            AccountNumberLast4 = "3333",
            AccountHolderName = "Ada Lovelace MD",
            TaxId = RowTaxId,
            TaxIdType = TaxIdType.EIN,
        },
    };

    /// <summary>
    /// MemberServices with providers:read (the grant made on
    /// fix/security-authn-tenant-isolation, e8e8446), as explicit permissions.
    /// </summary>
    private HttpClient MemberServices()
    {
        var permissions = ChoRolePermissions.ForRole(ChoRolePermissions.MemberServices).Append("providers:read").ToArray();
        var client = _factory.CreateDefaultClient();
        var token = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(
            "user-member-services", Tenant, [ChoRolePermissions.MemberServices], permissions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private HttpClient ProviderRelations()
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("user-pr", ChoRolePermissions.ProviderRelations));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    public static IEnumerable<object[]> ReadPaths() => new[]
    {
        "/api/v1/providers/p-1",
        "/api/Providers/p-1",
        "/api/v1/providers/npi/" + Npi,
        "/api/Providers/npi/" + Npi,
        "/api/v1/providers/search?name=Ada",
        "/api/v1/providers/list",
        "/api/v1/providers/p-1/versions",
        "/api/v1/providers/p-1/versions/v-1",
    }.SelectMany(path => new[] { new object[] { "MemberServices", path }, new object[] { "ProviderRelations", path } });

    private static void ShouldHaveNoFullNumbers(string body)
    {
        body.Should().NotContain(RowAccount).And.NotContain(RowRouting).And.NotContain(RowTaxId)
            .And.NotContain(ApprovedAccount).And.NotContain(ApprovedRouting);
    }

    [Theory]
    [MemberData(nameof(ReadPaths))]
    public async Task Provider_reads_never_return_full_bank_numbers(string reader, string path)
    {
        var client = reader == "MemberServices" ? MemberServices() : ProviderRelations();

        var response = await client.GetAsync(path);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("3333", "the masked last 4 are still shown");
        body.Should().Contain(ProviderTin, "the provider's own TIN is not part of the bank account and is unchanged");
        ShouldHaveNoFullNumbers(body);
    }

    [Fact]
    public async Task Write_responses_mask_the_bank_account()
    {
        var response = await ProviderRelations().PostAsync("/api/v1/providers/p-1/amend", null);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        ShouldHaveNoFullNumbers(body);
    }

    [Theory]
    [InlineData("/api/v1/providers/p-1")]
    [InlineData("/api/v1/providers/npi/" + Npi)]
    public async Task Single_provider_reads_show_the_approved_account_not_the_stale_row_copy(string path)
    {
        var record = new ProviderBankAccountRecord
        {
            TenantId = Tenant, ProviderId = "p-1", ProviderNpi = Npi, ActiveChangeId = "change-1",
            Active = new ProviderBankAccount
            {
                EftEnabled = true, PreferredDisbursementMethod = DisbursementMethod.NachaCredit,
                RoutingNumber = ApprovedRouting, AccountNumber = ApprovedAccount,
                RoutingNumberLast4 = "0089", AccountNumberLast4 = "7777",
            },
        };
        (await _factory.BankAccounts.SaveAsync(record, 0)).Should().BeTrue();

        var body = await (await MemberServices().GetAsync(path)).Content.ReadAsStringAsync();

        body.Should().Contain("7777").And.Contain("0089");
        body.Should().NotContain("\"3333\"");
        ShouldHaveNoFullNumbers(body);
    }
}
