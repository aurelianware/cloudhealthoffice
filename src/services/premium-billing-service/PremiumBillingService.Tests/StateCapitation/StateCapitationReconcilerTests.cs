using PremiumBillingService.Edi;
using PremiumBillingService.StateCapitation;

namespace PremiumBillingService.Tests.StateCapitation;

/// <summary>
/// Expected-versus-received state capitation (gap 4 of the Medicaid core
/// assessment): what the plan should have been paid by member, month and rate
/// cell, against what the state's 820s paid, for Texas and Medi-Cal.
/// </summary>
public class StateCapitationReconcilerTests
{
    private static readonly DateOnly Jan = new(2027, 1, 1);
    private static readonly DateOnly Feb = new(2027, 2, 1);

    // Illustrative rate cells and PMPMs, not published state rates.
    private static readonly StateCapitationRate[] TexasRates =
    [
        new("STAR-HARRIS-CHILD", new DateOnly(2026, 9, 1), new DateOnly(2027, 8, 31), 245.10m),
        new("STAR-HARRIS-ADULT", new DateOnly(2026, 9, 1), new DateOnly(2027, 8, 31), 410.55m),
    ];

    /// <summary>A Texas-shaped 820: one RMR per member-month, rate cell in REF*ZZ after it.</summary>
    private static string State820(string trace, decimal total, params (string Member, string Month, decimal Paid, string Cell, string? Adx)[] lines)
    {
        var body = new List<string>
        {
            "ST*820*0001*005010X218",
            $"BPR*C*{total:0.00}*C*ACH*CCP*01*111000025*DA*123456789*1746000000**01*031100649*DA*987654321*20270105",
            $"TRN*1*{trace}*1746000000",
            "N1*PE*LONE STAR HEALTH PLAN*FI*741234567",
            "N1*PR*TEXAS HHSC",
        };
        var n = 1;
        foreach (var l in lines)
        {
            var start = l.Month.Replace("-", "") + "01";
            var y = int.Parse(l.Month[..4]); var m = int.Parse(l.Month[5..]);
            var end = l.Month.Replace("-", "") + DateTime.DaysInMonth(y, m).ToString("00");
            body.Add($"ENT*{n++}*2J*34*{l.Member}");
            body.Add($"NM1*IL*1*DOE*JANE****C1*{l.Member}");
            body.Add($"RMR*AZ*{l.Member}{l.Month.Replace("-", "")}**{l.Paid:0.00}");
            body.Add($"REF*ZZ*{l.Cell}");
            body.Add($"DTM*582****RD8*{start}-{end}");
            if (l.Adx != null)
                body.Add($"ADX*{l.Paid:0.00}*{l.Adx}");
        }
        body.Add($"SE*{body.Count + 1}*0001");
        var segments = new List<string>
        {
            "ISA*00*          *00*          *ZZ*TXHHSC         *ZZ*LONESTAR       *270105*1200*^*00501*000000001*0*P*:",
            "GS*RA*TXHHSC*LONESTAR*20270105*1200*1*X*005010X218",
        };
        segments.AddRange(body);
        segments.Add("GE*1*1");
        segments.Add("IEA*1*000000001");
        return string.Join("~", segments) + "~";
    }

