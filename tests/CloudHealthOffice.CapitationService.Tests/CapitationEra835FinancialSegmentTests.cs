using CapitationService.Models;
using CapitationService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.CapitationService.Tests;

/// <summary>
/// Element positions of the capitation 835's BPR and TRN, built by the shared
/// Era835FinancialSegmentBuilder (005010X221A1): BPR10 is the originating
/// company id (Era:OriginatingCompanyId), BPR11 its supplemental code, the
/// receiver bank in BPR12-BPR15 and the date in BPR16, for CHK/NON too.
/// TRN03 is the originating company id, never the payer id.
/// </summary>
public class CapitationEra835FinancialSegmentTests
{
    private const string OriginatingCompanyId = "1987654321";

    private static CapitationEraService Service(string? originatingCompanyId = OriginatingCompanyId, string? supplemental = null)
    {
        var settings = new Dictionary<string, string?>();
        if (originatingCompanyId != null) settings["Era:OriginatingCompanyId"] = originatingCompanyId;
        if (supplemental != null) settings["Era:OriginatingCompanySupplementalCode"] = supplemental;
        return new CapitationEraService(
            NullLogger<CapitationEraService>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    private static CapitationStatement Statement(decimal net = 126.00m, string? checkNumber = null)
    {
        var statement = new CapitationStatement
        {
            Id = "stmt-1",
            StatementNumber = "CAPSTMT-1-2026-03",
            ContractId = "contract-1",
            ProviderNPI = "1234567890",
            ProviderName = "Provider",
            CapitationPeriodStart = new DateTime(2026, 3, 1),
            CapitationPeriodEnd = new DateTime(2026, 3, 31),
            PaymentDate = new DateTime(2026, 4, 1),
            CheckNumber = checkNumber,
        };
        if (net != 0m)
        {
            statement.LineItems.Add(new CapitationLineItem
            {
                MemberId = "MEM001",
                GrossAmount = net,
                NetAmount = net,
                BasePMPM = net,
                RiskScore = 1.0m,
                AssignmentEffectiveDate = new DateTime(2026, 1, 1),
            });
        }
        statement.RecalculateTotals();
        return statement;
    }

    private static readonly CapitationContract Contract = new() { Id = "contract-1", ContractNumber = "CAP-1" };

    private static CapitationEraTradingPartnerInfo Ach() => new()
    {
        PayerName = "Plan",
        PayerId = "PLANID",
        PayerRoutingNumber = "091000019",
        PayerAccountNumber = "1111111111",
        PayeeRoutingNumber = "021000089",
        PayeeAccountNumber = "2222222222",
    };

    private static string[] Segment(string edi, string id)
        => edi.Split('~').First(s => s.StartsWith(id + "*", StringComparison.Ordinal)).Split('*');

    [Fact]
    public void Ach_Bpr_ElementPositions()
    {
        var statement = Statement();
        var bpr = Segment(Service(supplemental: "SUPPCODE1").Generate835ForStatement(statement, Contract, Ach()), "BPR");

        Assert.Equal(
            new[]
            {
                "BPR", "C", statement.NetPayable.ToString("F2"), "C", "ACH", "CCP", "01", "091000019", "DA", "1111111111",
                OriginatingCompanyId, "SUPPCODE1", "01", "021000089", "DA", "2222222222", "20260401",
            },
            bpr);
    }

    [Fact]
    public void Check_Bpr_DateIsBpr16()
    {
        var tp = new CapitationEraTradingPartnerInfo { PayerName = "Plan", PayerId = "PLANID" };
        var bpr = Segment(Service().Generate835ForStatement(Statement(checkNumber: "000123"), Contract, tp), "BPR");

        Assert.Equal(17, bpr.Length);
        Assert.Equal("CHK", bpr[4]);
        Assert.All(bpr.Skip(5).Take(11), e => Assert.Equal(string.Empty, e));
        Assert.Equal("20260401", bpr[16]);
    }

    [Fact]
    public void NonPayment_Bpr_DateIsBpr16()
    {
        var tp = new CapitationEraTradingPartnerInfo { PayerName = "Plan", PayerId = "PLANID" };
        var bpr = Segment(Service().Generate835ForStatement(Statement(), Contract, tp), "BPR");

        Assert.Equal(17, bpr.Length);
        Assert.Equal("NON", bpr[4]);
        Assert.Equal("20260401", bpr[16]);
    }

    [Fact]
    public void ZeroNetPayable_IsNonPayment_EvenWithBankDetails()
    {
        var bpr = Segment(Service().Generate835ForStatement(Statement(net: 0m), Contract, Ach()), "BPR");

        Assert.Equal(new[] { "H", "0.00", "C", "NON" }, bpr.Skip(1).Take(4));
        Assert.All(bpr.Skip(5).Take(11), e => Assert.Equal(string.Empty, e));
        Assert.Equal("20260401", bpr[16]);
    }

    [Fact]
    public void NegativeNetPayable_Bpr02Zero_BalanceCarriedForwardInPlb()
    {
        var statement = Statement(net: 0m);
        statement.Adjustments.Add(new CapitationAdjustment
        {
            Type = CapitationAdjustmentType.RetroDisenrollment,
            Description = "Retro term",
            Amount = -40m,
        });
        statement.RecalculateTotals();
        Assert.Equal(-40m, statement.NetPayable);

        var edi = Service().Generate835ForStatement(statement, Contract, Ach());

        var bpr = Segment(edi, "BPR");
        Assert.Equal(new[] { "H", "0.00", "C", "NON" }, bpr.Skip(1).Take(4));
        var plb = Segment(edi, "PLB");
        Assert.Contains($"*FB:{statement.StatementNumber}*-40.00", string.Join("*", plb));
    }

    private static void AssertBalanced(string edi)
    {
        var segments = edi.Split('~', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split('*')).ToList();
        var bpr02 = decimal.Parse(segments.Single(s => s[0] == "BPR")[2]);
        var clp04 = segments.Where(s => s[0] == "CLP").Sum(s => decimal.Parse(s[4]));
        var plb = segments.Where(s => s[0] == "PLB")
            .Sum(s => Enumerable.Range(0, (s.Length - 3) / 2).Sum(k => decimal.Parse(s[4 + 2 * k])));
        Assert.True(bpr02 >= 0m);
        Assert.Equal(bpr02, clp04 - plb);
    }

    [Theory]
    [InlineData(0, 0, 0)]       // members only, withhold in CLP04
    [InlineData(25, 0, 0)]      // incentive credit (negative PLB)
    [InlineData(0, -30, 0)]     // retro recoupment (positive PLB)
    [InlineData(15, -10, 20)]   // incentive, recoupment and withhold release
    [InlineData(0, -500, 0)]    // recoupment exceeding the month: BPR02 0, FB carries the rest
    public void Balances_Clp04LessPlb_EqualsBpr02(int incentive, int retro, int withholdRelease)
    {
        var statement = Statement(net: 0m);
        statement.LineItems.Add(new CapitationLineItem
        {
            MemberId = "MEM001", GrossAmount = 100m, WithholdAmount = 10m, NetAmount = 90m,
            BasePMPM = 100m, RiskScore = 1.0m, AssignmentEffectiveDate = new DateTime(2026, 3, 1),
        });
        statement.LineItems.Add(new CapitationLineItem
        {
            MemberId = "MEM002", GrossAmount = 60m, WithholdAmount = 6m, NetAmount = 54m,
            BasePMPM = 60m, RiskScore = 1.0m, AssignmentEffectiveDate = new DateTime(2026, 3, 1),
        });
        if (incentive != 0)
            statement.Adjustments.Add(new CapitationAdjustment { Type = CapitationAdjustmentType.IncentivePayment, Description = "Incentive", Amount = incentive });
        if (retro != 0)
            statement.Adjustments.Add(new CapitationAdjustment { Type = CapitationAdjustmentType.RetroDisenrollment, Description = "Retro term", Amount = retro });
        if (withholdRelease != 0)
            statement.Adjustments.Add(new CapitationAdjustment { Type = CapitationAdjustmentType.WithholdRelease, Description = "Release", Amount = withholdRelease });
        statement.RecalculateTotals();

        var edi = Service().Generate835ForStatement(statement, Contract, Ach());

        AssertBalanced(edi);
        Assert.Equal(Math.Max(statement.NetPayable, 0m).ToString("F2"), Segment(edi, "BPR")[2]);
    }

    [Fact]
    public void ClaimLoop_CasDirectlyAfterClp_ThenNm1_ThenDtm232()
    {
        var statement = Statement(net: 0m);
        statement.LineItems.Add(new CapitationLineItem
        {
            MemberId = "MEM001", MemberName = "Jo Doe", GrossAmount = 100m, WithholdAmount = 10m, NetAmount = 90m,
            BasePMPM = 100m, RiskScore = 1.2m, AssignmentEffectiveDate = new DateTime(2026, 3, 1),
        });
        statement.RecalculateTotals();

        var ids = Service().Generate835ForStatement(statement, Contract, Ach())
            .Split('~', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Split('*'))
            .SkipWhile(s => s[0] != "CLP")
            .TakeWhile(s => s[0] is not ("PLB" or "SE"))
            .Select(s => s[0] == "DTM" ? "DTM*" + s[1] : s[0])
            .ToList();

        Assert.Equal(new[] { "CLP", "CAS", "NM1", "DTM*232", "AMT", "QTY" }, ids);
    }

    [Fact]
    public void UnbalancedStatement_Throws()
    {
        var statement = Statement(net: 0m);
        statement.LineItems.Add(new CapitationLineItem
        {
            MemberId = "MEM001", GrossAmount = 100m, WithholdAmount = 10m, NetAmount = 95m, // should be 90
            BasePMPM = 100m, RiskScore = 1.0m, AssignmentEffectiveDate = new DateTime(2026, 3, 1),
        });
        statement.RecalculateTotals();

        var ex = Assert.Throws<InvalidOperationException>(
            () => Service().Generate835ForStatement(statement, Contract, Ach()));
        Assert.Contains("unbalanced", ex.Message);
    }

    [Fact]
    public void Trn03_IsOriginatingCompanyId()
    {
        var statement = Statement();
        var trn = Segment(Service().Generate835ForStatement(statement, Contract, Ach()), "TRN");

        Assert.Equal(new[] { "TRN", "1", statement.StatementNumber, OriginatingCompanyId }, trn);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("PLANID")]
    public void MissingOrMalformedOriginatingCompanyId_Throws(string? configured)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Service(configured).Generate835ForStatement(Statement(), Contract, Ach()));
        Assert.Contains("Era:OriginatingCompanyId", ex.Message);
    }

    [Fact]
    public void AchWithoutPayerAccount_Throws()
    {
        var tp = Ach();
        tp.PayerAccountNumber = null;

        var ex = Assert.Throws<InvalidOperationException>(
            () => Service().Generate835ForStatement(Statement(), Contract, tp));
        Assert.Contains("BPR09", ex.Message);
    }
}
