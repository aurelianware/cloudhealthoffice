using Microsoft.Extensions.Logging;
using CapitationService.Models;
using CapitationService.Services;

namespace CapitationService.Tests.Services;

public class CapitationEraServiceTests
{
    private readonly CapitationEraService _service;
    private readonly CapitationEraTradingPartnerInfo _defaultTp;

    public CapitationEraServiceTests()
    {
        var logger = new Mock<ILogger<CapitationEraService>>();
        _service = new CapitationEraService(logger.Object);

        _defaultTp = new CapitationEraTradingPartnerInfo
        {
            InterchangeSenderId = "HEALTHPLAN",
            InterchangeReceiverId = "PROVIDER01",
            ApplicationSenderId = "CHOSENDER",
            ApplicationReceiverId = "CHORECEIVER",
            PayerName = "Cloud Health Office",
            PayerId = "CHO12345",
            PayerRoutingNumber = "091000019",
            PayerAccountNumber = "1234567890",
            PayeeRoutingNumber = "021000089",
            PayeeAccountNumber = "9876543210"
        };
    }

    private static CapitationContract CreateContract() => new()
    {
        Id = "contract-1",
        ContractNumber = "CAP-1234567890-2026",
        ProviderNPI = "1234567890",
        ProviderName = "Dr. Sarah Chen, MD",
        ContractType = ContractType.PrimaryCareOnly,
        WithholdPercentage = 0.10m
    };

    private static CapitationStatement CreateStatement(int memberCount = 3)
    {
        var stmt = new CapitationStatement
        {
            Id = "stmt-1",
            StatementNumber = "CAPSTMT-1234567890-2026-03",
            ContractId = "contract-1",
            ContractNumber = "CAP-1234567890-2026",
            ProviderNPI = "1234567890",
            ProviderName = "Dr. Sarah Chen, MD",
            CapitationPeriodStart = new DateTime(2026, 3, 1),
            CapitationPeriodEnd = new DateTime(2026, 3, 31),
            PaymentDate = new DateTime(2026, 4, 1),
            CheckNumber = null
        };

        var members = new[]
        {
            ("MEM001", "John Doe",   28, "M", 28.00m, 1.0m, 1.0m),
            ("MEM002", "Jane Smith",  45, "F", 38.00m, 1.2m, 1.0m),
            ("MEM003", "Bob Wilson",  72, "M", 45.00m, 1.5m, 0.5m),
        };

        for (int i = 0; i < Math.Min(memberCount, members.Length); i++)
        {
            var (id, name, age, gender, basePmpm, riskScore, proration) = members[i];
            var adjustedPmpm = Math.Round(basePmpm * riskScore, 2);
            var gross = Math.Round(adjustedPmpm * proration, 2);
            var withhold = Math.Round(gross * 0.10m, 2);

            stmt.LineItems.Add(new CapitationLineItem
            {
                MemberId = id,
                MemberName = name,
                MemberAge = age,
                Gender = gender,
                BasePMPM = basePmpm,
                RiskScore = riskScore,
                AdjustedPMPM = adjustedPmpm,
                ProrationFactor = proration,
                GrossAmount = gross,
                WithholdAmount = withhold,
                NetAmount = gross - withhold,
                AssignmentEffectiveDate = new DateTime(2025, 1, 1)
            });
        }

        stmt.Adjustments.Add(new CapitationAdjustment
        {
            Type = CapitationAdjustmentType.RetroEnrollment,
            Description = "Retro add for MEM004",
            Amount = 28.00m,
            RelatedMemberId = "MEM004",
            AdjustmentDate = new DateTime(2026, 3, 15)
        });

        stmt.RecalculateTotals();
        return stmt;
    }

    #region ISA/GS/ST Envelope

    [Fact]
    public void Generate835_StartsWithISAHeader()
    {
        var edi = _service.Generate835ForStatement(CreateStatement(), CreateContract(), _defaultTp);

        edi.Should().StartWith("ISA*00*");
    }

