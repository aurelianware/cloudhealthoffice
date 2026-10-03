using System.Net;
using System.Net.Http.Json;
using CapitationService.Models;
using CapitationService.Repositories;
using CapitationService.Services;
using CapitationService.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CapitationService.Tests.Security;

/// <summary>
/// Capitation runs and disbursements can run with no inbound caller (a
/// scheduled run, a batch release). Their calls to coverage-service,
/// risk-adjustment-service, provider-service (the masked bank-account read)
/// and tenant-service must name the record's tenant, so the shared outbound
/// handler mints a service token for it. Without it they go out with no token
/// and the callee answers 401.
/// </summary>
public class BackgroundCallsCarryServiceTokenTests
{
    private const string Tenant = "tenant-cap";
    private const string ClientId = "capitation-service";

    private static NoCallerHost Host() => new(ClientId, (services, outbound) =>
    {
        services.AddHttpClient("CoverageService", c => c.BaseAddress = new Uri("http://coverage-service:8080"))
            .ConfigurePrimaryHttpMessageHandler(() => outbound);
        services.AddHttpClient("RiskAdjustmentService", c => c.BaseAddress = new Uri("http://risk-adjustment-service:8080"))
            .ConfigurePrimaryHttpMessageHandler(() => outbound);
        services.AddHttpClient("ProviderService", c => c.BaseAddress = new Uri("http://provider-service:8080"))
            .ConfigurePrimaryHttpMessageHandler(() => outbound);
        services.AddHttpClient(TenantPaymentControls.HttpClientName, c => c.BaseAddress = new Uri("http://tenant-service"))
            .ConfigurePrimaryHttpMessageHandler(() => outbound);
    });

    [Fact]
    public async Task CapitationRun_CoverageAndRiskScoreCalls_WithoutCaller_CarryServiceTokenForTheRunsTenant()
    {
        using var host = Host();
        host.Outbound.Respond = request => request.RequestUri!.AbsolutePath.StartsWith("/api/v1/coverage/by-pcp/")
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new List<CapitationCoverageDto>
                {
                    new()
                    {
                        CoverageId = "cov-1", MemberId = "MEM001", PlanId = "PLAN-1",
                        EffectiveDate = new DateTime(2025, 1, 1), DateOfBirth = new DateTime(2000, 6, 15),
                        Gender = "M", PcpNpi = "1234567890",
                    },
                }),
            }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new RiskScoreDto { RiskScore = 1.2m }) };

        var runs = new Mock<ICapitationRunRepository>();
        var contracts = new Mock<ICapitationContractRepository>();
        var statements = new Mock<ICapitationStatementRepository>();
        runs.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(new CapitationRun
        {
            Id = "run-1",
            TenantId = Tenant,
            RunNumber = "CAPRUN-2026-03-T",
            CapitationPeriod = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            Status = CapitationRunStatus.Pending,
            Criteria = new CapitationRunCriteria(),
        });
        runs.Setup(r => r.UpdateAsync(It.IsAny<CapitationRun>())).ReturnsAsync((CapitationRun r) => r);
        statements.Setup(r => r.CreateAsync(It.IsAny<CapitationStatement>())).ReturnsAsync((CapitationStatement s) => s);
        contracts.Setup(r => r.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>()))
            .ReturnsAsync(new List<CapitationContract>
            {
                new()
                {
                    Id = "contract-1", ContractNumber = "CAP-1", ProviderNPI = "1234567890", ProviderName = "Dr. Smith",
                    ContractType = ContractType.PrimaryCareOnly, LineOfBusiness = LineOfBusiness.Commercial,
                    Status = CapitationRateConfigStatus.Active, EffectiveDate = new DateTime(2026, 1, 1),
                    RiskAdjusted = true, DefaultRiskScore = 1.0m,
                    RateTiers = new List<CapitationRateTier>
                    {
                        new() { TierName = "Adult", AgeFrom = 18, AgeTo = 64, BasePMPM = 50m },
                    },
                },
            });

        var service = new CapitationRunService(runs.Object, contracts.Object, statements.Object,
            host.Services.GetRequiredService<IHttpClientFactory>(),
            TestSeparationOfDuties.Create(runs: runs.Object, tenantId: Tenant),
            Mock.Of<ILogger<CapitationRunService>>());

        await service.ExecuteRunAsync("run-1");

        var coverageCall = host.Outbound.Requests.Single(r => r.RequestUri!.AbsolutePath.StartsWith("/api/v1/coverage/by-pcp/"));
        var riskCall = host.Outbound.Requests.Single(r => r.RequestUri!.AbsolutePath.StartsWith("/api/risk-adjustment/"));
        NoCallerHost.TokenOf(coverageCall).Should().Be((Tenant, ClientId));
        NoCallerHost.TokenOf(riskCall).Should().Be((Tenant, ClientId));
    }

    [Fact]
    public async Task Disbursement_ProviderBankAccountRead_WithoutCaller_CarriesServiceTokenForTheStatementsTenant()
    {
        using var host = Host();
        host.Outbound.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ProviderBankAccountDto
            {
                EftEnabled = true,
                PreferredDisbursementMethod = "NachaCredit",
                RoutingNumber = "091000019",
                AccountNumber = "987654321",
            }),
        };

        var statements = new Mock<ICapitationStatementRepository>();
        var disbursements = new Mock<ICapitationDisbursementRepository>();
        var runs = new Mock<ICapitationRunRepository>();
        statements.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(new CapitationStatement
        {
            Id = "stmt-1", TenantId = Tenant, StatementNumber = "S-1", ProviderNPI = "1234567890",
            ProviderName = "Dr. Smith", Status = CapitationStatementStatus.Approved, NetPayable = 100m,
            GrossCapitation = 100m, CreatedBy = "maker",
        });
        statements.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>())).ReturnsAsync((CapitationStatement s) => s);
        disbursements.Setup(r => r.CreateAsync(It.IsAny<CapitationDisbursement>())).ReturnsAsync((CapitationDisbursement d) => d);

        var service = new CapitationDisbursementService(disbursements.Object, statements.Object, runs.Object,
            Mock.Of<INachaCreditFileService>(), Mock.Of<IStripeConnectService>(),
            host.Services.GetRequiredService<IHttpClientFactory>(),
            new ConfigurationBuilder().Build(),
            TestSeparationOfDuties.Create(runs: runs.Object, tenantId: Tenant),
            Mock.Of<ILogger<CapitationDisbursementService>>());

        await service.InitiateDisbursementAsync(new InitiateDisbursementRequest { StatementId = "stmt-1", InitiatedBy = "checker" });

        var call = host.Outbound.Requests.Single();
        call.RequestUri!.AbsolutePath.Should().Be("/api/providers/npi/1234567890/bank-account");
        NoCallerHost.TokenOf(call).Should().Be((Tenant, ClientId));
    }

    [Fact]
    public async Task PaymentControlsLookup_WithoutCaller_CarriesServiceTokenForNamedTenant()
    {
        using var host = Host();
        var controls = new TenantPaymentControls(
            host.Services.GetRequiredService<IHttpClientFactory>(), Mock.Of<ILogger<TenantPaymentControls>>());

        await controls.IsSeparationOfDutiesEnforcedAsync(Tenant);

        NoCallerHost.TokenOf(host.Outbound.Last).Should().Be((Tenant, ClientId));
    }
}
