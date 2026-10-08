using ClaimsService.EDI.Inbound;
using ClaimsService.Models;
using EngineClaimType = CloudHealthOffice.ClaimsScrubEngine.Models.ClaimType;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.EDI;

/// <summary>
/// 837I (005010X223A2) institutional header and line detail: type of bill,
/// admission/discharge, statement period, CL1, DRG, POA, ICD-10-PCS,
/// occurrence/span/value/condition codes, revenue-code-only lines and NDC.
/// All data below is synthetic.
/// </summary>
public class X12837InstitutionalTests
{
    // Inpatient: total knee replacement, DRG 470, 4-day stay. Lines 1–2 are
    // revenue-code-only (no SV202 HCPCS); line 2 has no DTP*472 and carries
    // an NDC; line 3 has both revenue code and HCPCS.
    private const string InpatientDrgSample =
        "ISA*00*          *00*          *ZZ*SUBMITTER01    *ZZ*CHOPAYER       *260301*1200*^*00501*000000101*0*T*:~" +
        "GS*HC*SUBMITTER01*CHOPAYER*20260301*1200*101*X*005010X223A2~" +
        "ST*837*0101*005010X223A2~" +
        "BHT*0019*00*INPT-BATCH-0001*20260301*1200*CH~" +
        "NM1*41*2*SYNTHETIC GENERAL HOSPITAL*****46*SUBMITTER01~" +
        "PER*IC*BILLING OFFICE*TE*5555550100~" +
        "NM1*40*2*CHO TEST PAYER*****46*CHOPAYER~" +
        "HL*1**20*1~" +
        "NM1*85*2*SYNTHETIC GENERAL HOSPITAL*****XX*1999999984~" +
        "N3*100 MAIN ST~" +
        "N4*ANYTOWN*TX*75001~" +
        "REF*EI*990000001~" +
        "HL*2*1*22*0~" +
        "SBR*P*18*GRP-0001******CI~" +
        "NM1*IL*1*TESTPATIENT*ALEX****MI*MBR-INPT-0001~" +
        "DMG*D8*19600115*F~" +
        "NM1*PR*2*CHO TEST PAYER*****PI*PAYER01~" +
        "CLM*INPT-0001*42500.00***11:A:1**A*Y*Y~" +
        "DTP*096*TM*1030~" +
        "DTP*434*RD8*20260210-20260214~" +
        "DTP*435*DT*202602100815~" +
        "CL1*3*1*01~" +
        "REF*G1*AUTH-0001~" +
        "HI*ABK:M1711:::::::Y~" +
        "HI*ABJ:M1711~" +
        "HI*ABF:I10:::::::Y*ABF:E119:::::::N~" +
        "HI*DR:470~" +
        "HI*BBR:0SRD0JZ:D8:20260210~" +
        "HI*BBQ:0SRC0JZ:D8:20260211~" +
        "HI*BH:11:D8:20260101~" +
        "HI*BI:70:RD8:20260201-20260205~" +
        "HI*BE:80:::4~" +
        "HI*BG:39~" +
        "NM1*71*1*SURGEON*SAM****XX*1999999992~" +
        "LX*1~" +
        "SV2*0120**12000.00*DA*4~" +
        "DTP*472*RD8*20260210-20260214~" +
        "LX*2~" +
        "SV2*0250**1500.00*UN*10~" +
        "LIN**N4*00409123401~" +
        "CTP****2*ML~" +
        "LX*3~" +
        "SV2*0360*HC:27447*29000.00*UN*1~" +
        "DTP*472*D8*20260210~" +
        "SE*44*0101~" +
        "GE*1*101~" +
        "IEA*1*000000101~";

