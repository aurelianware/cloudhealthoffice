using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Formulas;
using CloudHealthOffice.GrouperEngine.Severity;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// What a claim's secondary diagnoses are worth in severity.
///
/// <para>
/// Every answer here is CMS's own, printed by the parity oracle's
/// <c>SeverityCheck</c>, which calls CMS's real
/// <c>MsdrgSeverityProcessor.processClaimSeverity</c>. Severity is recomputed
/// for every formula an MDC holds, with that formula's own suppression list
/// applied, so these answers are reached 824 times per claim rather than once.
/// </para>
/// </summary>
public class SeverityTests
{
    private static readonly IReadOnlySet<string> NoExclusions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> NoHacs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static ClaimCodes Claim(params ClaimDiagnosis[] secondaries) =>
        new(
            [ClaimDiagnosis.Create("PRIN1", 4, ComplicationLevel.Mcc, ["stcc"]), .. secondaries],
            []);

    private static ClaimDiagnosis Sdx(
        string code, ComplicationLevel severity, params string[] operands) =>
        ClaimDiagnosis.Create(code, 4, severity, operands);

    private static ComplicationLevel Severity(
        ClaimCodes codes,
        IEnumerable<string>? suppression = null,
        IReadOnlySet<string>? excluded = null,
        bool patientDied = false,
        IReadOnlySet<string>? hacs = null) =>
        MsDrgSeverityProcessor.ForFormula(
            codes,
            excluded ?? NoExclusions,
            patientDied,
            hacs ?? NoHacs,
            suppression ?? []);

    /// <summary>
    /// The plain cases. A claim with nothing to count is CMS's <c>NON_CC</c>,
    /// which this model spells <see cref="ComplicationLevel.None"/>; the
    /// highest counting secondary wins, whichever order the codes arrive in.
    /// </summary>
    [Fact]
    public void The_highest_counting_secondary_decides()
    {
        Assert.Equal(ComplicationLevel.None, Severity(Claim()));
        Assert.Equal(
            ComplicationLevel.None,
            Severity(Claim(Sdx("S1", ComplicationLevel.None, "renalfail"))));
        Assert.Equal(
            ComplicationLevel.Cc,
            Severity(Claim(Sdx("S1", ComplicationLevel.Cc, "renalfail"))));
        Assert.Equal(
            ComplicationLevel.Mcc,
            Severity(Claim(
                Sdx("S1", ComplicationLevel.Cc, "renalfail"),
                Sdx("S2", ComplicationLevel.Mcc, "diabetes"))));
        Assert.Equal(
            ComplicationLevel.Mcc,
            Severity(Claim(
                Sdx("S1", ComplicationLevel.Mcc, "diabetes"),
                Sdx("S2", ComplicationLevel.Cc, "renalfail"))));
    }

    /// <summary>
    /// The principal diagnosis is never its own complication, however severe
    /// its own table row says it is. CMS passes only the secondaries.
    /// </summary>
    [Fact]
    public void The_principal_diagnosis_does_not_count_towards_severity()
    {
        // Claim()'s principal is an MCC and there is nothing else.
        Assert.Equal(ComplicationLevel.None, Severity(Claim()));
    }

    /// <summary>
    /// A formula's suppression list takes its own lists out of the count. The
    /// row here is V44's MDC 0 / DRG 19 pancreas-and-kidney transplant rule,
    /// which suppresses <c>diabetes</c> and <c>renalfail</c> because they are
    /// the reason for the transplant rather than complications of the stay.
    /// </summary>
    [Fact]
    public void A_formulas_suppression_list_removes_its_lists_from_the_count()
    {
        string[] transplant = ["diabetes", "renalfail"];

        Assert.Equal(
            ComplicationLevel.Cc,
            Severity(
                Claim(
                    Sdx("S1", ComplicationLevel.Mcc, "diabetes"),
                    Sdx("S2", ComplicationLevel.Cc, "stcc")),
                transplant));

        Assert.Equal(
            ComplicationLevel.None,
            Severity(
                Claim(
                    Sdx("S1", ComplicationLevel.Mcc, "diabetes"),
                    Sdx("S2", ComplicationLevel.Cc, "renalfail")),
                transplant));

        Assert.Equal(
            ComplicationLevel.Mcc,
            Severity(
                Claim(Sdx("S1", ComplicationLevel.Mcc, "diabetes")),
                ["renalfail"]));
    }

