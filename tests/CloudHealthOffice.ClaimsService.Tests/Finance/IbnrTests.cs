using ClaimsService.Controllers;
using ClaimsService.Finance;
using ClaimsService.Models;
using ClaimsService.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace CloudHealthOffice.ClaimsService.Tests.Finance;

/// <summary>
/// The paid-claims lag triangle and chain-ladder IBNR for the monthly
/// Medicaid close (gap 4 of the Medicaid core assessment).
/// </summary>
public class IbnrTests
{
    private static DateOnly M(int year, int month) => new(year, month, 1);
    private static DateTime D(int year, int month, int day = 15) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Increments_CountAnAdjustmentsDifference_AndAVoidTakesItAllBack()
    {
        var versions = new[]
        {
            new ClaimPaymentVersion("C1", 1, D(2026, 1), D(2026, 2), 100m, false),
            new ClaimPaymentVersion("C1", 2, D(2026, 1), D(2026, 4), 130m, false),   // adjusted up
            new ClaimPaymentVersion("C2", 1, D(2026, 1), D(2026, 1), 50m, false),
            new ClaimPaymentVersion("C2", 2, D(2026, 1), D(2026, 3), 0m, true),      // voided
            new ClaimPaymentVersion("C3", 1, D(2026, 2), null, 0m, false),           // not paid yet
        };

        var increments = LagTriangle.Increments(versions);

        increments.Should().BeEquivalentTo(new[]
        {
            new PaidIncrement("C1", M(2026, 1), M(2026, 2), 100m),
            new PaidIncrement("C1", M(2026, 1), M(2026, 4), 30m),
            new PaidIncrement("C2", M(2026, 1), M(2026, 1), 50m),
            new PaidIncrement("C2", M(2026, 1), M(2026, 3), -50m),
        });
    }

    [Fact]
    public void Triangle_IsCumulativeByLag_AndIgnoresPaymentsAfterTheValuation()
    {
        var increments = new[]
        {
            new PaidIncrement("a", M(2026, 1), M(2026, 1), 60m),
            new PaidIncrement("b", M(2026, 1), M(2026, 2), 30m),
            new PaidIncrement("c", M(2026, 1), M(2026, 3), 10m),
            new PaidIncrement("d", M(2026, 2), M(2026, 2), 66m),
            new PaidIncrement("e", M(2026, 2), M(2026, 4), 99m),  // after valuation
        };

        var t = LagTriangle.Build(increments, M(2026, 1), M(2026, 3), M(2026, 3));

        t.Rows.Select(r => r.IncurredMonth).Should().Equal(M(2026, 1), M(2026, 2), M(2026, 3));
        t.Rows[0].CumulativePaid.Should().Equal(60m, 90m, 100m);
        t.Rows[1].CumulativePaid.Should().Equal(66m, 66m);
        t.Rows[2].CumulativePaid.Should().Equal(0m);
    }

    /// <summary>
    /// A steady 60/30/10 payment pattern: every month should develop to 100%
    /// of its ultimate, so a month paid 60 at lag 0 has ultimate 100.
    /// </summary>
    [Fact]
    public void ChainLadder_RecoversAStablePattern()
    {
        var increments = new List<PaidIncrement>();
        for (var i = 0; i < 6; i++)
        {
            var incurred = M(2026, 1).AddMonths(i);
            var ultimate = 1000m + 100m * i;
            increments.Add(new("x", incurred, incurred, ultimate * 0.6m));
            increments.Add(new("x", incurred, incurred.AddMonths(1), ultimate * 0.3m));
            increments.Add(new("x", incurred, incurred.AddMonths(2), ultimate * 0.1m));
        }
        var triangle = LagTriangle.Build(increments, M(2026, 1), M(2026, 6), M(2026, 6));

        var estimate = IbnrEstimator.Estimate(triangle, new IbnrOptions { PmpmFallbackBelowCompletion = 0m });

        estimate.DevelopmentFactors[0].Should().Be(1.5m);
        estimate.CompletionFactors.Should().Equal(0.6m, 0.9m, 1m, 1m, 1m, 1m);
        var june = estimate.Months.Single(m => m.IncurredMonth == M(2026, 6));
        june.PaidToDate.Should().Be(900m);
        june.Ultimate.Should().Be(1500m);
        june.Ibnr.Should().Be(600m);
        estimate.Months.Single(m => m.IncurredMonth == M(2026, 5)).Ibnr.Should().Be(140m);  // 1400 ultimate, 1260 paid
        estimate.Months.Where(m => m.IncurredMonth <= M(2026, 4)).Should().OnlyContain(m => m.Ibnr == 0m);
        estimate.TotalIbnr.Should().Be(740m);
    }

