using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services.Adjudication.Shadow;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Shadow;

public class ShadowPendedAnalysisTests
{
    private static readonly DateTime AsOf = new(2026, 11, 23);
    private static readonly DateTime Since = AsOf.AddDays(-365);

    [Fact]
    public void EachPendReason_CountsWhatChoWouldDo_AndAgreementWithTheExaminer()
    {
        var results = new[]
        {
            Pended("c1", "MR01", ShadowOutcome.Paid, choPaid: 60m, pendedOn: new(2026, 3, 1)),
            Pended("c2", "MR01", ShadowOutcome.Paid, choPaid: 60m, pendedOn: new(2026, 3, 1)),
            Pended("c3", "MR01", ShadowOutcome.Denied, pendedOn: new(2026, 3, 1)),
            Pended("c4", "MR01", ShadowOutcome.Pended, choPendCode: "PRICING"),
            Pended("c5", "COB", ShadowOutcome.Paid, choPaid: 40m, pendedOn: new(2026, 11, 3)),
        };
        var current = new Dictionary<string, Claim>
        {
            ["c1"] = Final(ClaimStatus.Paid, paid: 60m, decidedOn: new(2026, 3, 11)),
            ["c2"] = Final(ClaimStatus.Paid, paid: 75m, decidedOn: new(2026, 3, 21)),
            ["c3"] = Final(ClaimStatus.Paid, paid: 50m, decidedOn: new(2026, 3, 6)),
            ["c4"] = Final(ClaimStatus.Denied, paid: 0m, decidedOn: new(2026, 3, 1)),
            ["c5"] = StillPendedInCore(),
        };

        var analysis = ShadowPendedAnalysisBuilder.Build(results, current, 0.01m, AsOf, Since);

        var mr = analysis.ByPendReason[0];
        Assert.Equal("MR01", mr.PendReasonCode);
        Assert.Equal("Medical records requested", mr.PendReason);
        Assert.Equal(4, mr.Claims);
        Assert.Equal(3, mr.ChoWouldResolve);
        Assert.Equal(2, mr.ChoWouldPay);
        Assert.Equal(1, mr.ChoWouldDeny);
        Assert.Equal(1, mr.ChoWouldPend);
        Assert.Equal(["PRICING (1)"], mr.ChoPendCodes);
        Assert.Equal(0.75m, mr.AutoResolveRate);
        Assert.Equal(4, mr.ExaminerDecided);
        Assert.Equal(3, mr.Compared);
        Assert.Equal(1, mr.Agreed);
        Assert.Equal(1, mr.PaidAmountDiffers);
        Assert.Equal(1, mr.ChoDeniedExaminerPaid);
        Assert.Equal(0.3333m, mr.AgreementRate);
        // 10, 20 and 5 days; c4 has no pended date and no received date.
        Assert.Equal(11.7m, mr.AverageDaysPended);
        Assert.Equal(0, mr.StillPended);
        Assert.False(mr.AutomationCandidate);

        var cob = analysis.ByPendReason[1];
        Assert.Equal(1, cob.StillPended);
        Assert.Equal(20, cob.OldestStillPendedDays);
        Assert.Equal(0, cob.Compared);
        Assert.Null(cob.AgreementRate);

        Assert.Equal(5, analysis.Total.Claims);
        Assert.Equal(4, analysis.Total.ChoWouldResolve);
        Assert.Equal(1, analysis.Total.StillPended);
    }

    [Fact]
    public void AClaimRunTwice_CountsOnce_WithItsLatestResult()
    {
        var older = Pended("c1", "MR01", ShadowOutcome.Pended, choPendCode: "PRICING");
        older.CreatedAtUtc = AsOf.AddDays(-10);
        var newer = Pended("c1", "MR01", ShadowOutcome.Paid, choPaid: 60m);
        newer.CreatedAtUtc = AsOf.AddDays(-1);

        var analysis = ShadowPendedAnalysisBuilder.Build([older, newer], new Dictionary<string, Claim>(), 0.01m, AsOf, Since);

        Assert.Equal(1, analysis.Total.Claims);
        Assert.Equal(1, analysis.Total.ChoWouldResolve);
    }

