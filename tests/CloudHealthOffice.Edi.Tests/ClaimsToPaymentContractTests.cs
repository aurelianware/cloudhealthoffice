using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;
using Claims = ClaimsService.Models;
using Pay = PaymentService.Services;
using PayModels = PaymentService.Models;

namespace CloudHealthOffice.Edi.Tests;

/// <summary>
/// Contract between claims-service's claim search (it returns
/// <see cref="Claims.Claim"/> through plain <c>AddControllers()</c>: camelCase
/// properties, enums as numbers) and payment-service's <see cref="Pay.ClaimDto"/>.
/// This seam hid the billed-charge bug: payment-service read fields
/// (approvedAmount, claimLines[].paidAmount, patientResponsibility) that
/// claims-service never sends. These tests serialize the real claims-service
/// model and read it the way payment-service does.
/// </summary>
public class ClaimsToPaymentContractTests
{
    // ASP.NET Core MVC's default output options (AddControllers without AddJsonOptions).
    private static readonly JsonSerializerOptions ClaimsServiceWire = new(JsonSerializerDefaults.Web);

    private static Claims.Claim AdjudicatedClaim(Claims.ClaimStatus status) => new()
    {
        Id = "clm-1",
        ClaimNumber = "CLM-0001",
        MemberId = "M-1",
        BillingProviderNPI = "1234567893",
        TotalChargeAmount = 300m,
        Status = status,
        ServiceDateFrom = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
        AdjudicationResult = new Claims.AdjudicationResult
        {
            AllowedAmount = 220m,      // includes member cost share: never the amount paid
            DeductibleAmount = 40m,
            CoinsuranceAmount = 10m,
            CopayAmount = 0m,
            PatientResponsibility = 50m,
            PayerPayment = 170m,       // what the plan pays: CLP04
            AdjustmentReasons = { new Claims.ClaimAdjustmentReason { GroupCode = "CO", ReasonCode = "45", Amount = 80m } },
        },
        ClaimLines =
        {
            new Claims.ClaimLine
            {
                LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 200m, Units = 1,
                AdjudicationResult = new Claims.LineAdjudicationResult { AllowedAmount = 150m, PaidAmount = 120m, PatientResponsibility = 30m },
            },
            new Claims.ClaimLine
            {
                LineNumber = 2, ProcedureCode = "85025", ChargeAmount = 100m, Units = 1,
                AdjudicationResult = new Claims.LineAdjudicationResult { AllowedAmount = 70m, PaidAmount = 50m, PatientResponsibility = 20m },
            },
        },
    };

    /// <summary>Serialize as claims-service does, read as payment-service does (ReadFromJsonAsync).</summary>
    private static async Task<Pay.ClaimDto> RoundTrip(Claims.Claim claim)
    {
        var json = JsonSerializer.Serialize(new[] { claim }, ClaimsServiceWire);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var dtos = await content.ReadFromJsonAsync<List<Pay.ClaimDto>>();
        return Assert.Single(dtos!);
    }

    [Fact]
    public async Task ApprovedClaim_PlanPaidAmount_IsAdjudicationResultPayerPayment_NotAllowedOrBilled()
    {
        var dto = await RoundTrip(AdjudicatedClaim(Claims.ClaimStatus.Approved));

        Assert.Equal(170m, dto.PlanPaidAmount);
        Assert.NotEqual(dto.AdjudicationResult!.AllowedAmount, dto.PlanPaidAmount);
        Assert.Equal(300m, dto.TotalChargeAmount);                 // CLP03
        Assert.Equal(50m, dto.AdjudicationResult.PatientResponsibility); // CLP05
    }

    [Fact]
    public async Task ServiceLinePaidAmounts_ComeFromLineAdjudicationResult_AndSumToPayerPayment()
    {
        var dto = await RoundTrip(AdjudicatedClaim(Claims.ClaimStatus.Approved));

        Assert.Equal(new decimal?[] { 120m, 50m }, dto.ServiceLines!.Select(l => l.LinePaidAmount));
        Assert.Equal(dto.PlanPaidAmount, dto.ServiceLines!.Sum(l => l.LinePaidAmount));
    }

