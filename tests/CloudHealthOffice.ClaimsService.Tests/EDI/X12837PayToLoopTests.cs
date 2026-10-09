using ClaimsService.EDI.Inbound;
using ClaimsService.EDI.Validation;
using CloudHealthOffice.ClaimsService.Tests.EDI.Snip;
using Xunit;
using static CloudHealthOffice.ClaimsService.Tests.EDI.Snip.Snip837Samples;

namespace CloudHealthOffice.ClaimsService.Tests.EDI;

/// <summary>
/// 837 Loop 2010AB (pay-to address) and Loop 2010AC (pay-to plan) for 837P
/// (005010X222A1) and 837I (005010X223A2): parsing, mapping onto the stored
/// claim, and SNIP validation. In 5010, 2010AB is an address only (NM1*87
/// with NM103 onward not used: X12 RFI 1522); 2010AC is used only on a
/// subrogation demand, BHT06 = 31 (X12 RFI 1036).
/// </summary>
public class X12837PayToLoopTests
{
    private static readonly string[] PayToAddressLoop =
    [
        "NM1*87*2",
        "N3*PO BOX 1234*DEPT 7",
        "N4*SPRINGFIELD*IL*627010001",
    ];

    private static readonly string[] PayToPlanLoop =
    [
        "NM1*PE*2*SYNTHETIC MEDICAID PLAN*****PI*PLAN01",
        "N3*1 PLAN WAY",
        "N4*CAPITAL CITY*IL*62701",
        "REF*EI*000000001",
    ];

    /// <summary>Inserts loops after 2010AA (before the 2000B HL), as the TR3 orders them.</summary>
    private static void InsertAfterBillingProvider(List<string> body, params string[] segments)
    {
        var subscriberHl = body.FindIndex(s => s.StartsWith("HL*2*", StringComparison.Ordinal));
        body.InsertRange(subscriberHl, segments);
    }

    private static string Build(bool institutional, bool payToAddress, bool payToPlan, string bht06 = "CH")
    {
        void Mutate(List<string> body)
        {
            body[0] = body[0][..body[0].LastIndexOf('*')] + "*" + bht06;
            if (payToAddress) InsertAfterBillingProvider(body, PayToAddressLoop);
            if (payToPlan) InsertAfterBillingProvider(body, PayToPlanLoop);
        }
        return institutional ? Institutional(Mutate) : Professional(Mutate);
    }

