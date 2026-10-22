using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Formulas;
using CloudHealthOffice.GrouperEngine.Hac;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// Deciding which of a stay's secondary diagnoses the hospital caused.
///
/// <para>
/// The HAC numbers, list names and formulas here are V44's own; the codes are
/// synthetic. Three of these tests assert behaviour that is CMS's rather than
/// the policy's, and say so where they do: parity is the requirement, because
/// firing a condition the payer does not takes severity off a stay the payer
/// pays in full.
/// </para>
/// </summary>
public class MsDrgHacProcessorTests
{
    private static readonly IReadOnlySet<string> Nothing =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static InMemoryHacSource Tables(
        IReadOnlyDictionary<string, MsDrgHacEntry[]> codes,
        params (int Number, string Formula)[] formulas) =>
        new(
            "V440",
            codes.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<MsDrgHacEntry>)pair.Value,
                StringComparer.OrdinalIgnoreCase),
            formulas.ToDictionary(
                row => row.Number,
                row => (IReadOnlyList<DrgFormulaExpression>)
                    [DrgFormulaParser.Parse(row.Formula)]));

    private static ClaimCodes Claim(
        IEnumerable<string> secondaries, params string[] procedureOperands) =>
        new(
            [
                ClaimDiagnosis.Create("PRIN1", 5, ComplicationLevel.None, []),
                .. secondaries.Select(code =>
                    ClaimDiagnosis.Create(code, 5, ComplicationLevel.Mcc, [])),
            ],
            procedureOperands.Length == 0
                ? []
                : [ClaimProcedure.Create("0JH00ZZ", procedureOperands)]);

    private static MsDrgHacDetermination Determine(
        IMsDrgHacSource tables,
        IReadOnlyList<MsDrgHacSecondary> secondaries,
        IEnumerable<string> mask,
        HospitalPoaReporting reporting = HospitalPoaReporting.NonExempt,
        IReadOnlySet<string>? excluded = null,
        params string[] procedureOperands) =>
        MsDrgHacProcessor.Determine(
            secondaries,
            Claim(secondaries.Select(secondary => secondary.Code), procedureOperands),
            new AttributeMask(mask.Select(OperandName.Parse)),
            excluded ?? Nothing,
            tables,
            reporting);

    /// <summary>
    /// A condition the patient arrived with is not one the hospital caused, so
    /// it keeps counting towards severity. <c>W</c> — clinically undetermined
    /// — counts as arrived with.
    /// </summary>
    [Theory]
    [InlineData(PresentOnAdmission.Yes)]
    [InlineData(PresentOnAdmission.ClinicallyUndetermined)]
    public void A_condition_present_on_admission_is_not_hospital_acquired(
        PresentOnAdmission poa)
    {
        var tables = Tables(
            new Dictionary<string, MsDrgHacEntry[]> { ["S1"] = [new(5, "HAC05")] },
            (5, "SDX:HAC05"));

        var result = Determine(
            tables, [new MsDrgHacSecondary("S1", poa)], ["SDX:HAC05"]);

        Assert.Equal(
            MsDrgHacStatus.CriteriaNotMet, result.Outcomes[0].Conditions[0].Status);
        Assert.Empty(result.RemovedFromSeverity);
        Assert.Null(result.ReturnCode);
    }

    /// <summary>
    /// A condition that was not present on admission and whose formula is
    /// satisfied stops counting towards severity. This is the whole point of
    /// the policy and the only outcome here that changes what a stay is paid.
    /// </summary>
    [Fact]
    public void A_condition_the_hospital_caused_leaves_the_severity_count()
    {
        var tables = Tables(
            new Dictionary<string, MsDrgHacEntry[]> { ["S1"] = [new(5, "HAC05")] },
            (5, "SDX:HAC05"));

        var result = Determine(
            tables,
            [new MsDrgHacSecondary("S1", PresentOnAdmission.No)],
            ["SDX:HAC05"]);

        Assert.Equal(MsDrgHacStatus.CriteriaMet, result.Outcomes[0].Conditions[0].Status);
        Assert.Contains("S1", result.RemovedFromSeverity);
    }

    /// <summary>
    /// A code the principal diagnosis's CC exclusion list already took out of
    /// the severity count is not put through the HAC rules: there is nothing
    /// left to take.
    /// </summary>
    [Fact]
    public void A_code_the_exclusion_list_already_took_out_is_not_reconsidered()
    {
        var tables = Tables(
            new Dictionary<string, MsDrgHacEntry[]> { ["S1"] = [new(5, "HAC05")] },
            (5, "SDX:HAC05"));

        var result = Determine(
            tables,
            [new MsDrgHacSecondary("S1", PresentOnAdmission.No)],
            ["SDX:HAC05"],
            excluded: new HashSet<string>(["S1"], StringComparer.OrdinalIgnoreCase));

        Assert.Equal(
            MsDrgHacStatus.NotApplicableExclusion, result.Outcomes[0].Conditions[0].Status);
        Assert.Empty(result.RemovedFromSeverity);
    }

    /// <summary>
    /// A facility exempt from POA reporting acquires nothing, whatever the
    /// codes say. Nothing leaves the severity count and the claim is not
    /// refused.
    /// </summary>
    [Fact]
    public void An_exempt_facility_acquires_nothing()
    {
        var tables = Tables(
            new Dictionary<string, MsDrgHacEntry[]> { ["S1"] = [new(5, "HAC05")] },
            (5, "SDX:HAC05"));

        var result = Determine(
            tables,
            [new MsDrgHacSecondary("S1", PresentOnAdmission.Blank)],
            ["SDX:HAC05"],
            reporting: HospitalPoaReporting.Exempt);

        Assert.Equal(
            MsDrgHacStatus.NotApplicableExempt, result.Outcomes[0].Conditions[0].Status);
        Assert.Empty(result.RemovedFromSeverity);
        Assert.Null(result.ReturnCode);
    }

    /// <summary>
    /// A POA indicator that cannot be read, on a stay carrying a condition the
    /// hospital caused, refuses the claim. All four unreadable values do it.
    /// </summary>
    [Theory]
    [InlineData(PresentOnAdmission.Blank)]
    [InlineData(PresentOnAdmission.Invalid)]
    [InlineData(PresentOnAdmission.ExemptE)]
    [InlineData(PresentOnAdmission.ExemptOne)]
    public void An_unreadable_poa_on_the_condition_refuses_the_claim(
        PresentOnAdmission poa)
    {
        var tables = Tables(
            new Dictionary<string, MsDrgHacEntry[]> { ["S1"] = [new(5, "HAC05")] },
            (5, "SDX:HAC05"));

        var result = Determine(tables, [new MsDrgHacSecondary("S1", poa)], ["SDX:HAC05"]);

        Assert.Equal(MsDrgHacProcessor.MissingOnePoa, result.ReturnCode);
    }

    /// <summary>
    /// CMS's reach is wider than the condition: an unreadable POA on <i>any</i>
    /// HAC-list secondary refuses the claim, even one whose own condition was
    /// not met. Its <c>hasInvalidPoaOnClaim</c> flag is set while walking every
    /// HAC-bearing code and then applied to every condition.
    /// </summary>
    [Fact]
    public void An_unreadable_poa_on_an_unrelated_hac_code_refuses_the_claim_too()
    {
        var tables = Tables(
            new Dictionary<string, MsDrgHacEntry[]>
            {
                ["S1"] = [new(5, "HAC05")],
                ["S2"] = [new(9, "HAC09")],
            },
            (5, "SDX:HAC05"),
            (9, "SDX:HAC09"));

        // S1's condition is met with a legible indicator. S2 is on a HAC list
        // whose formula is not satisfied, and its indicator is blank.
        var result = Determine(
            tables,
            [
                new MsDrgHacSecondary("S1", PresentOnAdmission.No),
                new MsDrgHacSecondary("S2", PresentOnAdmission.Blank),
            ],
            ["SDX:HAC05"]);

        Assert.Equal(MsDrgHacStatus.CriteriaMet, result.Outcomes[0].Conditions[0].Status);
        Assert.Equal(MsDrgHacStatus.CriteriaNotMet, result.Outcomes[1].Conditions[0].Status);
        Assert.Equal(MsDrgHacProcessor.MissingOnePoa, result.ReturnCode);
    }

    /// <summary>
    /// When the facility's POA reporting status is unknown, two secondary
    /// diagnoses are enough to refuse the claim.
    ///
    /// <para>
    /// CMS's condition reads as though it counted indicators that are neither
    /// <c>Y</c> nor <c>W</c>: <c>poa != Y || poa != W</c>. No value fails that,
    /// so it counts every secondary on the claim. Replicated, and named so in
    /// the engine, because the alternative is to implement what CMS looks like
    /// it meant and disagree with it on every such stay.
    /// </para>
    /// </summary>
    [Fact]
    public void An_unknown_reporting_status_refuses_a_claim_with_two_secondaries()
    {
        var tables = Tables(
            new Dictionary<string, MsDrgHacEntry[]> { ["S1"] = [new(5, "HAC05")] },
            (5, "SDX:HAC05"));

        var secondaries = new[]
        {
            new MsDrgHacSecondary("S1", PresentOnAdmission.No),

            // Present on admission, so not a condition at all -- and still
            // counted.
            new MsDrgHacSecondary("S2", PresentOnAdmission.Yes),
        };

        Assert.Equal(2, MsDrgHacProcessor.SecondariesCountedByCmssTautology(secondaries));

        Assert.Equal(
            MsDrgHacProcessor.UnknownStatusMultipleConditions,
            Determine(tables, secondaries, ["SDX:HAC05"],
                reporting: HospitalPoaReporting.Unknown).ReturnCode);
    }

    /// <summary>
    /// With one secondary and an unknown reporting status, the refusal says
    /// which indicator it objected to.
    /// </summary>
    [Theory]
    [InlineData(PresentOnAdmission.No, MsDrgHacProcessor.UnknownStatusPoaNotPresent)]
    [InlineData(PresentOnAdmission.Unknown, MsDrgHacProcessor.UnknownStatusPoaNotPresent)]
    [InlineData(PresentOnAdmission.Blank, MsDrgHacProcessor.UnknownStatusPoaUnreadable)]
    public void An_unknown_reporting_status_names_the_indicator_it_refused(
        PresentOnAdmission poa, int expected)
    {
        var tables = Tables(
            new Dictionary<string, MsDrgHacEntry[]> { ["S1"] = [new(5, "HAC05")] },
            (5, "SDX:HAC05"));

        // A met condition is what opens this branch at all, so the diagnosis
        // has to satisfy HAC 5 whatever its indicator says.
        var result = Determine(
            tables,
            [new MsDrgHacSecondary("S1", poa)],
            ["SDX:HAC05"],
            reporting: HospitalPoaReporting.Unknown);

        Assert.Equal(expected, result.ReturnCode);
    }

    /// <summary>
    /// Six of the 14 conditions need a procedure, and without it the condition
    /// is not met however the diagnosis is coded.
    /// </summary>
    [Fact]
    public void A_condition_that_needs_a_procedure_is_not_met_without_one()
    {
        var tables = Tables(
            new Dictionary<string, MsDrgHacEntry[]> { ["S1"] = [new(8, "HAC08")] },
            (8, "SDX:HAC08 & hac08_proc"));

        Assert.Equal(
            MsDrgHacStatus.CriteriaNotMet,
            Determine(
                tables,
                [new MsDrgHacSecondary("S1", PresentOnAdmission.No)],
                ["SDX:HAC08"])
                .Outcomes[0].Conditions[0].Status);

        var withProcedure = Determine(
            tables,
            [new MsDrgHacSecondary("S1", PresentOnAdmission.No)],
            ["SDX:HAC08", "hac08_proc"],
            procedureOperands: "hac08_proc");

        Assert.Equal(
            MsDrgHacStatus.CriteriaMet, withProcedure.Outcomes[0].Conditions[0].Status);
        Assert.Contains(8, withProcedure.ProcedureConditions);
    }

    /// <summary>
    /// HAC 6's show-list rule: unless some code is on HAC 6 from <i>off</i> the
    /// show list, the show-list entries are not conditions.
    ///
    /// <para>
    /// Unreachable on a real V44 claim, because no code on HAC 6 gets HAC
    /// entries at all — see <c>ImportHac</c>. Asserted anyway, so a fiscal
    /// year that un-gates HAC 6 finds the rule already built rather than
    /// quietly missing.
    /// </para>
    /// </summary>
    [Fact]
    public void A_show_list_condition_alone_is_not_a_condition()
    {
        var tables = Tables(
            new Dictionary<string, MsDrgHacEntry[]> { ["S1"] = [new(6, "hac06_show")] },
            (6, "SDX:hac06_sdx | (SDX:hac06_sdx & SDX:hac06_show)"));

        var result = Determine(
            tables,
            [new MsDrgHacSecondary("S1", PresentOnAdmission.No)],
            ["SDX:hac06_sdx", "SDX:hac06_show"]);

        Assert.Equal(
            MsDrgHacStatus.CriteriaNotMet, result.Outcomes[0].Conditions[0].Status);
        Assert.Empty(result.RemovedFromSeverity);
        Assert.Null(result.ReturnCode);
    }

    /// <summary>
    /// A code on no HAC list has nothing decided about it, and a stay of such
    /// codes is not refused however its indicators read.
    /// </summary>
    [Fact]
    public void A_code_on_no_hac_list_has_nothing_decided_about_it()
    {
        var tables = Tables(
            new Dictionary<string, MsDrgHacEntry[]>(), (5, "SDX:HAC05"));

        var result = Determine(
            tables,
            [new MsDrgHacSecondary("S1", PresentOnAdmission.Invalid)],
            ["SDX:HAC05"]);

        Assert.Empty(result.Outcomes[0].Conditions);
        Assert.Empty(result.RemovedFromSeverity);
        Assert.Null(result.ReturnCode);
    }
}