    [Theory]
    [InlineData(Claims.ClaimStatus.Approved, PayModels.ClaimStatus.Approved)]
    [InlineData(Claims.ClaimStatus.Pended, PayModels.ClaimStatus.Pended)]
    [InlineData(Claims.ClaimStatus.Denied, PayModels.ClaimStatus.Denied)]
    [InlineData(Claims.ClaimStatus.Paid, PayModels.ClaimStatus.Paid)]
    [InlineData(Claims.ClaimStatus.Voided, PayModels.ClaimStatus.Voided)]
    public async Task ClaimStatus_NumericWireValue_MapsToTheSameStatus(Claims.ClaimStatus sent, PayModels.ClaimStatus read)
    {
        var dto = await RoundTrip(AdjudicatedClaim(sent));

        Assert.Equal(read, dto.Status);
    }

    [Fact]
    public void EveryClaimsServiceStatus_HasTheSameNameAndValueInPaymentService()
    {
        foreach (var status in Enum.GetValues<Claims.ClaimStatus>())
        {
            Assert.True(Enum.TryParse<PayModels.ClaimStatus>(status.ToString(), out var mirrored), $"{status} missing");
            Assert.Equal((int)status, (int)mirrored);
        }
    }

    /// <summary>
    /// claims-service fills <c>claimLines[].adjudicationResult.adjustmentReasons</c>
    /// (CO-45, PR-1/2/3, OA-23). <c>remarkCode</c> arrives with the line
    /// cost-share change; it is not on this model yet, so it is added to the
    /// wire JSON here the way claims-service will send it.
    /// </summary>
    private static Claims.Claim ClaimWithLineAdjustments()
    {
        var claim = AdjudicatedClaim(Claims.ClaimStatus.Approved);
        claim.ClaimLines[0].AdjudicationResult!.AdjustmentReasons = new List<Claims.ClaimAdjustmentReason>
        {
            new() { GroupCode = "CO", ReasonCode = "45", Amount = 50m },
            new() { GroupCode = "PR", ReasonCode = "1", Amount = 20m, Description = "Deductible" },
            new() { GroupCode = "PR", ReasonCode = "2", Amount = 10m },
        };
        claim.ClaimLines[1].AdjudicationResult!.AdjustmentReasons = new List<Claims.ClaimAdjustmentReason>
        {
            new() { GroupCode = "CO", ReasonCode = "45", Amount = 30m },
            new() { GroupCode = "OA", ReasonCode = "23", Amount = 0m },
            new() { GroupCode = "PR", ReasonCode = "1", Amount = 20m },
        };
        return claim;
    }