    // Outpatient emergency visit, type of bill 131, revenue code + HCPCS on
    // every line, one drug line with an NDC.
    private const string OutpatientSample =
        "ISA*00*          *00*          *ZZ*SUBMITTER01    *ZZ*CHOPAYER       *260306*0900*^*00501*000000102*0*T*:~" +
        "GS*HC*SUBMITTER01*CHOPAYER*20260306*0900*102*X*005010X223A2~" +
        "ST*837*0102*005010X223A2~" +
        "BHT*0019*00*OUTPT-BATCH-0001*20260306*0900*CH~" +
        "NM1*41*2*SYNTHETIC GENERAL HOSPITAL*****46*SUBMITTER01~" +
        "NM1*40*2*CHO TEST PAYER*****46*CHOPAYER~" +
        "HL*1**20*1~" +
        "NM1*85*2*SYNTHETIC GENERAL HOSPITAL*****XX*1999999984~" +
        "N3*100 MAIN ST~" +
        "N4*ANYTOWN*TX*75001~" +
        "REF*EI*990000001~" +
        "HL*2*1*22*0~" +
        "SBR*P*18*GRP-0001******CI~" +
        "NM1*IL*1*TESTPATIENT*JORDAN****MI*MBR-OUTPT-0001~" +
        "DMG*D8*19850704*M~" +
        "NM1*PR*2*CHO TEST PAYER*****PI*PAYER01~" +
        "CLM*OUTPT-0001*1350.00***13:A:1**A*Y*Y~" +
        "DTP*434*RD8*20260305-20260305~" +
        "CL1*1*7*01~" +
        "HI*ABK:R0789~" +
        "HI*APR:R079~" +
        "LX*1~" +
        "SV2*0450*HC:99284:25*900.00*UN*1~" +
        "DTP*472*D8*20260305~" +
        "LX*2~" +
        "SV2*0300*HC:80053*250.00*UN*1~" +
        "DTP*472*D8*20260305~" +
        "LX*3~" +
        "SV2*0636*HC:J1885*200.00*UN*2~" +
        "DTP*472*D8*20260305~" +
        "LIN**N4*00409379601~" +
        "CTP****2*ML~" +
        "SE*30*0102~" +
        "GE*1*102~" +
        "IEA*1*000000102~";

    // Professional line with an NDC (2410 LIN/CTP).
    private const string ProfessionalNdcSample =
        "ISA*00*          *00*          *ZZ*SUBMITTER02    *ZZ*CHOPAYER       *260310*1000*^*00501*000000103*0*T*:~" +
        "GS*HC*SUBMITTER02*CHOPAYER*20260310*1000*103*X*005010X222A1~" +
        "ST*837*0103*005010X222A1~" +
        "BHT*0019*00*PROF-BATCH-0001*20260310*1000*CH~" +
        "HL*1**20*1~" +
        "NM1*85*2*SYNTHETIC CLINIC*****XX*1999999976~" +
        "HL*2*1*22*0~" +
        "SBR*P*18*GRP-0001******CI~" +
        "NM1*IL*1*TESTPATIENT*RILEY****MI*MBR-PROF-0001~" +
        "CLM*PROF-0001*85.00***11:B:1*Y*A*Y*Y~" +
        "HI*ABK:J029~" +
        "LX*1~" +
        "SV1*HC:J0696*85.00*UN*1*11**1~" +
        "DTP*472*D8*20260309~" +
        "LIN**N4*00781320495~" +
        "CTP****500*ME~" +
        "SE*16*0103~" +
        "GE*1*103~" +
        "IEA*1*000000103~";

    // ── Parser ─────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Inpatient_ReadsInstitutionalHeader()
    {
        var claim = Assert.Single(X12837Parser.Parse(InpatientDrgSample));
        var header = claim.ClaimHeader;

        Assert.Equal(EngineClaimType.Institutional, claim.ClaimType);
        Assert.Equal("11", header.FacilityTypeCode);
        Assert.Equal("1", header.FrequencyCode);
        Assert.Equal("20260210", header.AdmissionDate);
        Assert.Equal("0815", header.AdmissionHour);
        Assert.Equal("1030", header.DischargeHour);
        Assert.Null(header.DischargeDate); // TM, not a date
        Assert.Equal("20260210", header.StatementFromDate);
        Assert.Equal("20260214", header.StatementToDate);
        Assert.Equal("3", header.AdmissionTypeCode);
        Assert.Equal("1", header.AdmissionSourceCode);
        Assert.Equal("01", header.PatientStatusCode);
        Assert.Equal("470", header.DrgCode);
    }

