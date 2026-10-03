using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CapitationService.Models;
using CapitationService.Repositories;
using CapitationService.Services;
using CapitationService.Tests.Services;
using CapitationService.Tests.Support;

namespace CapitationService.Tests.Security;

/// <summary>
/// Maker-checker on capitation payments: the user who created or executed the
/// capitation run that produced a statement cannot approve that statement or
/// release its payment, unless the tenant turned the rule off (which is logged).
/// </summary>
public class SeparationOfDutiesTests
{
    private const string Maker = "maker-1";
    private const string Checker = "checker-2";

    private readonly Mock<ICapitationStatementRepository> _statements = new();
    private readonly Mock<ICapitationRunRepository> _runs = new();
    private readonly Mock<ICapitationDisbursementRepository> _disbursements = new();
    private readonly Mock<INachaCreditFileService> _nacha = new();
    private readonly ListLogger<PaymentSeparationOfDuties> _sodLog = new();

    public SeparationOfDutiesTests()
    {
        _statements.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>()))
            .ReturnsAsync((CapitationStatement s) => s);
        _disbursements.Setup(r => r.CreateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);
    }

    private CapitationStatement Statement(
        string id = "stmt-1",
        CapitationStatementStatus status = CapitationStatementStatus.Generated,
        string? createdBy = Maker,
        string? runCreatedBy = Maker,
        string runId = "run-1")
    {
        var statement = new CapitationStatement
        {
            Id = id,
            StatementNumber = $"CAPSTMT-1234567890-2026-03-{id}",
            CapitationRunId = runId,
            ProviderNPI = "1234567890",
            ProviderName = "Dr. Smith",
            Status = status,
            NetPayable = 5000m,
            CreatedBy = createdBy,
            RunCreatedBy = runCreatedBy
        };
        _statements.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(statement);
        return statement;
    }

    private PaymentSeparationOfDuties Rule(bool enforced = true)
        => TestSeparationOfDuties.Create(enforced, _runs.Object, _sodLog);

    private CapitationRunService RunService(bool enforced = true)
        => new(_runs.Object, Mock.Of<ICapitationContractRepository>(), _statements.Object,
            Mock.Of<IHttpClientFactory>(), Rule(enforced), Mock.Of<ILogger<CapitationRunService>>());

    private CapitationDisbursementService DisbursementService(bool enforced = true)
    {
        var handler = new MockHttpMessageHandler<ProviderBankAccountDto>(_ => new ProviderBankAccountDto
        {
            EftEnabled = true,
            PreferredDisbursementMethod = "Check",
            RoutingNumber = "091000019",
            AccountNumber = "123456789",
            AccountType = "Checking"
        });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("ProviderService"))
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("http://provider-service") });

        return new CapitationDisbursementService(_disbursements.Object, _statements.Object, _runs.Object,
            _nacha.Object, Mock.Of<IStripeConnectService>(), factory.Object,
            new ConfigurationBuilder().Build(), Rule(enforced), Mock.Of<ILogger<CapitationDisbursementService>>());
    }

    // ── Approve ────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_ByUserWhoExecutedTheRun_IsRefused()
    {
        Statement(createdBy: Maker, runCreatedBy: "someone-else");

        var act = () => RunService().ApproveStatementAsync("stmt-1", Maker);

        (await act.Should().ThrowAsync<SeparationOfDutiesException>())
            .Which.Message.Should().Contain("cannot approve");
        _statements.Verify(r => r.UpdateAsync(It.IsAny<CapitationStatement>()), Times.Never);
    }

    [Fact]
    public async Task Approve_ByUserWhoCreatedTheRun_IsRefused()
    {
        Statement(createdBy: "executor-9", runCreatedBy: Maker);

        var act = () => RunService().ApproveStatementAsync("stmt-1", Maker);

        await act.Should().ThrowAsync<SeparationOfDutiesException>();
        _statements.Verify(r => r.UpdateAsync(It.IsAny<CapitationStatement>()), Times.Never);
    }

    [Fact]
    public async Task Approve_ComparesUserIdsCaseInsensitively()
    {
        Statement();

        var act = () => RunService().ApproveStatementAsync("stmt-1", Maker.ToUpperInvariant());

        await act.Should().ThrowAsync<SeparationOfDutiesException>();
    }

    [Fact]
    public async Task Approve_ByDifferentUser_Succeeds()
    {
        Statement();

        var result = await RunService().ApproveStatementAsync("stmt-1", Checker);

        result.Status.Should().Be(CapitationStatementStatus.Approved);
        result.ApprovedBy.Should().Be(Checker);
        _sodLog.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Approve_LegacyStatement_FallsBackToTheRunsRecordedCreator()
    {
        // Generated before statements recorded makers: only the system placeholder.
        Statement(createdBy: CapitationStatement.SystemCreator, runCreatedBy: null);
        _runs.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(new CapitationRun { Id = "run-1", CreatedBy = Maker });

        var act = () => RunService().ApproveStatementAsync("stmt-1", Maker);

        await act.Should().ThrowAsync<SeparationOfDutiesException>();
    }

    [Fact]
    public async Task Approve_LegacyStatementWithNoRecordedCreator_IsAllowedAndLogged()
    {
        Statement(createdBy: CapitationStatement.SystemCreator, runCreatedBy: null);
        _runs.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(new CapitationRun { Id = "run-1", CreatedBy = null });

        var result = await RunService().ApproveStatementAsync("stmt-1", Maker);

        result.Status.Should().Be(CapitationStatementStatus.Approved);
        _sodLog.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning
            && e.EventId == PaymentSeparationOfDuties.NoMakerRecordedEvent
            && e.Message.Contains("stmt-1") && e.Message.Contains(Maker));
    }

    [Fact]
    public async Task Approve_ByMaker_WhenTenantTurnedTheRuleOff_IsAllowedAndAudited()
    {
        Statement();

        var result = await RunService(enforced: false).ApproveStatementAsync("stmt-1", Maker);

        result.Status.Should().Be(CapitationStatementStatus.Approved);
        result.ApprovedBy.Should().Be(Maker);
        _sodLog.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning
            && e.EventId == PaymentSeparationOfDuties.OverrideAuditEvent
            && e.Message.Contains(Maker)
            && e.Message.Contains("stmt-1")
            && e.Message.Contains("CAPSTMT-1234567890-2026-03-stmt-1")
            && e.Message.Contains("tenant-1"));
    }

    [Fact]
    public async Task Approve_ByDifferentUser_WhenTenantTurnedTheRuleOff_WritesNoOverrideAudit()
    {
        Statement();

        await RunService(enforced: false).ApproveStatementAsync("stmt-1", Checker);

        _sodLog.Entries.Should().BeEmpty();
    }

    // ── Release ────────────────────────────────────────────────────────

    [Fact]
    public async Task Release_ByMaker_IsRefusedBeforeAnyMoneyMoves()
    {
        Statement(status: CapitationStatementStatus.Approved);

        var act = () => DisbursementService().InitiateDisbursementAsync(
            new InitiateDisbursementRequest { StatementId = "stmt-1", InitiatedBy = Maker });

        (await act.Should().ThrowAsync<SeparationOfDutiesException>())
            .Which.Message.Should().Contain("cannot release");
        _disbursements.Verify(r => r.CreateAsync(It.IsAny<CapitationDisbursement>()), Times.Never);
        _statements.Verify(r => r.UpdateAsync(It.IsAny<CapitationStatement>()), Times.Never);
    }

    [Fact]
    public async Task Release_ByDifferentUser_Succeeds()
    {
        Statement(status: CapitationStatementStatus.Approved);

        var disbursement = await DisbursementService().InitiateDisbursementAsync(
            new InitiateDisbursementRequest { StatementId = "stmt-1", InitiatedBy = Checker });

        disbursement.InitiatedBy.Should().Be(Checker);
        _disbursements.Verify(r => r.CreateAsync(It.IsAny<CapitationDisbursement>()), Times.Once);
    }

    [Fact]
    public async Task Release_ByMaker_WhenTenantTurnedTheRuleOff_IsAllowedAndAudited()
    {
        Statement(status: CapitationStatementStatus.Approved);

        await DisbursementService(enforced: false).InitiateDisbursementAsync(
            new InitiateDisbursementRequest { StatementId = "stmt-1", InitiatedBy = Maker });

        _disbursements.Verify(r => r.CreateAsync(It.IsAny<CapitationDisbursement>()), Times.Once);
        _sodLog.Entries.Should().Contain(e => e.EventId == PaymentSeparationOfDuties.OverrideAuditEvent
                                              && e.Message.Contains(Maker) && e.Message.Contains("Release"));
    }

    [Fact]
    public async Task BatchRelease_IncludingAStatementTheUserPrepared_IsRefusedAsAWhole()
    {
        Statement("stmt-ok", CapitationStatementStatus.Approved, createdBy: "other", runCreatedBy: "other");
        Statement("stmt-mine", CapitationStatementStatus.Approved);

        var act = () => DisbursementService().InitiateBatchDisbursementAsync(new InitiateBatchDisbursementRequest
        {
            StatementIds = ["stmt-ok", "stmt-mine"],
            InitiatedBy = Maker
        });

        await act.Should().ThrowAsync<SeparationOfDutiesException>();
        _disbursements.Verify(r => r.CreateAsync(It.IsAny<CapitationDisbursement>()), Times.Never);
    }

    [Fact]
    public async Task BatchRelease_ByDifferentUser_Succeeds()
    {
        Statement("stmt-a", CapitationStatementStatus.Approved);
        Statement("stmt-b", CapitationStatementStatus.Approved);

        var result = await DisbursementService().InitiateBatchDisbursementAsync(new InitiateBatchDisbursementRequest
        {
            StatementIds = ["stmt-a", "stmt-b"],
            InitiatedBy = Checker
        });

        result.DisbursementsInitiated.Should().Be(2);
        result.Errors.Should().Be(0);
    }

    [Fact]
    public async Task NachaFile_ContainingAPaymentTheUserPrepared_IsNotGenerated()
    {
        Statement(status: CapitationStatementStatus.PaymentInitiated);
        _disbursements.Setup(r => r.GetByStatusAsync(DisbursementStatus.Pending)).ReturnsAsync(new List<CapitationDisbursement>
        {
            new() { Id = "d-1", StatementId = "stmt-1", Method = DisbursementMethod.NachaCredit, Amount = 5000m, ProviderNPI = "1234567890" }
        });

        var act = () => DisbursementService().GenerateNachaCreditFileAsync(Maker);

        await act.Should().ThrowAsync<SeparationOfDutiesException>();
        _nacha.Verify(n => n.GenerateNachaCreditFile(It.IsAny<List<NachaCreditEntryDetail>>(), It.IsAny<NachaCreditFileOptions>()), Times.Never);
        _disbursements.Verify(r => r.UpdateAsync(It.IsAny<CapitationDisbursement>()), Times.Never);
    }

    [Fact]
    public async Task NachaFile_ByDifferentUser_IsGenerated()
    {
        Statement(status: CapitationStatementStatus.PaymentInitiated);
        _disbursements.Setup(r => r.GetByStatusAsync(DisbursementStatus.Pending)).ReturnsAsync(new List<CapitationDisbursement>
        {
            new() { Id = "d-1", StatementId = "stmt-1", Method = DisbursementMethod.NachaCredit, Amount = 5000m, ProviderNPI = "1234567890" }
        });
        _nacha.Setup(n => n.GenerateNachaCreditFile(It.IsAny<List<NachaCreditEntryDetail>>(), It.IsAny<NachaCreditFileOptions>()))
            .Returns(new NachaCreditFileResult { FileReference = "file-1" });

        var result = await DisbursementService().GenerateNachaCreditFileAsync(Checker);

        result.FileReference.Should().Be("file-1");
    }

    // ── Tenant setting ─────────────────────────────────────────────────

    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"configuration\":{}}", true)]
    [InlineData("{\"configuration\":{\"paymentControls\":{}}}", true)]
    [InlineData("{\"configuration\":{\"paymentControls\":{\"enforceSeparationOfDuties\":true}}}", true)]
    [InlineData("{\"configuration\":{\"paymentControls\":{\"enforceSeparationOfDuties\":\"false\"}}}", true)]
    [InlineData("{\"configuration\":{\"paymentControls\":{\"enforceSeparationOfDuties\":null}}}", true)]
    [InlineData("{\"configuration\":{\"paymentControls\":{\"enforceSeparationOfDuties\":false}}}", false)]
    public void TenantSetting_OnlyAnExplicitFalseTurnsTheRuleOff(string tenantJson, bool enforced)
    {
        TenantPaymentControls.ParseEnforced(tenantJson).Should().Be(enforced);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task TenantSetting_Unreadable_KeepsTheRuleOn(HttpStatusCode status)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(TenantPaymentControls.HttpClientName))
            .Returns(new HttpClient(new StatusHandler(status)) { BaseAddress = new Uri("http://tenant-service") });
        var controls = new TenantPaymentControls(factory.Object, Mock.Of<ILogger<TenantPaymentControls>>());

        (await controls.IsSeparationOfDutiesEnforcedAsync("tenant-1")).Should().BeTrue();
    }

    [Fact]
    public async Task TenantSetting_ReadFromTenantService()
    {
        string? requested = null;
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(TenantPaymentControls.HttpClientName))
            .Returns(() => new HttpClient(new StatusHandler(HttpStatusCode.OK,
                "{\"tenantId\":\"tenant-1\",\"configuration\":{\"paymentControls\":{\"enforceSeparationOfDuties\":false}}}",
                uri => requested = uri)) { BaseAddress = new Uri("http://tenant-service") });
        var controls = new TenantPaymentControls(factory.Object, Mock.Of<ILogger<TenantPaymentControls>>());

        (await controls.IsSeparationOfDutiesEnforcedAsync("tenant-1")).Should().BeFalse();
        requested.Should().Be("http://tenant-service/api/v1/tenants/tenant-1");
    }

    private sealed class StatusHandler(HttpStatusCode status, string body = "{}", Action<string>? seen = null)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            seen?.Invoke(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}

/// <summary>Captures formatted log entries for assertions.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, eventId, formatter(state, exception)));
}
