using ClaimsService.EDI.Inbound;
using ClaimsService.Models;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication.Stages;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.EDI;

/// <summary>
/// Coordination-of-benefits data on an inbound 837 (005010X222A1): our payer
/// responsibility from 2000B SBR01, the other payers from 2320 SBR / AMT*D /
/// CAS and 2330B NM1*PR, and their line adjudication from 2430 SVD / CAS —
/// parsed, mapped onto the claim, and turned into the benefit engine's COB
/// input.
/// </summary>
public class X12837CobTests
{
    // We are tertiary (2000B SBR*T). Primary: 2320 SBR*P, AMT*D 112, line
    // level 2430 on both lines. Secondary: 2320 SBR*S, claim-level CAS and
    // AMT*D 60, no 2430. 2330A NM1*IL / 2330B NM1*PR must not overwrite the
    // claim's own subscriber or rendering provider.
    private const string Tertiary837 = """
        ISA*00*          *00*          *ZZ*SUBMITTER02    *ZZ*CHOPAYER       *260426*0930*^*00501*000000207*0*T*:~
        GS*HC*SUBMITTER02*CHOPAYER*20260426*0930*207*X*005010X222A1~
        ST*837*0207*005010X222A1~
        BHT*0019*00*GOLD-BATCH-0207*20260426*0930*CH~
        NM1*41*2*SYNTHETIC FAMILY CLINIC*****46*SUBMITTER02~
        PER*IC*BILLING OFFICE*TE*5555550100~
        NM1*40*2*CLOUD HEALTH OFFICE GOLDEN HEALTH PLAN*****46*CHOPAYER~
        HL*1**20*1~
        NM1*85*2*SYNTHETIC FAMILY CLINIC*****XX*1999999943~
        N3*200 OAK ST~
        N4*ANYTOWN*TX*75001~
        REF*EI*990000002~
        HL*2*1*22*0~
        SBR*T*18*GRP-GOLD******CI~
        NM1*IL*1*TESTPATIENT*RILEY****MI*MBR-GOLD-07~
        DMG*D8*19780214*F~
        NM1*PR*2*CLOUD HEALTH OFFICE GOLDEN HEALTH PLAN*****PI*CHOGOLD01~
        CLM*GOLD-TERT-0001*580.00***11:B:1*Y*A*Y*Y~
        HI*ABK:I10~
        NM1*82*1*DOCTOR*DANA****XX*1999999976~
        SBR*P*01*PRIMGRP01******CI~
        AMT*D*112.00~
        OI***Y*P**Y~
        NM1*IL*1*TESTPATIENT*RILEY****MI*PRIM-778899~
        NM1*PR*2*SYNTHETIC PRIMARY INSURANCE*****PI*OTHERPAYER1~
        SBR*S*01*SECGRP02******CI~
        CAS*OA*23*462.00~
        CAS*PR*1*50.00**2*8.00~
        AMT*D*60.00~
        OI***Y*P**Y~
        NM1*IL*1*TESTPATIENT*RILEY****MI*SEC-445566~
        NM1*82*1*OTHERDOC*PAT****XX*1999999992~
        NM1*PR*2*SYNTHETIC SECONDARY INSURANCE*****PI*OTHERPAYER2~
        LX*1~
        SV1*HC:99214*400.00*UN*1*11**1~
        DTP*472*D8*20260419~
        SVD*OTHERPAYER1*40.00*HC:99214**1~
        CAS*CO*45*260.00~
        CAS*PR*1*100.00~
        DTP*573*D8*20260422~
        LX*2~
        SV1*HC:99213*180.00*UN*1*11**1~
        DTP*472*D8*20260419~
        SVD*OTHERPAYER1*72.00*HC:99213**1~
        CAS*CO*45*90.00~
        CAS*PR*2*18.00~
        DTP*573*D8*20260422~
        SE*46*0207~
        GE*1*207~
        IEA*1*000000207~
        """;

    [Fact]
    public void Parse_ReadsPayerResponsibility_OtherPayers_AndLineAdjudication()
    {
        var claim = Assert.Single(X12837Parser.Parse(Tertiary837));

        Assert.Equal("T", claim.PayerResponsibilityCode);
        // 2330A/2330B/2330D NM1 segments did not overwrite the claim's own data.
        Assert.Equal("MBR-GOLD-07", claim.Subscriber.MemberId);
        Assert.Equal("1999999976", claim.ClaimHeader.RenderingProvider!.Npi);
        Assert.Equal("TESTPATIENT", claim.Subscriber.LastName);
        Assert.Equal("19780214", claim.Subscriber.DateOfBirth);

        var payers = claim.OtherPayers!;
        Assert.Equal(2, payers.Count);
        Assert.Equal(("P", "SYNTHETIC PRIMARY INSURANCE", "OTHERPAYER1", 112m),
            (payers[0].PayerResponsibilityCode, payers[0].PayerName, payers[0].PayerId, payers[0].PaidAmount));
        Assert.Empty(payers[0].ClaimAdjustments);
        Assert.Equal(("S", "OTHERPAYER2", 60m),
            (payers[1].PayerResponsibilityCode, payers[1].PayerId, payers[1].PaidAmount));
        Assert.Equal(
            new[] { ("OA", "23", 462m), ("PR", "1", 50m), ("PR", "2", 8m) },
            payers[1].ClaimAdjustments.Select(a => (a.GroupCode, a.ReasonCode, a.Amount)));

        var line1 = Assert.Single(claim.ServiceLines[0].OtherPayerAdjudications!);
        Assert.Equal(("OTHERPAYER1", 40m), (line1.PayerId, line1.PaidAmount));
        Assert.Equal(new[] { ("CO", "45", 260m), ("PR", "1", 100m) },
            line1.Adjustments.Select(a => (a.GroupCode, a.ReasonCode, a.Amount)));
        var line2 = Assert.Single(claim.ServiceLines[1].OtherPayerAdjudications!);
        Assert.Equal(72m, line2.PaidAmount);
        Assert.Equal(new[] { ("CO", "45", 90m), ("PR", "2", 18m) },
            line2.Adjustments.Select(a => (a.GroupCode, a.ReasonCode, a.Amount)));
        // Line charges are untouched by the 2430 loops.
        Assert.Equal(new[] { 400m, 180m }, claim.ServiceLines.Select(l => l.ChargeAmount));
    }