    private static async Task<Pay.ClaimDto> RoundTripWithLineRemark(Claims.Claim claim, int line, int adjustment, string remarkCode)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new[] { claim }, ClaimsServiceWire))!;
        node[0]!["claimLines"]![line]!["adjudicationResult"]!["adjustmentReasons"]![adjustment]!["remarkCode"] = remarkCode;
        using var content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
        return Assert.Single((await content.ReadFromJsonAsync<List<Pay.ClaimDto>>())!);
    }

    [Fact]
    public async Task LineAdjustmentReasons_BecomeSvcCas_RemarkInLq_AndThe835Balances()
    {
        var dto = await RoundTripWithLineRemark(ClaimWithLineAdjustments(), line: 0, adjustment: 1, remarkCode: "N130");
        var cp = Pay.Era835ClaimPaymentBuilder.Build(dto, denied: false,
            new Pay.CarcRarcMappingService(Microsoft.Extensions.Logging.Abstractions.NullLogger<Pay.CarcRarcMappingService>.Instance));

        var payment = new PayModels.Payment
        {
            CheckNumber = "0001000001", PaymentMethod = "CHK", TotalPaymentAmount = cp.PaymentAmount,
            PaymentDate = new DateTime(2026, 4, 2), PayeeNPI = "1234567893",
            ClaimPayments = new List<PayModels.ClaimPayment> { cp },
        };
        var edi = new Pay.BatchEraGeneratorService(Microsoft.Extensions.Logging.Abstractions.NullLogger<Pay.BatchEraGeneratorService>.Instance)
            .GenerateBatch(new[] { new Pay.EraPaymentInput { TradingPartnerId = "TP", Payment = payment } },
                new Dictionary<string, Pay.TradingPartnerInfo> { ["TP"] = new() { OriginatingCompanyId = "1123456789" } })
            .Single().EdiContent;
        var segments = edi.Split('~', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split('*')).ToList();

        // Line CAS grouped by group code, reason/amount/quantity triplets; the claim-level
        // CO-45 total is not repeated in the header.
        // (service-date DTMs between SVC and CAS left out of the comparison)
        var withoutLineDates = string.Join("~", edi.Split('~').Where(s => !s.StartsWith("DTM*47", StringComparison.Ordinal)));
        Assert.Contains(
            "SVC*HC:99213*200.00*120.00**1~CAS*CO*45*50.00~CAS*PR*1*20.00**2*10.00~LQ*HE*N130~" +
            "SVC*HC:85025*100.00*50.00**1~CAS*CO*45*30.00~CAS*OA*23*0.00~CAS*PR*1*20.00~",
            withoutLineDates);
        var clp = segments.Single(s => s[0] == "CLP");
        Assert.DoesNotContain(segments.SkipWhile(s => s[0] != "CLP").Skip(1).TakeWhile(s => s[0] != "NM1"), s => s[0] == "CAS");

        // SVC02 - sum(line CAS) = SVC03 for each line; CLP03 - sum(CAS) = CLP04.
        var lineCas = 0m;
        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i][0] != "SVC") continue;
            var cas = segments.Skip(i + 1).TakeWhile(s => s[0] is not ("SVC" or "PLB" or "SE"))
                .Where(s => s[0] == "CAS")
                .Sum(s => Enumerable.Range(0, (s.Length - 1) / 3).Sum(k => decimal.Parse(s[3 + 3 * k])));
            Assert.Equal(decimal.Parse(segments[i][3]), decimal.Parse(segments[i][2]) - cas);
            lineCas += cas;
        }
        Assert.Equal(decimal.Parse(clp[4]), decimal.Parse(clp[3]) - lineCas);
        Assert.Equal("170.00", clp[4]);
    }

    [Fact]
    public async Task LineAdjustmentReasons_WithoutRemarkCode_DeserializeWithNullRemark()
    {
        var dto = await RoundTrip(ClaimWithLineAdjustments());

        var reasons = dto.ServiceLines![0].AdjudicationResult!.AdjustmentReasons!;
        Assert.Equal(new[] { ("CO", "45", 50m), ("PR", "1", 20m), ("PR", "2", 10m) },
            reasons.Select(r => (r.GroupCode, r.ReasonCode, r.Amount)));
        Assert.All(reasons, r => Assert.Null(r.RemarkCode));
    }

    [Fact]
    public async Task BillingProviderName_IsThePayeeName()
    {
        var claim = AdjudicatedClaim(Claims.ClaimStatus.Approved);
        claim.BillingProviderName = "SYNTHETIC FAMILY CLINIC";

        var dto = await RoundTrip(claim);

        Assert.Equal("SYNTHETIC FAMILY CLINIC", dto.ProviderName);
        Assert.Equal("SYNTHETIC FAMILY CLINIC", dto.PayeeNameOr(dto.BillingProviderNPI));
    }

    [Fact]
    public async Task WithoutBillingProviderName_PayeeNameFallsBackToTheNpi()
    {
        var dto = await RoundTrip(AdjudicatedClaim(Claims.ClaimStatus.Approved));

        Assert.Null(dto.ProviderName);
        Assert.Equal("1234567893", dto.PayeeNameOr(dto.BillingProviderNPI));
    }

    /// <summary>
    /// 5010 has no pay-to provider NPI or name (837 2010AB is an address
    /// only, X12 RFI 1522): the payee is the billing provider, named by
    /// billingProviderName. A long name is cut to N102's 60 characters only
    /// when the 835 is written.
    /// </summary>
    [Fact]
    public async Task PayeeIsTheBillingProvider_LongNameCutOnlyInN102()
    {
        var claim = AdjudicatedClaim(Claims.ClaimStatus.Approved);
        claim.BillingProviderName = new string('N', 300);

        var dto = await RoundTrip(claim);

        Assert.Equal("1234567893", dto.PayeeNpi);
        Assert.Null(dto.PayToAddress);
        Assert.Null(dto.PayToPlan);
        Assert.Equal(300, dto.PayeeNameOr(dto.PayeeNpi).Length);
        Assert.Equal(new string('N', 60), Pay.Era835Names.N102(dto.PayeeNameOr(dto.PayeeNpi)));
    }

    // 837 with Loop 2010AB (pay-to address) and, on a subrogation demand,
    // Loop 2010AC (pay-to plan). Synthetic data; NPIs are check-digit-valid test numbers.
    private static string Pay837(string version, string bht06, bool payToPlan)
    {
        var claimSegments = version.Contains("223")
            ? new[] { "CLM*PCN-PAYTO*300.00***13:A:1**A*Y*Y", "DTP*434*RD8*20260105-20260106", "CL1*1*7*01", "HI*ABK:R0789", "LX*1", "SV2*0450*HC:99283*300.00*UN*1", "DTP*472*D8*20260105" }
            : new[] { "CLM*PCN-PAYTO*300.00***11:B:1*Y*A*Y*Y", "HI*ABK:J069", "LX*1", "SV1*HC:99213*300.00*UN*1***1", "DTP*472*D8*20260110" };
        var body = new List<string>
        {
            $"BHT*0019*00*BATCH0001*20260115*1200*{bht06}",
            "NM1*41*2*ACME BILLING*****46*SUB001", "PER*IC*BILLING DESK*TE*5555550100", "NM1*40*2*CHO PAYER*****46*CHO",
            "HL*1**20*1", "NM1*85*2*SYNTHETIC FAMILY CLINIC*****XX*1234567893", "N3*100 MAIN ST", "N4*SPRINGFIELD*IL*62701", "REF*EI*123456789",
            "NM1*87*2", "N3*PO BOX 1234", "N4*SPRINGFIELD*IL*627010001",
        };
        if (payToPlan)
            body.AddRange(new[] { "NM1*PE*2*SYNTHETIC MEDICAID PLAN*****PI*PLAN01", "N3*1 PLAN WAY", "N4*CAPITAL CITY*IL*62701", "REF*EI*000000001" });
        body.AddRange(new[] { "HL*2*1*22*0", "SBR*P*18*GRP001******CI", "NM1*IL*1*TESTPATIENT*ALEX****MI*MEM0001", "DMG*D8*19800101*F", "NM1*PR*2*CHO PAYER*****PI*CHO" });
        body.AddRange(claimSegments);
        var segments = new List<string>
        {
            "ISA*00*          *00*          *ZZ*SUB001         *ZZ*CHO            *260115*1200*^*00501*000000101*0*T*:",
            $"GS*HC*SUB001*CHO*20260115*1200*101*X*{version}", $"ST*837*0001*{version}",
        };
        segments.AddRange(body);
        segments.Add($"SE*{body.Count + 2}*0001");
        segments.Add("GE*1*101");
        segments.Add("IEA*1*000000101");
        return string.Join("~", segments) + "~";
    }

    /// <summary>837 text → SNIP → parser → mapper → stored claim → claim search wire → ClaimDto.</summary>
    private static async Task<Pay.ClaimDto> FromEdi(string edi)
    {
        var validation = new ClaimsService.EDI.Validation.X12837SnipValidator().Validate(edi);
        Assert.Empty(validation.AllIssues);
        var parsed = Assert.Single(ClaimsService.EDI.Inbound.X12837Parser.Parse(edi));
        var claim = ClaimsService.EDI.Inbound.X12837ClaimMapper.Map(parsed, "tenant-1").ToClaim();
        claim.Id = "clm-payto";
        claim.Status = Claims.ClaimStatus.Approved;
        claim.AdjudicationResult = new Claims.AdjudicationResult { AllowedAmount = 300m, PayerPayment = 300m };
        return await RoundTrip(claim);
    }

    [Theory]
    [InlineData("005010X222A1")]
    [InlineData("005010X223A2")]
    public async Task PayToAddressFrom2010AB_ReachesThe835_AsPayeeN3N4_UnderTheBillingProvider(string version)
    {
        var dto = await FromEdi(Pay837(version, "CH", payToPlan: false));

        Assert.Null(dto.PayToPlan);
        var payment = new PayModels.Payment
        {
            CheckNumber = "0001000001", PaymentMethod = "CHK", TotalPaymentAmount = 300m, PaymentDate = new DateTime(2026, 4, 2),
            PayerName = "CHO", PayerId = "CHO",
            // As PaymentRunService builds a payment from its claims.
            PayeeName = dto.PayeeNameOr(dto.PayeeNpi), PayeeNPI = dto.PayeeNpi,
            PayeeAddress = Pay.PaymentRunService.SharedPayToAddress(new[] { dto }),
            ClaimPayments = new List<PayModels.ClaimPayment>
            {
                Pay.Era835ClaimPaymentBuilder.Build(dto, denied: false,
                    new Pay.CarcRarcMappingService(Microsoft.Extensions.Logging.Abstractions.NullLogger<Pay.CarcRarcMappingService>.Instance)),
            },
        };
        var edi = new Pay.EraGeneratorService(Microsoft.Extensions.Logging.Abstractions.NullLogger<Pay.EraGeneratorService>.Instance)
            .Generate835(payment, new Pay.TradingPartnerInfo { OriginatingCompanyId = "1123456789" });
        var segments = edi.Split('~').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        var n1 = segments.FindIndex(s => s.StartsWith("N1*PE*", StringComparison.Ordinal));
        Assert.Equal("N1*PE*SYNTHETIC FAMILY CLINIC*XX*1234567893", segments[n1]);
        Assert.Equal("N3*PO BOX 1234", segments[n1 + 1]);
        Assert.Equal("N4*SPRINGFIELD*IL*627010001", segments[n1 + 2]);
    }

    [Theory]
    [InlineData("005010X222A1")]
    [InlineData("005010X223A2")]
    public async Task PayToPlanFrom2010AC_ReachesPaymentService_NotAsThePayeeNpi(string version)
    {
        var dto = await FromEdi(Pay837(version, "31", payToPlan: true));

        Assert.Equal(("SYNTHETIC MEDICAID PLAN", "PI", "PLAN01", "000000001"),
            (dto.PayToPlan!.Name, dto.PayToPlan.IdentifierQualifier, dto.PayToPlan.Identifier, dto.PayToPlan.TaxId));
        Assert.Equal(("1 PLAN WAY", "CAPITAL CITY"), (dto.PayToPlan.Address!.Line1, dto.PayToPlan.Address.City));
        Assert.Equal("PO BOX 1234", dto.PayToAddress!.Line1);
        Assert.Equal("1234567893", dto.PayeeNpi); // the plan has no NPI; PaymentRunService leaves such claims out
    }

    /// <summary>
    /// claims-service's 837I header detail (<c>Claim.Institutional</c>) and
    /// <c>Claim.ClaimFrequencyCode</c> reach the 835 CLP as CLP08 facility type
    /// code, CLP09 frequency code and CLP11 DRG.
    /// </summary>
    [Fact]
    public async Task InstitutionalClaim_FacilityFrequencyAndDrg_ReachClp08Clp09Clp11()
    {
        var claim = AdjudicatedClaim(Claims.ClaimStatus.Approved);
        claim.ClaimType = Claims.ClaimType.Institutional;
        claim.ClaimFrequencyCode = "7";
        claim.Institutional = new Claims.InstitutionalClaimDetails { FacilityTypeCode = "11", DrgCode = "470", PatientStatusCode = "01" };

        var dto = await RoundTrip(claim);
        Assert.Equal(Pay.ClaimFormType.Institutional, dto.ClaimType);
        Assert.Equal("7", dto.ClaimFrequencyCode);
        Assert.Equal(("11", "470"), (dto.Institutional!.FacilityTypeCode, dto.Institutional.DrgCode));

        var cp = Pay.Era835ClaimPaymentBuilder.Build(dto, denied: false,
            new Pay.CarcRarcMappingService(Microsoft.Extensions.Logging.Abstractions.NullLogger<Pay.CarcRarcMappingService>.Instance));
        var segments = 0;
        var clp = Pay.Era835ClaimLoops.BuildClaimLoop(cp, ref segments).Split('~')[0];
        Assert.Equal("CLP*CLM-0001*1*300.00*170.00*50.00*HM*clm-1*11*7**470", clp);
    }

    [Fact]
    public async Task ProfessionalClaim_HasNoInstitutionalDetail_ClpEndsAtClp07()
    {
        var dto = await RoundTrip(AdjudicatedClaim(Claims.ClaimStatus.Approved));
        Assert.Null(dto.Institutional);
        Assert.Equal("1", dto.ClaimFrequencyCode); // claims-service default; not reported for 837P

        var cp = Pay.Era835ClaimPaymentBuilder.Build(dto, denied: false,
            new Pay.CarcRarcMappingService(Microsoft.Extensions.Logging.Abstractions.NullLogger<Pay.CarcRarcMappingService>.Instance));
        var segments = 0;
        Assert.Equal("CLP*CLM-0001*1*300.00*170.00*50.00*HM*clm-1",
            Pay.Era835ClaimLoops.BuildClaimLoop(cp, ref segments).Split('~')[0]);
    }

    [Fact]
    public async Task ClaimWithoutAdjudicationResult_HasNoPlanPaidAmount()
    {
        var claim = AdjudicatedClaim(Claims.ClaimStatus.Approved);
        claim.AdjudicationResult = null;
        foreach (var line in claim.ClaimLines)
            line.AdjudicationResult = null;

        var dto = await RoundTrip(claim);

        Assert.Null(dto.PlanPaidAmount);
        Assert.All(dto.ServiceLines!, l => Assert.Null(l.LinePaidAmount));
    }
}