    [Fact]
    public void ImmatureMonths_UseProjectedPmpm_WhenMemberMonthsAreGiven()
    {
        var increments = new List<PaidIncrement>();
        for (var i = 0; i < 4; i++)
        {
            var incurred = M(2026, 1).AddMonths(i);
            increments.Add(new("x", incurred, incurred, 200m));
            increments.Add(new("x", incurred, incurred.AddMonths(1), 600m));
            increments.Add(new("x", incurred, incurred.AddMonths(2), 200m));
        }
        var triangle = LagTriangle.Build(increments, M(2026, 1), M(2026, 4), M(2026, 4));

        var estimate = IbnrEstimator.Estimate(triangle, new IbnrOptions
        {
            ExpectedPmpm = 410m,
            MemberMonths = new Dictionary<DateOnly, int> { [M(2026, 4)] = 3 },
        });

        var april = estimate.Months.Single(m => m.IncurredMonth == M(2026, 4));
        april.CompletionFactor.Should().Be(0.2m);
        april.Method.Should().Be(UltimateMethod.ProjectedPmpm);
        april.Ultimate.Should().Be(1230m);
        april.UltimatePmpm.Should().Be(410m);
        // March is 80% complete: developed normally.
        estimate.Months.Single(m => m.IncurredMonth == M(2026, 3)).Method.Should().Be(UltimateMethod.ChainLadder);
    }

    [Fact]
    public async Task Endpoint_ReadsTheIncurredWindow_AndRefusesBadMonths()
    {
        var repo = new Mock<IClaimRepository>();
        repo.Setup(r => r.GetPaymentVersionsAsync(D(2026, 1, 1), D(2026, 4, 1), LineOfBusiness.Medicaid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ClaimPaymentVersion("C1", 1, D(2026, 1), D(2026, 1), 60m, false),
                new ClaimPaymentVersion("C2", 1, D(2026, 1), D(2026, 2), 40m, false),
                new ClaimPaymentVersion("C3", 1, D(2026, 2), D(2026, 2), 75m, false),
            });
        var controller = new ClaimsFinanceController(repo.Object);

        var result = await controller.Ibnr(new IbnrRequest
        {
            IncurredFrom = "2026-01", IncurredTo = "2026-03", Valuation = "2026-03", LineOfBusiness = LineOfBusiness.Medicaid
        }, default);

        var body = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<IbnrResponse>().Subject;
        body.ClaimVersionsRead.Should().Be(3);
        body.Triangle.Should().HaveCount(3);
        // Lag 0->1 from January (60 -> 100) and February (75 -> 75): 175 / 135.
        body.DevelopmentFactors[0].Should().Be(1.2963m);
        body.Estimate.Single(e => e.IncurredMonth == "2026-01").Ultimate.Should().Be(100m);
        body.Estimate.Single(e => e.IncurredMonth == "2026-03").PaidToDate.Should().Be(0m);
        body.TotalPaid.Should().Be(175m);

        (await controller.Ibnr(new IbnrRequest { IncurredFrom = "2026-04", IncurredTo = "2026-03", Valuation = "2026-03" }, default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await controller.Ibnr(new IbnrRequest { IncurredFrom = "Jan", IncurredTo = "2026-03", Valuation = "2026-03" }, default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
    }
}
