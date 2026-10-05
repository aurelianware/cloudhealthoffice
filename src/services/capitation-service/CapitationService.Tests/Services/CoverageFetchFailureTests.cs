using System.Net;
using System.Text;
using CapitationService.Models;
using CapitationService.Repositories;
using CapitationService.Services;
using CapitationService.Tests.Support;
using Microsoft.Extensions.Logging;

namespace CapitationService.Tests.Services;

/// <summary>
/// The PCP's member list comes from coverage-service. A failure to read it
/// (401/403, 404, 5xx, transport, unreadable or missing body) must hold the
/// statement and mark the run as needing attention, never produce an empty
/// member list that pays the PCP nothing and looks complete. A successful
/// empty list (a PCP with no members) is a normal statement.
/// </summary>
public class CoverageFetchFailureTests
{
    private static readonly DateTime Period = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ICapitationRunRepository> _runs = new();
    private readonly Mock<ICapitationContractRepository> _contracts = new();
    private readonly Mock<ICapitationStatementRepository> _statements = new();
    private readonly Mock<IHttpClientFactory> _http = new();
    private readonly Mock<ILogger<CapitationRunService>> _logger = new();
    private readonly List<CapitationStatement> _saved = new();
    private readonly CapitationRunService _service;

    public CoverageFetchFailureTests()
    {
        var run = new CapitationRun
        {
            Id = "run-1", TenantId = "tenant-1", RunNumber = "CAPRUN-MCR-2026-03-COV",
            CapitationPeriod = Period, Status = CapitationRunStatus.Pending, Criteria = new CapitationRunCriteria()
        };
        _runs.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);
        _runs.Setup(r => r.UpdateAsync(It.IsAny<CapitationRun>())).ReturnsAsync((CapitationRun r) => r);
        _statements.Setup(r => r.CreateAsync(It.IsAny<CapitationStatement>()))
            .Callback<CapitationStatement>(_saved.Add)
            .ReturnsAsync((CapitationStatement s) => s);
        _contracts.Setup(r => r.GetActiveContractsAsync(It.IsAny<LineOfBusiness?>(), It.IsAny<ContractType?>()))
            .ReturnsAsync(new List<CapitationContract>
            {
                new()
                {
                    Id = "contract-1", ContractNumber = "CAP-1234567890-2026", ProviderNPI = "1234567890", ProviderName = "Dr. Smith",
                    ContractType = ContractType.PrimaryCareOnly, LineOfBusiness = LineOfBusiness.Medicare,
                    Status = CapitationRateConfigStatus.Active, EffectiveDate = new DateTime(2026, 1, 1),
                    RiskAdjusted = false, DefaultRiskScore = 1.0m, WithholdPercentage = 0,
                    RateTiers = [new() { TierName = "Adult", AgeFrom = 18, AgeTo = 64, BasePMPM = 100m }]
                }
            });

        _service = new CapitationRunService(_runs.Object, _contracts.Object, _statements.Object, _http.Object,
            TestSeparationOfDuties.Create(runs: _runs.Object), _logger.Object);
    }

    private void Coverage(Func<HttpResponseMessage> answer)
    {
        var client = new HttpClient(new FuncHandler(_ => answer())) { BaseAddress = new Uri("http://coverage-service") };
        _http.Setup(f => f.CreateClient("CoverageService")).Returns(client);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private async Task<CapitationRun> RunAndExpectHeld(string detailFragment)
    {
        var result = await _service.ExecuteRunAsync("run-1");

        var statement = Assert.Single(_saved);
        statement.LineItems.Should().BeEmpty();
        statement.Status.Should().Be(CapitationStatementStatus.OnHold);
        statement.RequiresAttention.Should().BeTrue();
        statement.MemberIssues.Should().ContainSingle(i =>
            i.Reason == CapitationRunService.CoverageUnavailable && i.ProviderNPI == "1234567890" && i.Detail.Contains(detailFragment));
        statement.Adjustments.Should().Contain(a => a.Description.Contains("member list could not be read"));

        result.Status.Should().Be(CapitationRunStatus.Completed);
        result.RequiresAttention.Should().BeTrue();
        result.MembersNeedingAttention.Should().ContainSingle(i => i.Reason == CapitationRunService.CoverageUnavailable);
        result.Errors.Should().ContainSingle(e => e.Contains("1234567890") && e.Contains("could not be read"));
        _logger.Verify(l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.AtLeastOnce);
        return result;
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task FailedCoverageFetch_HoldsTheStatement_RunNeedsAttention(HttpStatusCode status)
    {
        Coverage(() => new HttpResponseMessage(status));
        await RunAndExpectHeld(((int)status).ToString());
    }

    [Fact]
    public async Task TransportFailure_HoldsTheStatement()
    {
        Coverage(() => throw new HttpRequestException("connection refused"));
        await RunAndExpectHeld("unreachable");
    }

    [Fact]
    public async Task UnreadableBody_HoldsTheStatement()
    {
        Coverage(() => Json(HttpStatusCode.OK, "<html>gateway</html>"));
        await RunAndExpectHeld("unreadable");
    }

    [Fact]
    public async Task NullBody_HoldsTheStatement()
    {
        Coverage(() => Json(HttpStatusCode.OK, "null"));
        await RunAndExpectHeld("no member list");
    }

    [Fact]
    public async Task EmptyMemberList_IsANormalStatement()
    {
        Coverage(() => Json(HttpStatusCode.OK, "[]"));

        var result = await _service.ExecuteRunAsync("run-1");

        var statement = Assert.Single(_saved);
        statement.Status.Should().Be(CapitationStatementStatus.Generated);
        statement.RequiresAttention.Should().BeFalse();
        result.RequiresAttention.Should().BeFalse();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task MembersReturned_ArePaid()
    {
        Coverage(() => Json(HttpStatusCode.OK,
            """[{"coverageId":"cov-1","memberId":"MEM001","planId":"P1","effectiveDate":"2025-01-01T00:00:00Z","dateOfBirth":"2000-06-15T00:00:00Z","gender":"M","pcpNpi":"1234567890"}]"""));

        var result = await _service.ExecuteRunAsync("run-1");

        Assert.Single(Assert.Single(_saved).LineItems).GrossAmount.Should().Be(100m);
        result.RequiresAttention.Should().BeFalse();
    }

    private sealed class FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
