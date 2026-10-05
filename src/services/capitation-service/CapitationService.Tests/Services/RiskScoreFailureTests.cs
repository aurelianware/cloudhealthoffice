using System.Net;
using System.Text;
using CapitationService.Models;
using CapitationService.Repositories;
using CapitationService.Services;
using CapitationService.Tests.Support;
using Microsoft.Extensions.Logging;

namespace CapitationService.Tests.Services;

/// <summary>
/// A risk-adjusted contract pays base PMPM × the member's risk score. A risk
/// score that cannot be read (401/403, 5xx, transport failure, unreadable body)
/// must fail that member's line visibly, never quietly become 1.0. A 404 (no
/// score for the measurement year) uses the contract's documented default.
/// </summary>
public class RiskScoreFailureTests
{
    private static readonly DateTime Period = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ICapitationRunRepository> _runs = new();
    private readonly Mock<ICapitationContractRepository> _contracts = new();
    private readonly Mock<ICapitationStatementRepository> _statements = new();
    private readonly Mock<IHttpClientFactory> _http = new();
    private readonly Mock<ILogger<CapitationRunService>> _logger = new();
    private readonly List<CapitationStatement> _saved = new();
    private readonly List<string> _riskPaths = new();
    private readonly CapitationRunService _service;
    private readonly CapitationRun _run;

    public RiskScoreFailureTests()
    {
        _run = new CapitationRun
        {
            Id = "run-1", TenantId = "tenant-1", RunNumber = "CAPRUN-MCR-2026-03-TEST",
            CapitationPeriod = Period, Status = CapitationRunStatus.Pending, Criteria = new CapitationRunCriteria()
        };
        _runs.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(_run);
        _runs.Setup(r => r.UpdateAsync(It.IsAny<CapitationRun>())).ReturnsAsync((CapitationRun r) => r);
        _statements.Setup(r => r.CreateAsync(It.IsAny<CapitationStatement>()))
            .Callback<CapitationStatement>(_saved.Add)
            .ReturnsAsync((CapitationStatement s) => s);
        _contracts.Setup(r => r.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>()))
            .ReturnsAsync(new List<CapitationContract> { Contract() });

        var coverage = new HttpClient(new FuncHandler(_ => Json(HttpStatusCode.OK,
            """[{"coverageId":"cov-1","memberId":"MEM001","planId":"P1","effectiveDate":"2025-01-01T00:00:00Z","dateOfBirth":"2000-06-15T00:00:00Z","gender":"M","pcpNpi":"1234567890"},""" +
            """{"coverageId":"cov-2","memberId":"MEM002","planId":"P1","effectiveDate":"2025-01-01T00:00:00Z","dateOfBirth":"2000-06-15T00:00:00Z","gender":"M","pcpNpi":"1234567890"}]""")))
        { BaseAddress = new Uri("http://coverage-service") };
        _http.Setup(f => f.CreateClient("CoverageService")).Returns(coverage);