    [Fact]
    public void Parse_PrimaryClaim_HasNoOtherPayers()
    {
        var edi = Tertiary837.Replace("SBR*T*18", "SBR*P*18");
        var cut = edi.IndexOf("SBR*P*01", StringComparison.Ordinal);
        var lx = edi.IndexOf("LX*1", StringComparison.Ordinal);
        edi = edi.Remove(cut, lx - cut);

        var claim = Assert.Single(X12837Parser.Parse(edi));

        Assert.Equal("P", claim.PayerResponsibilityCode);
        Assert.Null(claim.OtherPayers);
    }

    [Fact]
    public void Map_AttachesLineAdjudicationToItsPayer()
    {
        var adapter = X12837ClaimMapper.Map(Assert.Single(X12837Parser.Parse(Tertiary837)), "tenant-1");

        Assert.Equal("T", adapter.PayerResponsibilityCode);
        Assert.Equal(new[] { "P", "S" }, adapter.OtherPayers.Select(p => p.PayerResponsibilityCode));
        var primary = adapter.OtherPayers[0];
        Assert.Equal(new[] { (1, 40m), (2, 72m) }, primary.LineAdjudications.Select(l => (l.LineNumber, l.PaidAmount)));
        Assert.Empty(adapter.OtherPayers[1].LineAdjudications);

        // Survives the adapter ↔ persisted claim round trip.
        var roundTripped = AdapterClaim.From(adapter.ToClaim());
        Assert.Equal("T", roundTripped.PayerResponsibilityCode);
        Assert.Equal(2, roundTripped.OtherPayers.Count);
        Assert.Equal(2, roundTripped.OtherPayers[0].LineAdjudications.Count);
    }

    [Fact]
    public void BuildCob_Tertiary_SendsBothPriorPayersWithLineAndClaimData()
    {
        var adapter = X12837ClaimMapper.Map(Assert.Single(X12837Parser.Parse(Tertiary837)), "tenant-1");

        var cob = BenefitCalculationStage.BuildCob(adapter)!;

        Assert.Equal(3, cob.PayerSequence);
        Assert.True(cob.UseComplementaryModel);
        Assert.Equal("OTHERPAYER1", cob.PrimaryPayerId);
        Assert.Equal(new[] { 1, 2 }, cob.PriorPayers.Select(p => p.Sequence));
        Assert.Equal(112m, cob.PriorPayers[0].ClaimPaidAmount);
        Assert.Equal(2, cob.PriorPayers[0].Lines.Count);
        Assert.Equal(60m, cob.PriorPayers[1].ClaimPaidAmount);
        Assert.Equal(3, cob.PriorPayers[1].ClaimAdjustments.Count);
    }

    [Theory]
    [InlineData("P")]
    [InlineData("U")]
    [InlineData(null)]
    public void BuildCob_FirstOrUnknownPayer_NoCob(string? sbr01)
    {
        var adapter = X12837ClaimMapper.Map(Assert.Single(X12837Parser.Parse(Tertiary837)), "tenant-1");
        adapter.PayerResponsibilityCode = sbr01;

        Assert.Null(BenefitCalculationStage.BuildCob(adapter));
    }

    [Fact]
    public void BuildCob_Secondary_KeepsOnlyEarlierPayers()
    {
        var adapter = X12837ClaimMapper.Map(Assert.Single(X12837Parser.Parse(Tertiary837)), "tenant-1");
        adapter.PayerResponsibilityCode = "S";

        var cob = BenefitCalculationStage.BuildCob(adapter)!;

        Assert.Equal(2, cob.PayerSequence);
        Assert.Equal(new[] { 1 }, cob.PriorPayers.Select(p => p.Sequence));
    }

    [Fact]
    public void FinalizedEvent_CarriesDeductibleCredited_OnlyWhenItDiffersFromPr1()
    {
        var claim = new Claim
        {
            Id = "c-1",
            ClaimNumber = "C-1",
            MemberId = "MEM-1",
            ServiceDateFrom = new DateTime(2026, 4, 15),
            Status = ClaimStatus.Paid,
            AdjudicationResult = new AdjudicationResult { DeductibleAmount = 30m, DeductibleCreditedAmount = 90m, PatientResponsibility = 30m },
            ClaimLines =
            {
                new ClaimLine
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    AdjudicationResult = new LineAdjudicationResult
                    {
                        PatientResponsibility = 30m,
                        DeductibleCreditedAmount = 90m,
                        AdjustmentReasons = { new ClaimAdjustmentReason { GroupCode = "PR", ReasonCode = "1", Amount = 30m } },
                    },
                },
                new ClaimLine
                {
                    LineNumber = 2,
                    ProcedureCode = "99212",
                    AdjudicationResult = new LineAdjudicationResult { DeductibleCreditedAmount = 0m },
                },
            },
        };

        var evt = ClaimEventPublisher.BuildFinalizedEvent(claim, "tenant-1");

        Assert.Equal(90m, evt.DeductibleCredited);
        Assert.Equal(30m, evt.DeductibleApplied);
        Assert.Equal(new decimal?[] { 90m, null }, evt.LineItems.Select(l => l.DeductibleCredited));
    }
}
