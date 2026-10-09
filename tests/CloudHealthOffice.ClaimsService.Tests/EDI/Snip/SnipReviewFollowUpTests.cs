using ClaimsService.EDI.Inbound;
using ClaimsService.EDI.Validation;
using Xunit;
using static CloudHealthOffice.ClaimsService.Tests.EDI.Snip.Snip837Samples;

namespace CloudHealthOffice.ClaimsService.Tests.EDI.Snip;

/// <summary>
/// Regression tests for the PR #1272 review: truncated files, type-of-bill
/// classification, envelope rejections in the 999, finding caps, ICD-10
/// chapter U, segment-id echoing, 999 envelope details, missing-segment
/// positions, loop-scoped rules and Level 1 group codes.
/// </summary>
public class SnipReviewFollowUpTests
{
    private static SnipValidationResult Validate(string edi, Snip837ValidationOptions? options = null, ISnipCodeSetReference? codeSets = null)
        => new X12837SnipValidator(options, codeSets).Validate(edi);

    private static List<X12Segment> Ack(SnipValidationResult result, X12999AcknowledgmentBuilder.Options? options = null)
        => X12Tokenizer.Tokenize(X12999AcknowledgmentBuilder.Build(result, options ?? new X12999AcknowledgmentBuilder.Options { ControlNumber = 7 })!).Segments.ToList();

    private static string Seg(X12Segment s) => s.Id + "*" + string.Join('*', s.Elements);

    /// <summary>The invariant: the 999 accepts exactly the sets the import will submit.</summary>
    private static void AssertAckMatchesAcceptance(SnipValidationResult result)
    {
        var accepted = result.AcceptedTransactionSets.Count();
        var segs = Ack(result);
        Assert.Equal(accepted, segs.Count(s => s.Id == "IK5" && s.Elements[0] is "A" or "E"));
        Assert.Equal(accepted, segs.Where(s => s.Id == "AK9").Sum(s => int.Parse(s.Elements[3])));
        Assert.Equal(result.TransactionSets.Count(), segs.Count(s => s.Id == "IK5"));
    }

    // ── 1. Truncated file / never throws ─────────────────────────────

    [Fact]
    public void FileCutOffBeforeSe_DoesNotThrowAndRejects()
    {
        var full = Professional();
        var truncated = full[..full.IndexOf("SE*", StringComparison.Ordinal)];

        var result = Validate(truncated);

        Assert.Contains(result.AllIssues, i => i.RuleId == "L1-SE-MISSING");
        Assert.Contains(result.AllIssues, i => i.RuleId == "L1-GE-MISSING");
        Assert.Contains(result.AllIssues, i => i.RuleId == "L1-IEA-MISSING");
        Assert.Equal("R", result.AcknowledgmentCode);
        Assert.Empty(result.AcceptedTransactionSets);
        AssertAckMatchesAcceptance(result);
    }

    [Fact]
    public void UnexpectedFailure_BecomesL1FindingInsteadOfThrowing()
    {
        var result = Validate(Professional(), codeSets: new ThrowingCodeSets());

        var issue = Assert.Single(result.AllIssues);
        Assert.Equal(("L1-VALIDATION-FAILED", SnipSeverity.Error), (issue.RuleId, issue.Severity));
        Assert.Equal("R", result.AcknowledgmentCode);
        Assert.Empty(result.AcceptedTransactionSets);
        Assert.Null(X12999AcknowledgmentBuilder.Build(result));
    }

    private sealed class ThrowingCodeSets : ISnipCodeSetReference
    {
        public bool? IsValid(string codeSystem, string code, DateOnly? serviceDate) => throw new InvalidOperationException("boom");
    }

    // ── 2. Type of bill classification ───────────────────────────────

    private static string InstitutionalWithTob(string tob, Action<List<string>>? more = null) => Institutional(b =>
    {
        b.Replace("CLM*", $"CLM*PCN0002*300.00***{tob}:A:1**A*Y*Y");
        more?.Invoke(b);
    });

    [Theory]
    [InlineData("71")]  // RHC clinic: outpatient
    [InlineData("81")]  // hospice: not classified, but has no admission
    public void Tob_NonInpatient_DoesNotRequireAdmissionDateAsAnError(string tob)
    {
        var result = Validate(InstitutionalWithTob(tob));
        Assert.DoesNotContain(result.AllIssues, i => i.RuleId == "L4-DTP435" && i.Severity == SnipSeverity.Error);
    }