    [Fact]
    public void Generate835_ContainsGSFunctionalGroupHeader()
    {
        var edi = _service.Generate835ForStatement(CreateStatement(), CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        segments.Should().Contain(s => s.StartsWith("GS*HP*"));
    }

    [Fact]
    public void Generate835_ContainsST835TransactionHeader()
    {
        var edi = _service.Generate835ForStatement(CreateStatement(), CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        segments.Should().Contain(s => s.StartsWith("ST*835*"));
        segments.Should().Contain(s => s.Contains("005010X221A1"));
    }

    [Fact]
    public void Generate835_EndsWithIEATrailer()
    {
        var edi = _service.Generate835ForStatement(CreateStatement(), CreateContract(), _defaultTp);
        var segments = edi.Split('~').Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

        segments.Last().Should().StartWith("IEA*1*");
    }

    [Fact]
    public void Generate835_ContainsGETrailer()
    {
        var edi = _service.Generate835ForStatement(CreateStatement(), CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        segments.Should().Contain(s => s.StartsWith("GE*1*"));
    }

    [Fact]
    public void Generate835_SESegmentCountIncludesSTAndSE()
    {
        var edi = _service.Generate835ForStatement(CreateStatement(1), CreateContract(), _defaultTp);
        var segments = edi.Split('~').Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

        var seSegment = segments.First(s => s.StartsWith("SE*"));
        var seCount = int.Parse(seSegment.Split('*')[1]);

        // Count segments between ST and SE (inclusive)
        var stIdx = Array.FindIndex(segments, s => s.StartsWith("ST*"));
        var seIdx = Array.FindIndex(segments, s => s.StartsWith("SE*"));
        var actualCount = seIdx - stIdx + 1;

        seCount.Should().Be(actualCount);
    }

    #endregion

    #region BPR — Financial Information

    [Fact]
    public void Generate835_BPR_ContainsNetPayableAmount()
    {
        var stmt = CreateStatement();
        var edi = _service.Generate835ForStatement(stmt, CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        var bpr = segments.First(s => s.StartsWith("BPR*"));
        bpr.Should().Contain(stmt.NetPayable.ToString("F2"));
    }

    [Fact]
    public void Generate835_BPR_ACH_WhenBankDetailsProvided()
    {
        var edi = _service.Generate835ForStatement(CreateStatement(), CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        var bpr = segments.First(s => s.StartsWith("BPR*"));
        bpr.Should().Contain("*ACH*");
        bpr.Should().Contain("091000019"); // Payer routing
        bpr.Should().Contain("021000089"); // Payee routing
    }

    [Fact]
    public void Generate835_BPR_CHK_WhenCheckNumber()
    {
        var stmt = CreateStatement();
        stmt.CheckNumber = "CHK-12345";

        var tp = new CapitationEraTradingPartnerInfo
        {
            PayerName = "CHO", PayerId = "CHO1",
            PayerRoutingNumber = null, PayeeRoutingNumber = null
        };

        var edi = _service.Generate835ForStatement(stmt, CreateContract(), tp);
        var segments = edi.Split('~');

        var bpr = segments.First(s => s.StartsWith("BPR*"));
        bpr.Should().Contain("*CHK*");
    }

    [Fact]
    public void Generate835_BPR_NON_WhenNoBankOrCheck()
    {
        var stmt = CreateStatement();
        stmt.CheckNumber = null;
        stmt.PaymentDate = null;

        var tp = new CapitationEraTradingPartnerInfo
        {
            PayerName = "CHO", PayerId = "CHO1"
        };

        var edi = _service.Generate835ForStatement(stmt, CreateContract(), tp);
        var segments = edi.Split('~');

        var bpr = segments.First(s => s.StartsWith("BPR*"));
        bpr.Should().Contain("*NON*");
    }

    [Fact]
    public void Generate835_BPR_ZeroPayment_UsesICode()
    {
        var stmt = CreateStatement(0);  // No line items
        stmt.Adjustments.Clear();       // No adjustments either → net = 0
        stmt.RecalculateTotals();
        stmt.NetPayable.Should().Be(0);

        var edi = _service.Generate835ForStatement(stmt, CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        var bpr = segments.First(s => s.StartsWith("BPR*"));
        bpr.Should().StartWith("BPR*I*"); // Remittance info only
    }

    #endregion

    #region TRN / DTM / N1 Loops

    [Fact]
    public void Generate835_TRN_ContainsStatementNumber()
    {
        var stmt = CreateStatement();
        var edi = _service.Generate835ForStatement(stmt, CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        var trn = segments.First(s => s.StartsWith("TRN*"));
        trn.Should().Contain(stmt.StatementNumber);
    }

    [Fact]
    public void Generate835_DTM405_ProductionDate()
    {
        var edi = _service.Generate835ForStatement(CreateStatement(), CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        segments.Should().Contain(s => s.StartsWith("DTM*405*"));
    }

    [Fact]
    public void Generate835_N1PR_PayerIdentification()
    {
        var edi = _service.Generate835ForStatement(CreateStatement(), CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        var n1pr = segments.First(s => s.StartsWith("N1*PR*"));
        n1pr.Should().Contain("Cloud Health Office");
        n1pr.Should().Contain("CHO12345");
    }

    [Fact]
    public void Generate835_N1PE_PayeeWithNPI()
    {
        var edi = _service.Generate835ForStatement(CreateStatement(), CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        var n1pe = segments.First(s => s.StartsWith("N1*PE*"));
        n1pe.Should().Contain("Dr. Sarah Chen");
        n1pe.Should().Contain("*XX*1234567890");
    }

    #endregion

    #region CLP — Member Capitation Loops

    [Fact]
    public void Generate835_OneCLPPerMember()
    {
        var stmt = CreateStatement(3);
        var edi = _service.Generate835ForStatement(stmt, CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        var clpSegments = segments.Where(s => s.StartsWith("CLP*")).ToArray();
        clpSegments.Should().HaveCount(3);
    }

    [Fact]
    public void Generate835_CLP02_Is22_CapitationPayment()
    {
        var edi = _service.Generate835ForStatement(CreateStatement(1), CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        var clp = segments.First(s => s.StartsWith("CLP*"));
        var elements = clp.Split('*');
        elements[2].Should().Be("22"); // Capitation payment status code
    }

    [Fact]
    public void Generate835_CLP06_IsCP_CapitationFilingIndicator()
    {
        var edi = _service.Generate835ForStatement(CreateStatement(1), CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        var clp = segments.First(s => s.StartsWith("CLP*"));
        var elements = clp.Split('*');
        elements[6].Should().Be("CP");
    }

    [Fact]
    public void Generate835_CLP01_IsMemberId()
    {
        var stmt = CreateStatement(1);
        var edi = _service.Generate835ForStatement(stmt, CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        var clp = segments.First(s => s.StartsWith("CLP*"));
        var elements = clp.Split('*');
        elements[1].Should().Be("MEM001");
    }

    [Fact]
    public void Generate835_CLP_ContainsGrossAndNetAmounts()
    {
        var stmt = CreateStatement(1);
        var item = stmt.LineItems[0];
        var edi = _service.Generate835ForStatement(stmt, CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        var clp = segments.First(s => s.StartsWith("CLP*"));
        var elements = clp.Split('*');
        elements[3].Should().Be(item.GrossAmount.ToString("F2"));
        elements[4].Should().Be(item.NetAmount.ToString("F2"));
    }

    [Fact]
    public void Generate835_CLP07_ContainsContractNumber()
    {
        var contract = CreateContract();
        var edi = _service.Generate835ForStatement(CreateStatement(1), contract, _defaultTp);
        var segments = edi.Split('~');

        var clp = segments.First(s => s.StartsWith("CLP*"));
        var elements = clp.Split('*');
        elements[7].Should().Be(contract.ContractNumber);
    }

    [Fact]
    public void Generate835_NM1QC_PatientName()
    {
        var edi = _service.Generate835ForStatement(CreateStatement(1), CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        // NM1*QC for patient within CLP loop
        segments.Should().Contain(s => s.StartsWith("NM1*QC*") && s.Contains("MEM001"));
    }

    [Fact]
    public void Generate835_NoSVCServiceLines()
    {
        var edi = _service.Generate835ForStatement(CreateStatement(), CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        // Capitation 835 should NOT have SVC segments
        segments.Should().NotContain(s => s.StartsWith("SVC*"));
    }

    #endregion

    #region CAS — Withhold Adjustments

    [Fact]
    public void Generate835_CAS_CO45_ForWithholdAmount()
    {
        var stmt = CreateStatement(1);
        // Ensure withhold > 0
        stmt.LineItems[0].WithholdAmount.Should().BeGreaterThan(0);

        var edi = _service.Generate835ForStatement(stmt, CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        // CAS*CO*45*{amount} for contractual withhold
        var cas = segments.FirstOrDefault(s => s.StartsWith("CAS*CO*45*"));
        cas.Should().NotBeNull();
        cas.Should().Contain(stmt.LineItems[0].WithholdAmount.ToString("F2"));
    }

    #endregion

    #region PLB — Provider Level Adjustments

    [Fact]
    public void Generate835_PLB_WithholdAdjustment()
    {
        var stmt = CreateStatement();
        stmt.WithholdAmount.Should().BeGreaterThan(0);

        var edi = _service.Generate835ForStatement(stmt, CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        var plb = segments.FirstOrDefault(s => s.StartsWith("PLB*"));
        plb.Should().NotBeNull();
        plb.Should().Contain("WO:WITHHOLD");
    }

    [Fact]
    public void Generate835_PLB_RetroAdjustment()
    {
        var stmt = CreateStatement();
        // Statement has a RetroEnrollment adjustment added in CreateStatement
        stmt.Adjustments.Should().Contain(a => a.Type == CapitationAdjustmentType.RetroEnrollment);

        var edi = _service.Generate835ForStatement(stmt, CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        // PLB should contain code 72 (capitation payment) for retro adjustment
        var plbSegments = segments.Where(s => s.StartsWith("PLB*")).ToArray();
        plbSegments.Should().Contain(s => s.Contains("72:"));
    }

    [Fact]
    public void Generate835_PLB_ProviderNPIAsIdentifier()
    {
        var stmt = CreateStatement();
        var edi = _service.Generate835ForStatement(stmt, CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        var plb = segments.First(s => s.StartsWith("PLB*"));
        plb.Should().StartWith("PLB*1234567890*");
    }

    #endregion

    #region AMT / QTY Supplemental Data

    [Fact]
    public void Generate835_AMT_B6_BasePMPM()
    {
        var stmt = CreateStatement(1);
        var edi = _service.Generate835ForStatement(stmt, CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        var amt = segments.FirstOrDefault(s => s.StartsWith("AMT*B6*"));
        amt.Should().NotBeNull();
        amt.Should().Contain(stmt.LineItems[0].BasePMPM.ToString("F2"));
    }

    [Fact]
    public void Generate835_QTY_CA_RiskScore_WhenNotDefault()
    {
        var stmt = CreateStatement(2); // MEM002 has risk score 1.2
        var edi = _service.Generate835ForStatement(stmt, CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        segments.Should().Contain(s => s.StartsWith("QTY*CA*1.2"));
    }

    [Fact]
    public void Generate835_QTY_CA_Omitted_WhenRiskScoreIs1()
    {
        var stmt = CreateStatement(1); // MEM001 has risk score 1.0
        var edi = _service.Generate835ForStatement(stmt, CreateContract(), _defaultTp);
        var segments = edi.Split('~');

        // MEM001 risk score = 1.0, so QTY*CA should not appear
        var clpIdx = Array.FindIndex(segments, s => s.StartsWith("CLP*MEM001"));
        var nextClpOrPlb = Array.FindIndex(segments, clpIdx + 1, s => s.StartsWith("CLP*") || s.StartsWith("PLB*") || s.StartsWith("SE*"));
        var memberSegments = segments.Skip(clpIdx).Take(nextClpOrPlb - clpIdx);
        memberSegments.Should().NotContain(s => s.StartsWith("QTY*CA*"));
    }

    #endregion

    #region X12 Delimiter Escaping

    [Fact]
    public void Generate835_EscapesDelimitersInNames()
    {
        var stmt = CreateStatement(1);
        stmt.ProviderName = "O*Brien~Medical:Group";

        var edi = _service.Generate835ForStatement(stmt, CreateContract(), _defaultTp);

        // Delimiters should be replaced with spaces
        edi.Should().NotContain("O*Brien");
        edi.Should().Contain("O Brien Medical Group");
    }

    #endregion

    #region Full EDI Structure

    [Fact]
    public void Generate835_FullDocument_HasCorrectSegmentOrder()
    {
        var edi = _service.Generate835ForStatement(CreateStatement(2), CreateContract(), _defaultTp);
        var segments = edi.Split('~').Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Substring(0, Math.Min(3, s.Length))).ToArray();

        // Verify segment order: ISA, GS, ST, BPR, TRN, DTM, N1(PR), N1(PE), CLP..., PLB, SE, GE, IEA
        var idx = 0;
        segments[idx++].Should().Be("ISA");
        segments[idx++].Should().Be("GS*");
        segments[idx++].Should().Be("ST*");
        segments[idx++].Should().Be("BPR");
        segments[idx++].Should().Be("TRN");
        segments[idx++].Should().Be("DTM");
        segments[idx++].Should().Be("N1*"); // PR
        segments[idx++].Should().Be("N1*"); // PE

        // Skip member loops (CLP, NM1, DTM, CAS, AMT, QTY...)
        while (idx < segments.Length && !segments[idx].StartsWith("PLB") && !segments[idx].StartsWith("SE*"))
            idx++;

        // PLB should come before SE
        if (segments[idx].StartsWith("PLB")) idx++; // may have multiple PLBs
        while (idx < segments.Length && segments[idx].StartsWith("PLB")) idx++;

        segments[idx++].Should().Be("SE*");
        segments[idx++].Should().Be("GE*");
        segments[idx].Should().Be("IEA");
    }

    #endregion
}