    [Fact]
    public void Parser_KeepsTheRateCellRefOnItsRemittanceLine()
    {
        var advice = Edi820Parser.Parse(State820("TRC1", 245.10m, ("M100", "2027-01", 245.10m, "STAR-HARRIS-CHILD", null))).Single();

        var item = advice.Items.Single();
        item.MemberId.Should().Be("M100");
        item.References.Should().ContainSingle().Which.Value.Should().Be("STAR-HARRIS-CHILD");
        item.CoveragePeriodStart.Should().Be(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Texas_ClassifiesEveryMemberMonth_AndRollsUpByRateCell()
    {
        var jan = Edi820Parser.Parse(State820("TRC-JAN", 900.75m,
            ("M100", "2027-01", 245.10m, "STAR-HARRIS-CHILD", null),   // paid in full
            ("M200", "2027-01", 200.00m, "STAR-HARRIS-CHILD", null),   // underpaid
            ("M300", "2027-01", 410.55m, "STAR-HARRIS-ADULT", null),   // plan says child: cell mismatch
            ("M999", "2027-01", 45.10m, "STAR-HARRIS-CHILD", null)));  // not ours
        // February 820 recoups M100's January (retro disenrollment) and pays February.
        var feb = Edi820Parser.Parse(State820("TRC-FEB", 0m,
            ("M100", "2027-01", -245.10m, "STAR-HARRIS-CHILD", "52"),
            ("M100", "2027-02", 245.10m, "STAR-HARRIS-CHILD", null)));

        var warnings = new List<string>();
        var received = StateCapitationReconciler.ReadReceived(StateCapitationProfile.Texas, jan.Concat(feb), warnings);
        var expected = new[]
        {
            new ExpectedMemberMonth("M100", Jan, "STAR-HARRIS-CHILD"),
            new ExpectedMemberMonth("M100", Feb, "STAR-HARRIS-CHILD"),
            new ExpectedMemberMonth("M200", Jan, "STAR-HARRIS-CHILD"),
            new ExpectedMemberMonth("M300", Jan, "STAR-HARRIS-CHILD"),
            new ExpectedMemberMonth("M400", Jan, "STAR-HARRIS-ADULT"),  // never paid
        };

        var result = StateCapitationReconciler.Reconcile(StateCapitationProfile.Texas, expected, TexasRates, received);

        MemberMonthOutcome Of(string member, DateOnly month) => result.MemberMonths.Single(m => m.MemberId == member && m.Month == month).Outcome;
        Of("M100", Jan).Should().Be(MemberMonthOutcome.Recouped);
        Of("M100", Feb).Should().Be(MemberMonthOutcome.PaidInFull);
        Of("M200", Jan).Should().Be(MemberMonthOutcome.Underpaid);
        Of("M300", Jan).Should().Be(MemberMonthOutcome.RateCellMismatch);
        Of("M400", Jan).Should().Be(MemberMonthOutcome.NotPaid);
        Of("M999", Jan).Should().Be(MemberMonthOutcome.Unexpected);

        var m100Jan = result.MemberMonths.Single(m => m.MemberId == "M100" && m.Month == Jan);
        m100Jan.TraceNumbers.Should().BeEquivalentTo(["TRC-JAN", "TRC-FEB"]);
        m100Jan.AdjustmentReasons.Should().Contain("52");
        result.MemberMonths.Single(m => m.MemberId == "M200").Variance.Should().Be(-45.10m);

        var childJan = result.Summary.Single(s => s.Month == Jan && s.RateCell == "STAR-HARRIS-CHILD");
        childJan.ExpectedMembers.Should().Be(3);   // M100, M200, M300
        childJan.Expected.Should().Be(3 * 245.10m);
        childJan.Received.Should().Be(0m + 200.00m + 410.55m + 45.10m);   // M100 netted to zero; M999 is in the child cell too
        result.Warnings.Should().Contain(w => w.Contains("companion guide"), "the built-in Texas profile is unverified");
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void MediCal_RatesFollowTheEffectivePeriod_AndAMissingRateIsSaid()
    {
        var profile = StateCapitationProfile.California;
        StateCapitationRate[] rates =
        [
            new("LA-ACA-EXP", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), 512.00m),
            new("LA-ACA-EXP", new DateOnly(2027, 1, 1), null, 530.25m),
        ];
        var received = new[]
        {
            new ReceivedMemberMonth("91000001A", new DateOnly(2026, 12, 1), "LA-ACA-EXP", 512.00m, "CA-1", null, []),
            new ReceivedMemberMonth("91000001A", Jan, "LA-ACA-EXP", 530.25m, "CA-2", null, []),
        };
        var expected = new[]
        {
            new ExpectedMemberMonth("91000001A", new DateOnly(2026, 12, 1), "LA-ACA-EXP"),
            new ExpectedMemberMonth("91000001A", Jan, "LA-ACA-EXP"),
            new ExpectedMemberMonth("91000002B", Jan, "LA-SPD"),   // no rate loaded for this cell
        };

        var result = StateCapitationReconciler.Reconcile(profile, expected, rates, received);

        result.MemberMonths.Where(m => m.MemberId == "91000001A").Should().OnlyContain(m => m.Outcome == MemberMonthOutcome.PaidInFull);
        var spd = result.MemberMonths.Single(m => m.MemberId == "91000002B");
        spd.Outcome.Should().Be(MemberMonthOutcome.NoRate);
        spd.Expected.Should().BeNull();
        result.TotalReceived.Should().Be(1042.25m);
    }

    [Fact]
    public void Proration_PaysPartOfAMonth_WhenTheProfileSaysSo()
    {
        var profile = StateCapitationProfile.Texas with { ProrateByDays = true };
        var expected = new[] { new ExpectedMemberMonth("M1", Feb, "STAR-HARRIS-ADULT", DaysEnrolled: 14) };
        var received = new[] { new ReceivedMemberMonth("M1", Feb, "STAR-HARRIS-ADULT", 205.28m, "T", null, []) };

        var line = StateCapitationReconciler.Reconcile(profile, expected, TexasRates, received).MemberMonths.Single();

        line.Expected.Should().Be(205.28m);   // 410.55 * 14 / 28
        line.Outcome.Should().Be(MemberMonthOutcome.PaidInFull);
    }

    [Fact]
    public void ALineSpanningMonths_IsSplit_AndALineWithoutAPeriodIsReported()
    {
        var advice = new PremiumBillingService.Models.RemittanceAdvice
        {
            TraceNumber = "T-SPAN",
            Items =
            {
                new() { LineNumber = 1, Level = PremiumBillingService.Models.RemittanceLevel.Individual, MemberId = "M1", Amount = 100.01m,
                        CoveragePeriodStart = new DateTime(2027, 1, 1), CoveragePeriodEnd = new DateTime(2027, 2, 28) },
                new() { LineNumber = 2, Level = PremiumBillingService.Models.RemittanceLevel.Individual, MemberId = "M2", Amount = 10m },
            }
        };
        var warnings = new List<string>();

        var received = StateCapitationReconciler.ReadReceived(StateCapitationProfile.California, [advice], warnings);

        received.Select(r => (r.Month, r.Amount)).Should().Equal((Jan, 50.01m), (Feb, 50.00m));
        warnings.Should().HaveCount(2);
    }
}