    [Theory]
    [InlineData("18")]  // swing bed
    [InlineData("28")]
    [InlineData("65")]  // ICF
    [InlineData("66")]
    [InlineData("86")]
    public void Tob_Inpatient_RequiresAdmissionNotLineDates(string tob)
    {
        // Multi-day statement, a line without DTP*472, and no DTP*435.
        var result = Validate(InstitutionalWithTob(tob, b => b.RemoveSegment("DTP*472*D8*20260106")));

        Assert.Equal(SnipSeverity.Error, Assert.Single(result.AllIssues, i => i.RuleId == "L4-DTP435").Severity);
        Assert.DoesNotContain(result.AllIssues, i => i.RuleId == "L4-DTP472");
    }

    [Fact]
    public void Tob_Unclassified_RulesOnlyWarn()
    {
        // 81x (hospice) is in neither table.
        var result = Validate(InstitutionalWithTob("81", b => b.RemoveSegment("DTP*472*D8*20260106")));

        Assert.All(result.AllIssues.Where(i => i.RuleId is "L4-DTP435" or "L4-DTP472"),
            i => Assert.Equal(SnipSeverity.Warning, i.Severity));
        Assert.Contains(result.AllIssues, i => i.RuleId == "L4-DTP472");
        Assert.Single(result.AcceptedTransactionSets);
    }

    [Fact]
    public void Tob_Outpatient13_StillRequiresLineDates()
    {
        var result = Validate(Institutional(b => b.RemoveSegment("DTP*472*D8*20260106")));
        Assert.Equal(SnipSeverity.Error, Assert.Single(result.AllIssues, i => i.RuleId == "L4-DTP472").Severity);
    }

    // ── 3. Envelope errors reach the 999 ─────────────────────────────

    [Fact]
    public void BadGs04_RejectsGroupAndAk9()
    {
        var result = Validate(Professional().Replace("*SUB001*CHO*20260115*", "*SUB001*CHO*20261399*"));

        var group = Assert.Single(result.FunctionalGroups);
        Assert.Contains("1", group.GroupErrorCodes);
        Assert.Empty(result.AcceptedTransactionSets);
        var segs = Ack(result);
        Assert.Equal("AK9*R*1*1*0*1", Seg(segs.Single(s => s.Id == "AK9")));
        Assert.Equal("R", segs.Single(s => s.Id == "IK5").Elements[0]);
    }

    [Fact]
    public void IsaError_ForcesEveryIk5AndAk9ToR()
    {
        var edi = Wrap(ProfessionalVersion, ProfessionalBody(), ProfessionalBody("PCN0002")).Replace("*0*T*:", "*0*X*:");
        var result = Validate(edi);

        Assert.Contains(result.AllIssues, i => i.RuleId == "L1-ISA15");
        var segs = Ack(result);
        Assert.All(segs.Where(s => s.Id == "IK5"), s => Assert.Equal("R", s.Elements[0]));
        Assert.Equal("R", segs.Single(s => s.Id == "AK9").Elements[0]);
        Assert.Empty(result.AcceptedTransactionSets);
    }

    [Theory]
    [MemberData(nameof(InvariantCases))]
    public void Ack999_AlwaysAgreesWithAcceptedTransactionSets(string name, string edi)
    {
        _ = name;
        AssertAckMatchesAcceptance(Validate(edi));
        AssertAckMatchesAcceptance(Validate(edi, new Snip837ValidationOptions { Level1 = SnipAction.Warn, Level4 = SnipAction.Warn }));
    }