    [Fact]
    public void Parse_Inpatient_RoutesHiQualifiers_AndKeepsOnlyDiagnosesAsDiagnoses()
    {
        var header = X12837Parser.Parse(InpatientDrgSample)[0].ClaimHeader;

        var dx = header.DiagnosisCodes!;
        Assert.Equal(new[] { "ABK", "ABJ", "ABF", "ABF" }, dx.Select(d => d.Qualifier));
        Assert.Equal(new[] { "M1711", "M1711", "I10", "E119" }, dx.Select(d => d.Code));
        Assert.Equal(new[] { "Y", null, "Y", "N" }, dx.Select(d => d.PresentOnAdmission));
        Assert.Equal("M1711", header.PrincipalDiagnosisCode);
        Assert.Equal("M1711", header.AdmittingDiagnosisCode);

        Assert.Equal("0SRD0JZ", header.PrincipalProcedure!.Code);
        Assert.Equal("BBR", header.PrincipalProcedure.Qualifier);
        Assert.Equal("20260210", header.PrincipalProcedure.Date);
        var other = Assert.Single(header.OtherProcedures!);
        Assert.Equal(("0SRC0JZ", "20260211"), (other.Code, other.Date));

        var occurrence = Assert.Single(header.OccurrenceCodes!);
        Assert.Equal(("11", "20260101"), (occurrence.Code, occurrence.Date));
        var span = Assert.Single(header.OccurrenceSpanCodes!);
        Assert.Equal(("70", "20260201", "20260205"), (span.Code, span.Date, span.DateEnd));
        var value = Assert.Single(header.ValueCodes!);
        Assert.Equal(("80", 4m), (value.Code, value.Amount));
        Assert.Equal("39", Assert.Single(header.ConditionCodes!).Code);
    }

    [Fact]
    public void Parse_Inpatient_ServiceLines_RevenueCodeOnlyAndNdc()
    {
        var lines = X12837Parser.Parse(InpatientDrgSample)[0].ServiceLines;

        Assert.Equal(3, lines.Count);
        Assert.Equal(("0120", string.Empty, 12000m, 4m, "DA"),
            (lines[0].RevenueCode, lines[0].ProcedureCode, lines[0].ChargeAmount, lines[0].Units, lines[0].UnitType));
        Assert.Equal("0250", lines[1].RevenueCode);
        Assert.Equal(string.Empty, lines[1].ProcedureCode);
        Assert.Equal("00409123401", lines[1].NationalDrugCode);
        Assert.Equal(2m, lines[1].DrugQuantity);
        Assert.Equal("ML", lines[1].DrugUnitOfMeasure);
        Assert.Equal(("0360", "27447", "HC"), (lines[2].RevenueCode, lines[2].ProcedureCode, lines[2].ProcedureCodeQualifier));
        Assert.Null(lines[2].NationalDrugCode);
    }

    [Fact]
    public void Parse_Professional_DoesNotSetFacilityTypeButReadsNdc()
    {
        var claim = Assert.Single(X12837Parser.Parse(ProfessionalNdcSample));

        Assert.Null(claim.ClaimHeader.FacilityTypeCode);
        Assert.Equal("11", claim.ClaimHeader.PlaceOfServiceCode);
        var line = Assert.Single(claim.ServiceLines);
        Assert.Equal("J0696", line.ProcedureCode);
        Assert.Equal("00781320495", line.NationalDrugCode);
        Assert.Equal(500m, line.DrugQuantity);
        Assert.Equal("ME", line.DrugUnitOfMeasure);
    }