    [Fact]
    public void AReasonIsAnAutomationCandidate_OnlyWithEnoughClaimsResolvedAndAgreeing()
    {
        var results = Enumerable.Range(1, 20)
            .Select(i => Pended($"c{i}", "DUP", ShadowOutcome.Denied, pendedOn: new(2026, 5, 1)))
            .ToList();
        var current = results.ToDictionary(r => r.ClaimId, _ => Final(ClaimStatus.Denied, 0m, new DateTime(2026, 5, 15)));

        var row = Assert.Single(ShadowPendedAnalysisBuilder.Build(results, current, 0.01m, AsOf, Since).ByPendReason);
        Assert.Equal(1m, row.AgreementRate);
        Assert.Equal(14m, row.AverageDaysPended);
        Assert.True(row.AutomationCandidate);

        // One fewer claim, and it is not.
        results.RemoveAt(0);
        row = Assert.Single(ShadowPendedAnalysisBuilder.Build(results, current, 0.01m, AsOf, Since).ByPendReason);
        Assert.False(row.AutomationCandidate);
    }

    [Fact]
    public void ChoLackingDataToDecide_IsNotResolution_ButAProviderScreeningHoldIs()
    {
        var memberMissing = Pended("c1", "ELIG", ShadowOutcome.Denied, choPendCode: ShadowDiscrepancyComparer.MemberNotLoadedCode);
        var screening = Pended("c2", "ELIG", ShadowOutcome.Paid, choPaid: 10m, choPendCode: ShadowDiscrepancyComparer.ProviderScreeningCode);
        screening.HeldForProviderScreening = true;
        var voided = Pended("c3", "ELIG", ShadowOutcome.Paid, choPaid: 10m);

        var row = ShadowPendedAnalysisBuilder.Build([memberMissing, screening, voided],
            new Dictionary<string, Claim> { ["c3"] = Final(ClaimStatus.Voided, 0m, AsOf) }, 0.01m, AsOf, Since).Total;

        Assert.Equal(2, row.ChoWouldResolve);
        Assert.Equal(1, row.ChoWouldPend);
        Assert.Equal(1, row.Voided);
        // c1 and c2 have no final decision yet.
        Assert.Equal(2, row.StillPended);
    }

    [Fact]
    public async Task TheService_ReadsEachClaimsCurrentVersion()
    {
        var store = new InMemoryShadowAdjudicationRepository();
        await store.SaveRunAsync(new ShadowAdjudicationRun { TenantId = "tenant-1" },
        [
            Pended("c1", "MR01", ShadowOutcome.Paid, choPaid: 60m, pendedOn: new(2026, 3, 1)),
            new() { TenantId = "tenant-1", ClaimId = "c2", Kind = ShadowDiscrepancyKind.Match, CreatedAtUtc = AsOf },
        ]);
        var claims = Substitute.For<IClaimRepository>();
        claims.GetLatestVersionAsync("c1", Arg.Any<DateTime>()).Returns(Final(ClaimStatus.Paid, 60m, new DateTime(2026, 3, 8)));
        var service = new ShadowPendedAnalysisService(store, claims,
            Options.Create(new ShadowAdjudicationOptions()), new FixedClock(AsOf));

        var analysis = await service.BuildAsync("tenant-1", 365);

        Assert.Equal(1, analysis.Total.Claims);
        Assert.Equal(1, analysis.Total.Agreed);
        Assert.Equal(7m, analysis.Total.AverageDaysPended);
        Assert.False(analysis.Truncated);
        await claims.DidNotReceive().GetLatestVersionAsync("c2", Arg.Any<DateTime>());
    }

    private static ShadowAdjudicationResult Pended(
        string claimId, string pendCode, string choOutcome, decimal choPaid = 0m, string? choPendCode = null,
        DateTime? pendedOn = null) => new()
    {
        TenantId = "tenant-1",
        ClaimId = claimId,
        Kind = ShadowDiscrepancyKind.NotComparable,
        CoreOutcome = ShadowOutcome.NotFinal,
        ChoOutcome = choOutcome,
        ChoPaid = choPaid,
        ChoPendCode = choPendCode,
        CorePended = true,
        CorePendCode = pendCode,
        CorePendReason = pendCode == "MR01" ? "Medical records requested" : null,
        CorePendedDate = pendedOn,
        CreatedAtUtc = AsOf.AddDays(-1),
    };

    private static Claim Final(ClaimStatus status, decimal paid, DateTime decidedOn) => new()
    {
        Status = status,
        AdjudicatedDate = decidedOn,
        AdjudicationResult = new AdjudicationResult { PayerPayment = paid },
        ExternalSource = new ClaimExternalSource { Format = "cpcds" },
    };

    private static Claim StillPendedInCore() => new()
    {
        Status = ClaimStatus.Received,
        ExternalSource = new ClaimExternalSource { Format = "cpcds", CorePend = new ClaimCorePend { Code = "COB" } },
    };

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }
}