    public static IEnumerable<object[]> InvariantCases()
    {
        var two = Wrap(ProfessionalVersion, ProfessionalBody(), ProfessionalBody("PCN0002"));
        yield return ["clean", Professional()];
        yield return ["clean-two-sets", two];
        yield return ["balancing", Professional(b => b.Replace("CLM*", "CLM*PCN0001*175.00***11:B:1*Y*A*Y*Y"))];
        yield return ["ge-count", Professional().Replace("GE*1*101", "GE*3*101")];
        yield return ["iea-control", two.Replace("IEA*1*000000101", "IEA*1*000000999")];
        yield return ["gs04", Professional().Replace("*SUB001*CHO*20260115*", "*SUB001*CHO*2026011*")];
        yield return ["l5-warning", Professional(b => b.Replace("HI*", "HI*ABK:J06.9*ABF:R05"))];
        yield return ["one-bad-npi", Wrap(ProfessionalVersion, ProfessionalBody(), Bad(ProfessionalBody("PCN0002")))];
        yield return ["stray-segment", Professional().Replace("GE*1*101", "NTE*ADD*X~GE*1*101")];

        static List<string> Bad(List<string> body)
        {
            body.Replace("NM1*85", "NM1*85*2*ACME MEDICAL GROUP*****XX*1234567890");
            return body;
        }
    }

    // ── 4. Finding caps ──────────────────────────────────────────────

    private static string ManyBadLines(int lines)
    {
        var body = ProfessionalBody();
        body.RemoveRange(16, body.Count - 16); // drop the two sample lines
        body.Replace("CLM*", $"CLM*PCN0001*{lines * 10}.00***11:B:1*Y*A*Y*Y");
        for (var i = 1; i <= lines; i++)
        {
            body.Add($"LX*{i}");
            body.Add("SV1*HC:99213*10.00*UN*1***1"); // no DTP*472 → one L4 error each
        }
        return Wrap(ProfessionalVersion, body);
    }

    [Fact]
    public void PerSetCap_StopsCollectingAndAddsTooManyFinding()
    {
        var result = Validate(ManyBadLines(40), new Snip837ValidationOptions { MaxFindingsPerTransactionSet = 10 });

        var ts = Assert.Single(result.TransactionSets);
        Assert.Equal(11, ts.Issues.Count); // 10 + the marker
        Assert.Single(ts.Issues, i => i.RuleId == "L1-TOO-MANY-FINDINGS");
        Assert.Equal(30, ts.SuppressedFindings);
        Assert.Equal("R", ts.AcknowledgmentCode);
    }

    [Fact]
    public void PerFileCap_AppliesAcrossSetsAndKeepsRejections()
    {
        var bad = ProfessionalBody("PCN0002");
        bad.Replace("NM1*85", "NM1*85*2*ACME MEDICAL GROUP*****XX*1234567890");
        bad.Replace("N3*100", "N3*PO BOX 9");
        bad.Replace("CLM*", "CLM*PCN0002*999.00***11:B:1*Y*A*Y*Y");
        var result = Validate(
            Wrap(ProfessionalVersion, bad, ProfessionalBody("PCN0003"), bad),
            new Snip837ValidationOptions { MaxFindingsPerFile = 2 });

        Assert.True(result.FindingsTruncated);
        Assert.Equal(2, result.TransactionSets.Sum(t => t.Issues.Count));
        Assert.Single(result.EnvelopeIssues, i => i.RuleId == "L1-TOO-MANY-FINDINGS");
        // The last set's findings were all dropped, yet it is still rejected.
        Assert.Equal(["R", "A", "R"], result.TransactionSets.Select(t => t.AcknowledgmentCode));
        AssertAckMatchesAcceptance(result);
    }

    [Fact]
    public void Cap_SuppressedErrorStillRejectsAfterKeptWarnings()
    {
        var result = Validate(
            Professional(b =>
            {
                b.Replace("BHT*", "BHT*0022*00*BATCH0001*20260115*1200*CH");                  // L2 (warn)
                b.Replace("NM1*85", "NM1*85*2*ACME MEDICAL GROUP*****XX*1234567890");          // L4 (reject)
            }),
            new Snip837ValidationOptions { Level2 = SnipAction.Warn, MaxFindingsPerTransactionSet = 1 });

        var ts = Assert.Single(result.TransactionSets);
        Assert.DoesNotContain(ts.Issues, i => i.Severity == SnipSeverity.Error);
        Assert.True(ts.SuppressedError);
        Assert.Equal("R", ts.AcknowledgmentCode);
    }

    [Fact]
    public void Ack999_CapsIk3LoopsPerSet()
    {
        var result = Validate(ManyBadLines(30));
        var segs = Ack(result, new X12999AcknowledgmentBuilder.Options { MaxSegmentErrorsPerTransactionSet = 5 });
        Assert.Equal(5, segs.Count(s => s.Id == "IK3"));
        Assert.Equal("R", segs.Single(s => s.Id == "IK5").Elements[0]);
    }