    // ── Parser ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_WithoutPayToLoops_BothNull(bool institutional)
    {
        var claim = Assert.Single(X12837Parser.Parse(Build(institutional, payToAddress: false, payToPlan: false)));

        Assert.Null(claim.PayToAddress);
        Assert.Null(claim.PayToPlan);
        Assert.Equal("CH", claim.TransactionTypeCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_PayToAddress_IsAddressOnly_BillingProviderUnchanged(bool institutional)
    {
        var claim = Assert.Single(X12837Parser.Parse(Build(institutional, payToAddress: true, payToPlan: false)));

        var address = Assert.IsType<CloudHealthOffice.ClaimsScrubEngine.Models.ProviderAddress>(claim.PayToAddress);
        Assert.Equal(("PO BOX 1234", "DEPT 7", "SPRINGFIELD", "IL", "627010001"),
            (address.Line1, address.Line2, address.City, address.State, address.PostalCode));
        Assert.Null(claim.PayToPlan);
        // 2010AA keeps its own address; the pay-to N3/N4 never overwrite it.
        Assert.Equal(institutional ? "200 HOSPITAL DR" : "100 MAIN ST", claim.BillingProvider.Address.Line1);
        Assert.Equal(institutional ? "1003000126" : "1234567893", claim.BillingProvider.Npi);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_PayToPlan_NameIdTaxIdAndAddress(bool institutional)
    {
        var claim = Assert.Single(X12837Parser.Parse(Build(institutional, payToAddress: true, payToPlan: true, bht06: "31")));

        var plan = claim.PayToPlan!;
        Assert.Equal(("SYNTHETIC MEDICAID PLAN", "PI", "PLAN01", "000000001"),
            (plan.Name, plan.IdentificationQualifier, plan.IdentificationCode, plan.TaxId));
        Assert.Equal(("1 PLAN WAY", "CAPITAL CITY", "IL", "62701"),
            (plan.Address!.Line1, plan.Address.City, plan.Address.State, plan.Address.PostalCode));
        Assert.Equal("PO BOX 1234", claim.PayToAddress!.Line1);
        // The plan's REF*EI is not the billing provider's tax id.
        Assert.Equal(institutional ? "987654321" : "123456789", claim.BillingProvider.TaxId);
        Assert.Equal("31", claim.TransactionTypeCode);
    }

    [Fact]
    public void Parse_PayToPlanWithoutId_DoesNotTakeTheNameAsTheId()
    {
        var edi = Professional(body => InsertAfterBillingProvider(body, "NM1*PE*2*SYNTHETIC MEDICAID PLAN", "N3*1 PLAN WAY", "N4*CAPITAL CITY*IL*62701"));

        var plan = Assert.Single(X12837Parser.Parse(edi)).PayToPlan!;

        Assert.Equal("SYNTHETIC MEDICAID PLAN", plan.Name);
        Assert.Null(plan.IdentificationQualifier);
        Assert.Null(plan.IdentificationCode);
    }

    [Fact]
    public void Parse_PayToLoops_BelongToTheirBillingProvider_NotTheNextOne()
    {
        // Two 2000A loops: only the first has a pay-to address.
        var edi = Wrap(ProfessionalVersion, WithPayTo(ProfessionalBody("PCN-A")), ProfessionalBody("PCN-B"));

        var claims = X12837Parser.Parse(edi);

        Assert.Equal("PO BOX 1234", claims.Single(c => c.ClaimId == "PCN-A").PayToAddress!.Line1);
        Assert.Null(claims.Single(c => c.ClaimId == "PCN-B").PayToAddress);

        static List<string> WithPayTo(List<string> body)
        {
            InsertAfterBillingProvider(body, PayToAddressLoop);
            return body;
        }
    }

    // ── One ST, several HLs: each claim keeps its own HL's data ───────

    /// <summary>
    /// The 2000A/2000B part of a sample body (from its HL*20 onward), with
    /// HL ids renumbered from <paramref name="firstHl"/>, the billing
    /// provider NPI, subscriber member id and claim id replaced, and
    /// <paramref name="payToLoops"/> inserted after 2010AA.
    /// </summary>
    private static List<string> BillingProviderBlock(bool institutional, int firstHl, string npi, string memberId, string claimId, params string[] payToLoops)
    {
        var body = institutional ? InstitutionalBody(claimId) : ProfessionalBody(claimId);
        var block = body.Skip(body.FindIndex(s => s.StartsWith("HL*1*", StringComparison.Ordinal))).ToList();
        block.Replace("HL*1*", $"HL*{firstHl}**20*1");
        block.Replace("HL*2*", $"HL*{firstHl + 1}*{firstHl}*22*0");
        var nm185 = block.FindIndex(s => s.StartsWith("NM1*85*", StringComparison.Ordinal));
        block[nm185] = block[nm185][..block[nm185].LastIndexOf('*')] + "*" + npi;
        block.Replace("NM1*IL*", $"NM1*IL*1*TESTPATIENT*{memberId}****MI*{memberId}");
        block.InsertRange(block.FindIndex(s => s.StartsWith($"HL*{firstHl + 1}*", StringComparison.Ordinal)), payToLoops);
        return block;
    }

    /// <summary>BHT and 1000A/1000B of a sample body (everything before its HL*20).</summary>
    private static List<string> Header(bool institutional, string bht06 = "CH")
    {
        var body = institutional ? InstitutionalBody() : ProfessionalBody();
        var header = body.Take(body.FindIndex(s => s.StartsWith("HL*1*", StringComparison.Ordinal))).ToList();
        header[0] = header[0][..header[0].LastIndexOf('*')] + "*" + bht06;
        return header;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_SingleSt_TwoBillingProviders_EachClaimKeepsItsOwnHl(bool institutional)
    {
        // One ST: provider A (pay-to address A, no pay-to plan) with PCN-A,
        // then provider B (pay-to address B and a pay-to plan) with PCN-B.
        // PCN-A must not pick up B's NPI, member, pay-to address or plan
        // (a stray 2010AC would wrongly exclude PCN-A from payment).
        var body = Header(institutional, bht06: "31");
        body.AddRange(BillingProviderBlock(institutional, 1, "1234567893", "MEM-A", "PCN-A",
            "NM1*87*2", "N3*PO BOX A", "N4*SPRINGFIELD*IL*62701"));
        body.AddRange(BillingProviderBlock(institutional, 3, "1003000126", "MEM-B", "PCN-B",
            ["NM1*87*2", "N3*PO BOX B", "N4*CHICAGO*IL*60601", .. PayToPlanLoop]));
        var edi = Wrap(institutional ? InstitutionalVersion : ProfessionalVersion, body);

        var claims = X12837Parser.Parse(edi);

        Assert.Equal(2, claims.Count);
        var a = claims.Single(c => c.ClaimId == "PCN-A");
        var b = claims.Single(c => c.ClaimId == "PCN-B");
        Assert.Equal(("1234567893", "MEM-A", "PO BOX A"), (a.BillingProvider.Npi, a.Subscriber.MemberId, a.PayToAddress!.Line1));
        Assert.Null(a.PayToPlan);
        Assert.Equal(("1003000126", "MEM-B", "PO BOX B"), (b.BillingProvider.Npi, b.Subscriber.MemberId, b.PayToAddress!.Line1));
        Assert.Equal("PLAN01", b.PayToPlan!.IdentificationCode);
        Assert.Equal(2, a.ServiceLines.Count);
        Assert.Equal(2, b.ServiceLines.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_SingleSt_TwoSubscribersUnderOneBillingProvider_EachClaimKeepsItsOwnSubscriber(bool institutional)
    {
        // One HL*20 with a pay-to address; HL*22 subscriber A (who is the
        // patient) with PCN-A, then HL*22 subscriber B with an HL*23
        // dependent and PCN-B.
        var body = Header(institutional);
        body.AddRange(BillingProviderBlock(institutional, 1, "1234567893", "MEM-A", "PCN-A", PayToAddressLoop));
        var second = BillingProviderBlock(institutional, 1, "1234567893", "MEM-B", "PCN-B");
        second = second.Skip(second.FindIndex(s => s.StartsWith("HL*2*", StringComparison.Ordinal))).ToList();
        second.Replace("HL*2*", "HL*3*1*22*1");
        second.InsertRange(second.FindIndex(s => s.StartsWith("CLM*", StringComparison.Ordinal)),
            ["HL*4*3*23*0", "PAT*19", "NM1*QC*1*TESTCHILD*JO", "DMG*D8*20150101*M"]);
        body.AddRange(second);
        var edi = Wrap(institutional ? InstitutionalVersion : ProfessionalVersion, body);

        var claims = X12837Parser.Parse(edi);

        Assert.Equal(2, claims.Count);
        var a = claims.Single(c => c.ClaimId == "PCN-A");
        var b = claims.Single(c => c.ClaimId == "PCN-B");
        Assert.Equal("MEM-A", a.Subscriber.MemberId);
        Assert.Null(a.Patient);
        Assert.Equal("MEM-B", b.Subscriber.MemberId);
        Assert.Equal(("TESTCHILD", "19"), (b.Patient!.LastName, b.Patient.RelationshipCode));
        // Both claims sit under the one HL*20, so both carry its pay-to address.
        Assert.Equal(("PO BOX 1234", "PO BOX 1234"), (a.PayToAddress!.Line1, b.PayToAddress!.Line1));
        Assert.Equal(2, a.ServiceLines.Count);
    }

    // ── Mapper → stored claim → claim search wire ────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Map_PayToAddressAndPlan_ReachTheStoredClaim(bool institutional)
    {
        var parsed = Assert.Single(X12837Parser.Parse(Build(institutional, payToAddress: true, payToPlan: true, bht06: "31")));

        var claim = X12837ClaimMapper.Map(parsed, "tenant-1").ToClaim();

        Assert.Equal(("PO BOX 1234", "DEPT 7", "SPRINGFIELD", "IL", "627010001", (string?)null),
            (claim.PayToAddress!.Line1, claim.PayToAddress.Line2, claim.PayToAddress.City, claim.PayToAddress.State, claim.PayToAddress.PostalCode, claim.PayToAddress.CountryCode));
        Assert.Equal(("SYNTHETIC MEDICAID PLAN", "PI", "PLAN01", "000000001", "1 PLAN WAY"),
            (claim.PayToPlan!.Name, claim.PayToPlan.IdentifierQualifier, claim.PayToPlan.Identifier, claim.PayToPlan.TaxId, claim.PayToPlan.Address!.Line1));
        Assert.Equal(institutional ? "1003000126" : "1234567893", claim.BillingProviderNPI);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Map_WithoutPayToLoops_BothNull(bool institutional)
    {
        var parsed = Assert.Single(X12837Parser.Parse(Build(institutional, payToAddress: false, payToPlan: false)));

        var claim = X12837ClaimMapper.Map(parsed, "tenant-1").ToClaim();

        Assert.Null(claim.PayToAddress);
        Assert.Null(claim.PayToPlan);
    }

    [Fact]
    public void AdapterClaim_RoundTrip_KeepsPayToLoops()
    {
        var parsed = Assert.Single(X12837Parser.Parse(Build(false, payToAddress: true, payToPlan: true, bht06: "31")));
        var claim = X12837ClaimMapper.Map(parsed, "tenant-1").ToClaim();

        var roundTripped = global::ClaimsService.Models.AdapterClaim.From(claim).ToClaim();

        Assert.Same(claim.PayToAddress, roundTripped.PayToAddress);
        Assert.Same(claim.PayToPlan, roundTripped.PayToPlan);
    }

    // ── SNIP ─────────────────────────────────────────────────────────

    private static SnipValidationResult Validate(string edi) => new X12837SnipValidator().Validate(edi);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Snip_CleanPayToAddress_NoIssues(bool institutional)
    {
        var result = Validate(Build(institutional, payToAddress: true, payToPlan: false));

        Assert.Empty(result.AllIssues);
        Assert.Equal("A", result.AcknowledgmentCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Snip_CleanSubrogationDemandWithPayToPlan_NoIssues(bool institutional)
    {
        var result = Validate(Build(institutional, payToAddress: true, payToPlan: true, bht06: "31"));

        Assert.Empty(result.AllIssues);
    }

    [Fact]
    public void Snip_PayToAddressWithName_NM103IsNotUsed()
    {
        var edi = Professional(body => InsertAfterBillingProvider(body, "NM1*87*2*ACME PAYEE*****XX*1234567893", "N3*PO BOX 1", "N4*SPRINGFIELD*IL*62701"));

        var result = Validate(edi);
        var issues = result.AllIssues.Where(i => i.RuleId == "L2-2010AB-NOT-USED").ToList();

        Assert.Equal(new int?[] { 3, 8, 9 }, issues.Select(i => i.ElementPosition));
        Assert.All(issues, i => Assert.Equal(("I10", SnipLevel.ImplementationGuide), (i.ElementErrorCode, i.Level)));
        // The 4010-style NM1*87*2*NAME*****XX*NPI is still common: it warns
        // but never rejects the transaction set.
        Assert.All(issues, i => Assert.Equal(SnipSeverity.Warning, i.Severity));
        Assert.DoesNotContain(result.AllIssues, i => i.Severity == SnipSeverity.Error);
        Assert.NotEqual("R", result.AcknowledgmentCode);
        Assert.Equal("PO BOX 1", Assert.Single(X12837Parser.Parse(edi)).PayToAddress!.Line1);
    }

    [Fact]
    public void Snip_PayToAddressRepeatedInOneBillingProviderLoop_IsAWarning()
    {
        var edi = Professional(body => InsertAfterBillingProvider(body,
            ["NM1*87*2", "N3*PO BOX 1", "N4*SPRINGFIELD*IL*62701", .. PayToAddressLoop]));

        var result = Validate(edi);

        var issue = Assert.Single(result.AllIssues);
        Assert.Equal(("L2-2010AB-REPEAT", SnipSeverity.Warning, "2010AB", "5"), (issue.RuleId, issue.Severity, issue.Loop, issue.SegmentErrorCode));
        Assert.NotEqual("R", result.AcknowledgmentCode);
    }

    [Theory]
    [InlineData("N3", "L2-2010AB-N3")]
    [InlineData("N4", "L2-2010AB-N4")]
    public void Snip_PayToAddressWithoutN3OrN4_IsReported(string missing, string rule)
    {
        var edi = Professional(body => InsertAfterBillingProvider(body, PayToAddressLoop.Where(s => !s.StartsWith(missing + "*", StringComparison.Ordinal)).ToArray()));

        var issue = Assert.Single(Validate(edi).AllIssues, i => i.RuleId == rule);

        Assert.Equal(("3", "2010AB"), (issue.SegmentErrorCode, issue.Loop));
    }

    [Fact]
    public void Snip_PayToAddressEntityTypeInvalid_IsReported()
    {
        var edi = Professional(body => InsertAfterBillingProvider(body, "NM1*87*X", "N3*PO BOX 1", "N4*SPRINGFIELD*IL*62701"));

        Assert.Single(Validate(edi).AllIssues, i => i.RuleId == "L2-NM102" && i.Loop == "2010AB");
    }

    [Theory]
    [InlineData("NM1*PE*2*SYNTHETIC MEDICAID PLAN*****PI*PLAN01", "N3*", "L2-2010AC-N3")]
    [InlineData("NM1*PE*2*SYNTHETIC MEDICAID PLAN*****PI*PLAN01", "N4*", "L2-2010AC-N4")]
    [InlineData("NM1*PE*2*SYNTHETIC MEDICAID PLAN*****PI*PLAN01", "REF*", "L2-2010AC-TAXID")]
    [InlineData("NM1*PE*2******PI*PLAN01", null, "L2-NM103")]
    [InlineData("NM1*PE*2*SYNTHETIC MEDICAID PLAN*****XX*1234567893", null, "L2-NM108")]
    [InlineData("NM1*PE*1*SYNTHETIC MEDICAID PLAN*****PI*PLAN01", null, "L2-NM102")]
    public void Snip_PayToPlanMissingRequiredData_IsReported(string nm1, string? dropPrefix, string rule)
    {
        var loop = new[] { nm1 }.Concat(PayToPlanLoop.Skip(1).Where(s => dropPrefix is null || !s.StartsWith(dropPrefix, StringComparison.Ordinal))).ToArray();
        var edi = Professional(body =>
        {
            body[0] = body[0][..body[0].LastIndexOf('*')] + "*31";
            InsertAfterBillingProvider(body, loop);
        });

        var issue = Assert.Single(Validate(edi).AllIssues, i => i.RuleId == rule);

        Assert.Equal(SnipLevel.ImplementationGuide, issue.Level);
        Assert.Equal("2010AC", issue.Loop);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Snip_PayToPlanOnAChargeableClaim_IsASituationalError(bool institutional)
    {
        var result = Validate(Build(institutional, payToAddress: false, payToPlan: true, bht06: "CH"));

        var issue = Assert.Single(result.AllIssues);
        Assert.Equal(("L4-2010AC-BHT06", SnipLevel.Situational, "I9", "NM1"), (issue.RuleId, issue.Level, issue.SegmentErrorCode, issue.SegmentId));
    }
}