        _service = new CapitationRunService(_runs.Object, _contracts.Object, _statements.Object, _http.Object,
            TestSeparationOfDuties.Create(runs: _runs.Object), _logger.Object);
    }

    private static CapitationContract Contract() => new()
    {
        Id = "contract-1", ContractNumber = "CAP-1234567890-2026", ProviderNPI = "1234567890", ProviderName = "Dr. Smith",
        ContractType = ContractType.PrimaryCareOnly, LineOfBusiness = LineOfBusiness.Medicare,
        Status = CapitationRateConfigStatus.Active, EffectiveDate = new DateTime(2026, 1, 1),
        RiskAdjusted = true, DefaultRiskScore = 0.9m, WithholdPercentage = 0,
        RateTiers = [new() { TierName = "Adult", AgeFrom = 18, AgeTo = 64, BasePMPM = 100m }]
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Risk-adjustment-service answers per member with <paramref name="answer"/>.</summary>
    private void RiskService(Func<string, HttpResponseMessage> answer)
    {
        var client = new HttpClient(new FuncHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (_riskPaths) _riskPaths.Add(path);
            return answer(path);
        }))
        { BaseAddress = new Uri("http://risk-adjustment-service") };
        _http.Setup(f => f.CreateClient("RiskAdjustmentService")).Returns(client);
    }

    private void VerifyErrorLogged() =>
        _logger.Verify(l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.AtLeastOnce);

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task FailedRiskScore_FailsTheMembersLine_NeverPaysADefault(HttpStatusCode status)
    {
        RiskService(_ => new HttpResponseMessage(status));

        var result = await _service.ExecuteRunAsync("run-1");

        var statement = Assert.Single(_saved);
        statement.LineItems.Should().BeEmpty("no member is paid at a made-up score");
        statement.GrossCapitation.Should().Be(0m);
        statement.Status.Should().Be(CapitationStatementStatus.OnHold);
        statement.RequiresAttention.Should().BeTrue();
        statement.MemberIssues.Select(i => i.MemberId).Should().BeEquivalentTo("MEM001", "MEM002");
        statement.MemberIssues.Should().OnlyContain(i =>
            i.Reason == CapitationMemberIssue.RiskScoreUnavailable && i.Detail.Contains(((int)status).ToString()));

        result.RequiresAttention.Should().BeTrue();
        result.MembersNeedingAttention.Select(i => i.MemberId).Should().BeEquivalentTo("MEM001", "MEM002");
        result.Errors.Should().ContainSingle(e => e.Contains("MEM001") && e.Contains("MEM002"));
        result.TotalGrossCapitation.Should().Be(0m);
        VerifyErrorLogged();
    }

    [Fact]
    public async Task TransportFailure_FailsTheMembersLine()
    {
        RiskService(_ => throw new HttpRequestException("connection refused"));

        var result = await _service.ExecuteRunAsync("run-1");

        var statement = Assert.Single(_saved);
        statement.LineItems.Should().BeEmpty();
        statement.Status.Should().Be(CapitationStatementStatus.OnHold);
        result.MembersNeedingAttention.Should().HaveCount(2);
        VerifyErrorLogged();
    }

    [Fact]
    public async Task SuccessWithoutAScore_FailsTheMembersLine()
    {
        RiskService(_ => Json(HttpStatusCode.OK, """{"memberId":"MEM001","measurementYear":2026}"""));

        var result = await _service.ExecuteRunAsync("run-1");

        Assert.Single(_saved).LineItems.Should().BeEmpty();
        result.RequiresAttention.Should().BeTrue();
    }

    [Fact]
    public async Task OneMemberFails_OtherMemberIsPaidAtItsScore_StatementHeld()
    {
        RiskService(path => path.Contains("MEM001")
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : Json(HttpStatusCode.OK, """{"memberId":"MEM002","measurementYear":2026,"riskScore":1.5}"""));

        var result = await _service.ExecuteRunAsync("run-1");

        var statement = Assert.Single(_saved);
        var line = Assert.Single(statement.LineItems);
        line.MemberId.Should().Be("MEM002");
        line.RiskScore.Should().Be(1.5m);
        line.RiskScoreSource.Should().Be(RiskScoreSources.Service);
        line.GrossAmount.Should().Be(150m);
        statement.Status.Should().Be(CapitationStatementStatus.OnHold);
        result.MembersNeedingAttention.Should().ContainSingle(i => i.MemberId == "MEM001" && i.StatementId == statement.Id);
    }

    [Fact]
    public async Task NoScoreForTheYear_UsesTheContractDefault_AndSaysSo()
    {
        RiskService(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await _service.ExecuteRunAsync("run-1");

        var statement = Assert.Single(_saved);
        statement.LineItems.Should().HaveCount(2);
        statement.LineItems.Should().OnlyContain(li =>
            li.RiskScore == 0.9m && li.RiskScoreSource == RiskScoreSources.NoScoreForYear && li.GrossAmount == 90m);
        statement.Status.Should().Be(CapitationStatementStatus.Generated);
        statement.RequiresAttention.Should().BeFalse();
        result.RequiresAttention.Should().BeFalse();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadsTheDiagnosisFreeSummaryEndpoint()
    {
        RiskService(_ => Json(HttpStatusCode.OK, """{"memberId":"MEM001","measurementYear":2026,"riskScore":1.2}"""));

        await _service.ExecuteRunAsync("run-1");

        _riskPaths.Should().BeEquivalentTo(
            "/api/risk-adjustment/members/MEM001/scores/2026/summary",
            "/api/risk-adjustment/members/MEM002/scores/2026/summary");
    }

    private sealed class FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
