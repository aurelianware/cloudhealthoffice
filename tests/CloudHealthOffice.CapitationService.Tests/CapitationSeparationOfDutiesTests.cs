using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CapitationService.Models;
using CapitationService.Services;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.CapitationService.Tests;

/// <summary>
/// The capitation API factory with the real run and disbursement services (and the
/// real maker-checker rule) over mocked repositories; only the tenant's
/// payment-controls setting is stubbed.
/// </summary>
public class CapitationSeparationOfDutiesFactory : CapitationApiFactory
{
    public ITenantPaymentControls PaymentControls { get; } = Substitute.For<ITenantPaymentControls>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            var replaced = new[] { typeof(ICapitationRunService), typeof(ICapitationDisbursementService), typeof(ITenantPaymentControls) };
            foreach (var descriptor in services.Where(d => replaced.Contains(d.ServiceType)).ToList())
                services.Remove(descriptor);

            services.AddSingleton(PaymentControls);
            services.AddScoped<ICapitationRunService, CapitationRunService>();
            services.AddScoped<ICapitationDisbursementService, CapitationDisbursementService>();
        });
    }
}

/// <summary>
/// Maker-checker through the real pipeline: a FinanceApprover who created or
/// executed the run cannot approve or release its statements (403 problem), a
/// different FinanceApprover can, and a tenant that turned the rule off can.
/// </summary>
public class CapitationSeparationOfDutiesTests : IClassFixture<CapitationSeparationOfDutiesFactory>
{
    private const string Tenant = "tenant-sod";
    private const string Maker = "maker-1";
    private readonly CapitationSeparationOfDutiesFactory _factory;

    public CapitationSeparationOfDutiesTests(CapitationSeparationOfDutiesFactory factory)
    {
        _factory = factory;
        _factory.PaymentControls.IsSeparationOfDutiesEnforcedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.StatementRepository.UpdateAsync(Arg.Any<CapitationStatement>())
            .Returns(ci => ci.Arg<CapitationStatement>());
    }

    private CapitationStatement Statement(string id, CapitationStatementStatus status = CapitationStatementStatus.Generated)
    {
        var statement = new CapitationStatement
        {
            Id = id,
            StatementNumber = $"CAPSTMT-{id}",
            CapitationRunId = "run-sod",
            ProviderNPI = "1234567890",
            Status = status,
            NetPayable = 1000m,
            CreatedBy = Maker,
            RunCreatedBy = Maker
        };
        _factory.StatementRepository.GetByIdAsync(id).Returns(statement);
        return statement;
    }

    private HttpClient Approver(string subject)
        => _factory.CreateTenantClient(Tenant, subject, ChoRolePermissions.FinanceApprover);

    private static async Task AssertSeparationOfDutiesProblem(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Separation of duties", body.RootElement.GetProperty("title").GetString());
        Assert.Contains("you prepared capitation statement", body.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Approve_ByTheUserWhoPreparedTheStatement_Returns403()
    {
        var statement = Statement("s-sod-1");

        var response = await Approver(Maker).PutAsync("/api/v1/capitation/statements/s-sod-1/approve", null);

        await AssertSeparationOfDutiesProblem(response);
        Assert.Equal(CapitationStatementStatus.Generated, statement.Status);
        Assert.Null(statement.ApprovedBy);
    }

    [Fact]
    public async Task Approve_ByADifferentUser_Succeeds()
    {
        var statement = Statement("s-sod-2");

        var response = await Approver("checker-2").PutAsync("/api/v1/capitation/statements/s-sod-2/approve", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CapitationStatementStatus.Approved, statement.Status);
        Assert.Equal("checker-2", statement.ApprovedBy);
    }

    [Fact]
    public async Task Approve_ByTheMaker_WhenTenantTurnedTheRuleOff_Succeeds()
    {
        var statement = Statement("s-sod-3");
        _factory.PaymentControls.IsSeparationOfDutiesEnforcedAsync(Tenant, Arg.Any<CancellationToken>()).Returns(false);
        try
        {
            var response = await Approver(Maker).PutAsync("/api/v1/capitation/statements/s-sod-3/approve", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(Maker, statement.ApprovedBy);
        }
        finally
        {
            _factory.PaymentControls.IsSeparationOfDutiesEnforcedAsync(Tenant, Arg.Any<CancellationToken>()).Returns(true);
        }
    }

    [Fact]
    public async Task Release_ByTheUserWhoPreparedTheStatement_Returns403()
    {
        Statement("s-sod-4", CapitationStatementStatus.Approved);

        var single = await Approver(Maker).PostAsJsonAsync("/api/v1/capitation/disbursements",
            new { statementId = "s-sod-4" });
        await AssertSeparationOfDutiesProblem(single);

        var batch = await Approver(Maker).PostAsJsonAsync("/api/v1/capitation/disbursements/batch",
            new { statementIds = new[] { "s-sod-4" } });
        await AssertSeparationOfDutiesProblem(batch);

        await _factory.DisbursementRepository.DidNotReceive().CreateAsync(
            Arg.Is<CapitationDisbursement>(d => d.StatementId == "s-sod-4"));
    }

    [Fact]
    public async Task NachaFile_ContainingAPaymentTheUserPrepared_Returns403()
    {
        Statement("s-sod-5", CapitationStatementStatus.PaymentInitiated);
        _factory.DisbursementRepository.GetByStatusAsync(DisbursementStatus.Pending).Returns(new List<CapitationDisbursement>
        {
            new() { Id = "d-sod-5", StatementId = "s-sod-5", Method = DisbursementMethod.NachaCredit, Amount = 1000m }
        });

        var response = await Approver(Maker).PostAsync("/api/v1/capitation/disbursements/nacha-file", null);

        await AssertSeparationOfDutiesProblem(response);
    }
}