    /// <summary>
    /// CMS's data writes a suppression list of one empty string where a
    /// formula suppresses nothing — 768 of V44's 824 formulas. It matches no
    /// list, so it suppresses nothing, and no special case is needed. Worth a
    /// test because a port that treated an empty operand as a wildcard, or
    /// that skipped the comparison, would silently flatten every claim's
    /// severity.
    /// </summary>
    [Fact]
    public void An_empty_suppression_operand_suppresses_nothing()
    {
        Assert.Equal(
            ComplicationLevel.Mcc,
            Severity(Claim(Sdx("S1", ComplicationLevel.Mcc, "diabetes")), [""]));
    }

    /// <summary>
    /// The suppression operand is matched case-insensitively and against the
    /// code's list name with any position prefix ignored, because CMS's
    /// diagnosis lookup is a walk comparing list names.
    /// </summary>
    [Fact]
    public void A_suppression_operand_is_matched_loosely()
    {
        Assert.Equal(
            ComplicationLevel.None,
            Severity(Claim(Sdx("S1", ComplicationLevel.Mcc, "diabetes")), ["DIABETES"]));

        Assert.Equal(
            ComplicationLevel.None,
            Severity(Claim(Sdx("S1", ComplicationLevel.Mcc, "PDX:diabetes")), ["diabetes"]));
    }

    /// <summary>
    /// A secondary on the principal diagnosis's CC exclusion list says nothing
    /// the principal did not, so it cannot raise the stay's severity.
    /// </summary>
    [Fact]
    public void An_excluded_secondary_does_not_count()
    {
        var excluded = new HashSet<string>(["S1"], StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            ComplicationLevel.Cc,
            Severity(
                Claim(
                    Sdx("S1", ComplicationLevel.Mcc, "diabetes"),
                    Sdx("S2", ComplicationLevel.Cc, "stcc")),
                excluded: excluded));
    }

    /// <summary>
    /// An <c>mccalive</c> code counts only on a stay the patient survived. The
    /// attribute alone does not disqualify it — the discharge status does.
    /// </summary>
    [Fact]
    public void An_mccalive_secondary_counts_only_when_the_patient_lived()
    {
        var claim = Claim(
            Sdx("S1", ComplicationLevel.Mcc, "mccalive"),
            Sdx("S2", ComplicationLevel.Cc, "stcc"));

        Assert.Equal(ComplicationLevel.Mcc, Severity(claim));
        Assert.Equal(ComplicationLevel.Cc, Severity(claim, patientDied: true));
    }

    /// <summary>
    /// A hospital-acquired condition the hospital is not paid for does not
    /// count either. Nothing in this engine decides which codes those are yet,
    /// so this pins the mechanism rather than the policy.
    /// </summary>
    [Fact]
    public void A_hospital_acquired_condition_does_not_count()
    {
        Assert.Equal(
            ComplicationLevel.Cc,
            Severity(
                Claim(
                    Sdx("S1", ComplicationLevel.Mcc, "diabetes"),
                    Sdx("S2", ComplicationLevel.Cc, "stcc")),
                hacs: new HashSet<string>(["S1"], StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// With every secondary removed one way or another, the stay is back to no
    /// complication.
    /// </summary>
    [Fact]
    public void A_claim_whose_secondaries_are_all_removed_has_no_complication()
    {
        Assert.Equal(
            ComplicationLevel.None,
            Severity(
                Claim(
                    Sdx("S1", ComplicationLevel.Mcc, "diabetes"),
                    Sdx("S2", ComplicationLevel.Cc, "mccalive")),
                excluded: new HashSet<string>(["S1"], StringComparer.OrdinalIgnoreCase),
                patientDied: true));
    }

    /// <summary>
    /// The operand the severity enters the mask as. CMS adds the severity
    /// enum's own name, so <c>MCC</c> and <c>CC</c> — which is what the
    /// formulas test — and <c>NON_CC</c>, which no V44 formula tests and which
    /// therefore matches nothing. A port that wrote CMS's <i>other</i>
    /// spelling, <c>NCC</c>, would be introducing a token the data never uses.
    /// </summary>
    [Theory]
    [InlineData(ComplicationLevel.Mcc, "MCC")]
    [InlineData(ComplicationLevel.Cc, "CC")]
    [InlineData(ComplicationLevel.None, "NON_CC")]
    public void The_severity_enters_the_mask_under_cms_own_name(
        ComplicationLevel severity, string operand)
    {
        Assert.Equal(operand, MsDrgSeverityProcessor.Operand(severity).ToString());
    }
}