    [Fact]
    public void Parse_TwoClaims_DoNotLeakInstitutionalStateAcrossClaims()
    {
        // The inpatient claim's DRG/CL1/dates must be reset on the next CLM.
        var combined = InpatientDrgSample.Replace("SE*44*0101~GE*1*101~IEA*1*000000101~", string.Empty)
            + "HL*3*1*22*0~SBR*P*18*GRP-0001******CI~NM1*IL*1*TESTPATIENT*SAM****MI*MBR-SECOND~"
            + "CLM*INPT-0002*500.00***13:A:1**A*Y*Y~HI*ABK:R509~LX*1~SV2*0300*HC:85025*500.00*UN*1~DTP*472*D8*20260220~"
            + "SE*10*0101~GE*1*101~IEA*1*000000101~";

        var claims = X12837Parser.Parse(combined);

        Assert.Equal(2, claims.Count);
        var second = claims[1].ClaimHeader;
        Assert.Equal("13", second.FacilityTypeCode);
        Assert.Null(second.DrgCode);
        Assert.Null(second.AdmissionDate);
        Assert.Null(second.StatementFromDate);
        Assert.Null(second.PatientStatusCode);
        Assert.Null(second.PrincipalProcedure);
        Assert.Null(second.ValueCodes);
        Assert.Equal("R509", Assert.Single(second.DiagnosisCodes!).Code);
    }

    // ── Mapper ─────────────────────────────────────────────────────────

    [Fact]
    public void Map_Inpatient_PopulatesInstitutionalDetailsAndTypeOfBill()
    {
        var claim = X12837ClaimMapper.Map(X12837Parser.Parse(InpatientDrgSample)[0], "tenant-1");
        var inst = claim.Institutional!;

        Assert.Equal(ClaimType.Institutional, claim.ClaimType);
        Assert.Equal("111", claim.TypeOfBill);
        Assert.Equal("11", inst.FacilityTypeCode);
        Assert.Equal(new DateTime(2026, 2, 10), inst.AdmissionDate);
        Assert.Equal("0815", inst.AdmissionHour);
        Assert.Equal("1030", inst.DischargeHour);
        Assert.Equal(new DateTime(2026, 2, 10), inst.StatementFromDate);
        Assert.Equal(new DateTime(2026, 2, 14), inst.StatementToDate);
        Assert.Equal(("3", "1", "01"), (inst.AdmissionTypeCode, inst.AdmissionSourceCode, inst.PatientStatusCode));
        Assert.Equal("470", inst.DrgCode);
        Assert.Equal("0SRD0JZ", inst.PrincipalProcedure!.Code);
        Assert.Equal(new DateTime(2026, 2, 10), inst.PrincipalProcedure.Date);
        Assert.Equal("BBQ", Assert.Single(inst.OtherProcedures).CodeQualifier);
        Assert.Equal(new DateTime(2026, 1, 1), Assert.Single(inst.OccurrenceCodes).Date);
        var span = Assert.Single(inst.OccurrenceSpanCodes);
        Assert.Equal((new DateTime(2026, 2, 1), new DateTime(2026, 2, 5)), (span.FromDate!.Value, span.ToDate!.Value));
        Assert.Equal(4m, Assert.Single(inst.ValueCodes).Amount);
        Assert.Equal(new[] { "39" }, inst.ConditionCodes);
        Assert.Equal(4, inst.CalculateLengthOfStay());

        Assert.Equal(new[] { "Y", null, "Y", "N" }, claim.DiagnosisCodes.Select(d => d.PresentOnAdmission));
        Assert.Equal("ABJ", claim.DiagnosisCodes[1].CodeQualifier);
    }