    // ── 5. ICD-10-CM chapter U ───────────────────────────────────────

    [Theory]
    [InlineData("U071", true)]
    [InlineData("U099", true)]
    [InlineData("J069", true)]
    [InlineData("J06.9", false)]
    [InlineData("1234", false)]
    public void Icd10CmFormat(string code, bool valid) => Assert.Equal(valid, SnipCodeSets.IsIcd10CmFormat(code));

    [Fact]
    public void CovidDiagnosis_IsNotFlagged()
    {
        Assert.Empty(Validate(Professional(b => b.Replace("HI*", "HI*ABK:U071*ABF:R05"))).AllIssues);
    }

    // ── 6. Segment ids are never echoed beyond 3 safe characters ─────

    [Fact]
    public void UnknownLongSegmentId_IsReplacedEverywhere()
    {
        var edi = Professional(b => b.Insert(16, "SMITHJOHN19800101*1"));
        var result = Validate(edi);

        var issue = Assert.Single(result.AllIssues, i => i.RuleId == "L1-SEGMENT-ID");
        Assert.Equal("???", issue.SegmentId);
        Assert.DoesNotContain("SMITH", issue.Message);
        var ik3 = Ack(result).Single(s => s.Id == "IK3");
        Assert.Equal("???", ik3.Elements[0]);
    }

    [Fact]
    public void StraySegmentOutsideTransactionSet_IdIsNotEchoed()
    {
        var result = Validate(Professional().Replace("GE*1*101", "JANEDOE19800101*X~GE*1*101"));

        var issue = Assert.Single(result.AllIssues, i => i.RuleId == "L1-SEGMENT-OUTSIDE-ST");
        Assert.Equal("???", issue.SegmentId);
        Assert.DoesNotContain("JANE", issue.Message);
    }

    [Theory]
    [InlineData("NM1", "NM1")]
    [InlineData("ZZZ", "ZZZ")]
    [InlineData("SMITHJ", "???")]
    [InlineData("a1", "???")]
    [InlineData("", "???")]
    public void SafeSegmentId(string id, string expected) => Assert.Equal(expected, X12837SnipValidator.SafeSegmentId(id));

    // ── 7. 999 envelope ──────────────────────────────────────────────

    [Fact]
    public void Ack999_EchoesIsa15AndGs01()
    {
        var test = Ack(Validate(Professional()));
        Assert.Equal("T", test.Single(s => s.Id == "ISA").Elements[14]);

        var prod = Ack(Validate(Professional().Replace("*0*T*:", "*0*P*:")));
        Assert.Equal("P", prod.Single(s => s.Id == "ISA").Elements[14]);

        var hp = Ack(Validate(Professional().Replace("GS*HC*", "GS*HP*")));
        Assert.Equal("AK1*HP*101*005010X222A1", Seg(hp.Single(s => s.Id == "AK1")));
    }

    [Fact]
    public void Ack999_MissingGs08_HasNoTrailingSeparator()
    {
        var segs = Ack(Validate(Professional().Replace("*X*005010X222A1~ST", "*X*~ST")));
        Assert.Equal("AK1*HC*101", Seg(segs.Single(s => s.Id == "AK1")));
    }

    [Fact]
    public void Ack999_EachInterchangeIsAcknowledgedToItsOwnSender()
    {
        var first = Professional();
        var second = Professional().Replace("*ZZ*SUB001         *ZZ*CHO            *", "*ZZ*OTHERSUB       *ZZ*CHO            *")
            .Replace("GS*HC*SUB001*", "GS*HC*OTHERSUB*");
        var result = Validate(first + second);

        Assert.Equal(2, result.Interchanges.Count);
        var isas = Ack(result).Where(s => s.Id == "ISA").ToList();
        Assert.Equal(["SUB001", "OTHERSUB"], isas.Select(s => s.Elements[7].Trim()));
        Assert.Equal(["000000007", "000000008"], isas.Select(s => s.Elements[12]));
    }

    // ── 9. Missing-segment positions and loop scoping ────────────────

