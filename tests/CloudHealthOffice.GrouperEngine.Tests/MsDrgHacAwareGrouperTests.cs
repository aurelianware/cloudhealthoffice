using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Formulas;
using CloudHealthOffice.GrouperEngine.Grouping;
using CloudHealthOffice.GrouperEngine.Hac;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// Grouping twice, with the hospital-acquired-condition policy in between.
/// </summary>
public class MsDrgHacAwareGrouperTests
{
    private static readonly IReadOnlySet<string> Nothing =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static InMemoryFormulaSource Table() =>
        new(
            "V440",
            [
                Row(1, 177, "ANY:pneumonia & MCC"),
                Row(2, 178, "ANY:pneumonia & CC"),
                Row(3, 179, "ANY:pneumonia"),
            ]);

    private static MsDrgFormulaRow Row(int rank, int drg, string formula) =>
        new(
            Mdc: 4,
            Rank: rank,
            Drg: drg,
            BaseDrg: 177,
            Partition: DrgTablePartition.Medical,
            ReRouteMdc: 0,
            FormulaText: formula,
            Formula: DrgFormulaParser.Parse(formula),
            SeveritySuppressionOperands: [string.Empty],
            DrgSeverity: 0);

    private static InMemoryHacSource Hacs() =>
        new(
            "V440",
            new Dictionary<string, IReadOnlyList<MsDrgHacEntry>>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["S1"] = [new MsDrgHacEntry(5, "HAC05")],
            },
            new Dictionary<int, IReadOnlyList<DrgFormulaExpression>>
            {
                [5] = [DrgFormulaParser.Parse("SDX:HAC05")],
            });

    private static MsDrgHacStay Stay(
        PresentOnAdmission poa,
        HospitalPoaReporting reporting = HospitalPoaReporting.NonExempt) =>
        new(
            new AttributeMask(
                new[] { "PDX:pneumonia", "SDX:HAC05" }.Select(OperandName.Parse)),
            new ClaimCodes(
                [
                    ClaimDiagnosis.Create("PRIN1", 4, ComplicationLevel.None, ["pneumonia"]),

                    // The only thing on this claim carrying severity is the
                    // condition itself, so the policy decides the DRG.
                    ClaimDiagnosis.Create("S1", 4, ComplicationLevel.Mcc, ["HAC05"]),
                ],
                []),
            Nothing,
            PatientDied: false,
            [new MsDrgHacSecondary("S1", poa)],
            reporting);

    /// <summary>
    /// A condition the hospital caused drops the stay out of the MCC DRG and
    /// into the one with no complication. This is the payment consequence the
    /// two passes exist for, and it is why neither pass alone is the answer.
    /// </summary>
    [Fact]
    public void A_condition_the_hospital_caused_drops_the_stay_to_a_lower_drg()
    {
        var result = MsDrgHacAwareGrouper.Group(
            Table(), Hacs(), Stay(PresentOnAdmission.No));

        Assert.Equal(177, result.BeforeHacPolicy.Drg);
        Assert.Equal(ComplicationLevel.Mcc, result.BeforeHacPolicy.Severity);

        Assert.Equal(179, result.AfterHacPolicy.Drg);
        Assert.Equal(ComplicationLevel.None, result.AfterHacPolicy.Severity);

        Assert.True(result.PolicyChangedTheDrg);
        Assert.Contains("S1", result.Hac.RemovedFromSeverity);
    }

    /// <summary>
    /// The same stay, with the condition present on admission, keeps the MCC
    /// DRG. Both passes agree, which is what happens on the great majority of
    /// claims.
    /// </summary>
    [Fact]
    public void A_condition_present_on_admission_leaves_the_drg_alone()
    {
        var result = MsDrgHacAwareGrouper.Group(
            Table(), Hacs(), Stay(PresentOnAdmission.Yes));

        Assert.Equal(177, result.BeforeHacPolicy.Drg);
        Assert.Equal(177, result.AfterHacPolicy.Drg);
        Assert.False(result.PolicyChangedTheDrg);
    }

    /// <summary>
    /// An exempt facility keeps the MCC DRG even where the condition would
    /// otherwise have been met: without POA reporting, nothing can be shown to
    /// have been acquired in hospital.
    /// </summary>
    [Fact]
    public void An_exempt_facility_keeps_the_drg_it_grouped_to()
    {
        var result = MsDrgHacAwareGrouper.Group(
            Table(),
            Hacs(),
            Stay(PresentOnAdmission.No, HospitalPoaReporting.Exempt));

        Assert.Equal(177, result.AfterHacPolicy.Drg);
        Assert.Empty(result.Hac.RemovedFromSeverity);
    }

    /// <summary>
    /// A POA indicator that cannot be read refuses the claim, and the result
    /// still carries the DRG the stay grouped to before the policy ran —
    /// which is what tells a biller what the claim is worth once the indicator
    /// is corrected.
    /// </summary>
    [Fact]
    public void An_unreadable_indicator_refuses_the_claim_and_keeps_the_first_answer()
    {
        var result = MsDrgHacAwareGrouper.Group(
            Table(), Hacs(), Stay(PresentOnAdmission.Blank));

        Assert.Equal(177, result.BeforeHacPolicy.Drg);
        Assert.False(result.AfterHacPolicy.Groupable);
        Assert.Equal(MsDrgHacProcessor.MissingOnePoa, result.AfterHacPolicy.ReturnCode);
    }

    /// <summary>
    /// POA indicators that do not line up with the claim's secondary diagnoses
    /// are refused rather than paired up by position. A misaligned indicator
    /// decides the wrong condition, and the DRG it produces looks entirely
    /// ordinary.
    /// </summary>
    [Fact]
    public void Indicators_that_do_not_line_up_with_the_codes_are_refused()
    {
        var stay = Stay(PresentOnAdmission.No);

        var tooFew = stay with { Secondaries = [] };
        Assert.Contains(
            "POA indicators",
            Assert.Throws<ArgumentException>(
                () => MsDrgHacAwareGrouper.Group(Table(), Hacs(), tooFew)).Message);

        var wrongCode = stay with
        {
            Secondaries = [new MsDrgHacSecondary("S9", PresentOnAdmission.No)],
        };

        Assert.Contains(
            "secondary diagnosis 1 is 'S1'",
            Assert.Throws<ArgumentException>(
                () => MsDrgHacAwareGrouper.Group(Table(), Hacs(), wrongCode)).Message);
    }
}