    [Fact]
    public void Map_Inpatient_LinesKeepRevenueCodeHcpcsAndNdc_AndInheritStatementPeriod()
    {
        var claim = X12837ClaimMapper.Map(X12837Parser.Parse(InpatientDrgSample)[0], "tenant-1");

        Assert.Equal(new DateTime(2026, 2, 10), claim.ServiceDateFrom);
        Assert.Equal(new DateTime(2026, 2, 14), claim.ServiceDateTo);

        var room = claim.ClaimLines[0];
        Assert.Equal(("0120", string.Empty), (room.RevenueCode, room.ProcedureCode));

        // No DTP*472 on line 2 → inherits the statement covers period.
        var pharmacy = claim.ClaimLines[1];
        Assert.Equal("0250", pharmacy.RevenueCode);
        Assert.Equal(new DateTime(2026, 2, 10), pharmacy.ServiceDateFrom);
        Assert.Equal(new DateTime(2026, 2, 14), pharmacy.ServiceDateTo);
        Assert.Equal(("00409123401", 2m, "ML"), (pharmacy.NationalDrugCode, pharmacy.DrugQuantity!.Value, pharmacy.DrugUnitOfMeasure));

        var surgery = claim.ClaimLines[2];
        Assert.Equal(("0360", "27447"), (surgery.RevenueCode, surgery.ProcedureCode));
        Assert.Equal(new DateTime(2026, 2, 10), surgery.ServiceDateFrom);
        Assert.Equal(new DateTime(2026, 2, 10), surgery.ServiceDateTo);
    }

    [Fact]
    public void Map_Outpatient_TypeOfBill131_RevenueCodesAndHcpcs()
    {
        var claim = X12837ClaimMapper.Map(X12837Parser.Parse(OutpatientSample)[0], "tenant-1");

        Assert.Equal("131", claim.TypeOfBill);
        Assert.Null(claim.Institutional!.DrgCode);
        Assert.Null(claim.Institutional.AdmissionDate);
        Assert.Equal(("1", "7", "01"), (claim.Institutional.AdmissionTypeCode, claim.Institutional.AdmissionSourceCode, claim.Institutional.PatientStatusCode));
        // Outpatient: no admission date → statement period, same day = 1.
        Assert.Equal(1, claim.Institutional.CalculateLengthOfStay());
        Assert.Equal(new[] { "ABK", "APR" }, claim.DiagnosisCodes.Select(d => d.CodeQualifier));

        Assert.Equal(
            new[] { ("0450", "99284"), ("0300", "80053"), ("0636", "J1885") },
            claim.ClaimLines.Select(l => (l.RevenueCode!, l.ProcedureCode)));
        Assert.Equal(new[] { "25" }, claim.ClaimLines[0].Modifiers);
        Assert.Equal("00409379601", claim.ClaimLines[2].NationalDrugCode);
        Assert.All(claim.ClaimLines, l => Assert.Equal(new DateTime(2026, 3, 5), l.ServiceDateFrom));
    }

    [Fact]
    public void Map_Professional_HasNoInstitutionalDetails()
    {
        var claim = X12837ClaimMapper.Map(X12837Parser.Parse(ProfessionalNdcSample)[0], "tenant-1");

        Assert.Null(claim.Institutional);
        Assert.Null(claim.TypeOfBill);
        Assert.Equal("00781320495", Assert.Single(claim.ClaimLines).NationalDrugCode);
    }

    [Fact]
    public void AdapterClaim_RoundTripsInstitutionalAndLineDetail()
    {
        var adapter = X12837ClaimMapper.Map(X12837Parser.Parse(InpatientDrgSample)[0], "tenant-1");

        var roundTripped = AdapterClaim.From(adapter.ToClaim());

        Assert.Equal("111", roundTripped.TypeOfBill);
        Assert.Equal("470", roundTripped.Institutional!.DrgCode);
        Assert.Equal("Y", roundTripped.DiagnosisCodes[0].PresentOnAdmission);
        Assert.Equal("00409123401", roundTripped.ClaimLines[1].NationalDrugCode);
        Assert.Equal(2m, roundTripped.ClaimLines[1].DrugQuantity);
        Assert.Equal("ML", roundTripped.ClaimLines[1].DrugUnitOfMeasure);
    }
}