    [Fact]
    public void MissingRefF8_PointsAfterLast2300SegmentBeforeRef()
    {
        // CLM at 16, HI at 17: REF*F8 belongs right after CLM (no DTP/PWK/AMT present).
        var result = Validate(Professional(b => b.Replace("CLM*", "CLM*PCN0001*150.00***11:B:7*Y*A*Y*Y")));
        var issue = Assert.Single(result.AllIssues, i => i.RuleId == "L4-REF-F8");
        Assert.Equal(("REF", 17, "2300"), (issue.SegmentId, issue.SegmentPosition, issue.Loop));
    }

    [Fact]
    public void RefF8In2330B_DoesNotSatisfyThe2300Rule()
    {
        var result = Validate(Professional(b =>
        {
            b.Replace("CLM*", "CLM*PCN0001*150.00***11:B:7*Y*A*Y*Y");
            var lx = b.FindIndex(s => s.StartsWith("LX*1"));
            b.InsertRange(lx,
            [
                "SBR*S*18*******CI",
                "OI***Y***Y",
                "NM1*IL*1*TESTPATIENT*ALEX****MI*MEM0001",
                "NM1*PR*2*OTHER PAYER*****PI*OTHER",
                "REF*F8*ORIGCLAIM01",
            ]);
        }));
        Assert.Single(result.AllIssues, i => i.RuleId == "L4-REF-F8");
    }

    [Fact]
    public void MissingLineDtp472_PointsAfterTheServiceSegment()
    {
        // Line 2: LX at 21, SV1 at 22 → DTP*472 expected at 23.
        var result = Validate(Professional(b => b.RemoveAt(b.FindLastIndex(s => s.StartsWith("DTP*472")))));
        var issue = Assert.Single(result.AllIssues, i => i.RuleId == "L4-DTP472");
        Assert.Equal(("DTP", 23, "2400"), (issue.SegmentId, issue.SegmentPosition, issue.Loop));
        Assert.Contains(Ack(result), s => Seg(s) == "IK3*DTP*23*2400*I6");
    }

    [Fact]
    public void PrincipalDiagnosis_MayBeInAnyHiSegment()
    {
        var result = Validate(Institutional(b => b.Replace("HI*ABK:R0789", "HI*ABJ:R0789")));
        Assert.Contains(result.AllIssues, i => i.RuleId == "L2-HI-PRINCIPAL");

        var ok = Validate(Institutional(b =>
        {
            b.Replace("HI*ABK:R0789", "HI*ABJ:R0789");
            b.Insert(b.FindIndex(s => s.StartsWith("HI*")) + 1, "HI*ABK:R0789");
        }));
        Assert.DoesNotContain(ok.AllIssues, i => i.RuleId == "L2-HI-PRINCIPAL");
    }

    [Fact]
    public void ProfessionalClaimLevelDtp472_IsNotUsed()
    {
        var result = Validate(Professional(b => b.Insert(b.FindIndex(s => s.StartsWith("HI*")), "DTP*472*D8*20260110")));
        Assert.Equal("I4", Assert.Single(result.AllIssues, i => i.RuleId == "L2-2300-DTP472").SegmentErrorCode);
    }

    // ── 10. Group codes follow the Level 1 setting ───────────────────

    [Fact]
    public void GroupErrors_Level1Warn_ReportedButNotRejected()
    {
        var result = Validate(Professional().Replace("GE*1*101", "GE*2*101"),
            new Snip837ValidationOptions { Level1 = SnipAction.Warn });

        var group = Assert.Single(result.FunctionalGroups);
        Assert.Empty(group.GroupErrorCodes);
        Assert.Equal(["5"], group.GroupWarningCodes);
        Assert.Equal("E", group.AcknowledgmentCode);
        Assert.Single(result.AcceptedTransactionSets);
        Assert.Equal("AK9*E*2*1*1*5", Seg(Ack(result).Single(s => s.Id == "AK9")));
    }

    [Fact]
    public void GroupErrors_Level1Off_NotEmitted()
    {
        var result = Validate(Professional().Replace("GE*1*101", "GE*2*101").Replace("GS*HC*", "GS*HP*"),
            new Snip837ValidationOptions { Level1 = SnipAction.Off });

        var group = Assert.Single(result.FunctionalGroups);
        Assert.Empty(group.GroupErrorCodes);
        Assert.Empty(group.GroupWarningCodes);
        Assert.Empty(result.EnvelopeIssues);
        Assert.Equal("A", result.AcknowledgmentCode);
    }
}
