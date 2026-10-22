using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Formulas;
using CloudHealthOffice.GrouperEngine.Mask;
using CloudHealthOffice.GrouperEngine.Models;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// What a claim contributes to its mask. The cases here are the ones CMS's own
/// mask builder singles out — the operands no individual code carries.
/// </summary>
public class MsDrgMaskBuilderTests
{
    private const string Version = "V440";

    private static readonly DateOnly Discharge = new(2026, 3, 15);

    private static MsDrgMaskBuilder BuilderWith(
        IEnumerable<KeyValuePair<string, DiagnosisCodeAttributes>>? diagnoses = null,
        IEnumerable<KeyValuePair<string, ProcedureCodeAttributes>>? procedures = null) =>
        new(new InMemoryCodeAttributeSource(Version, diagnoses, procedures));

    private static MsDrgGroupingRequest Stay(
        string principal,
        IReadOnlyList<string>? secondaries = null,
        IReadOnlyList<string>? procedures = null,
        string? dischargeStatus = "01",
        string? sex = "F") =>
        new()
        {
            PrincipalDiagnosis = new GroupingDiagnosis(principal),
            SecondaryDiagnoses = (secondaries ?? [])
                .Select(code => new GroupingDiagnosis(code)).ToList(),
            Procedures = (procedures ?? [])
                .Select(code => new GroupingProcedure(code)).ToList(),
            DischargeDate = Discharge,
            DischargeStatus = dischargeStatus,
            Sex = sex,
        };

    [Fact]
    public void A_diagnosis_enters_the_mask_at_the_position_it_appeared()
    {
        var builder = BuilderWith(diagnoses: new Dictionary<string, DiagnosisCodeAttributes>
        {
            ["J181"] = InMemoryCodeAttributeSource.Diagnosis(4, ComplicationLevel.Mcc, "stcc"),
            ["N179"] = InMemoryCodeAttributeSource.Diagnosis(11, ComplicationLevel.Cc, "renalfail"),
        });

        var mask = builder.Build(Stay("J181", ["N179"]), out var unrecognised);

        Assert.Empty(unrecognised);
        Assert.True(mask.Contains(new OperandName(OperandPosition.PrincipalDiagnosis, "stcc")));
        Assert.False(mask.Contains(new OperandName(OperandPosition.SecondaryDiagnosis, "stcc")));
        Assert.True(mask.Contains(new OperandName(OperandPosition.SecondaryDiagnosis, "renalfail")));
        Assert.False(mask.Contains(new OperandName(OperandPosition.PrincipalDiagnosis, "renalfail")));
    }

    /// <summary>
    /// ORPROC is not in CMS's tables. It is derived, per procedure, from
    /// membership of either operating-room list — so a stay with a non-OR
    /// procedure does not get it.
    /// </summary>
    [Fact]
    public void An_operating_room_procedure_is_derived_not_looked_up()
    {
        var builder = BuilderWith(
            diagnoses: new Dictionary<string, DiagnosisCodeAttributes>
            {
                ["J181"] = InMemoryCodeAttributeSource.Diagnosis(4, ComplicationLevel.Mcc, "stcc"),
            },
            procedures: new Dictionary<string, ProcedureCodeAttributes>
            {
                ["0DTJ4ZZ"] = InMemoryCodeAttributeSource.Procedure("d477", "lapint"),
                ["5A1955Z"] = InMemoryCodeAttributeSource.Procedure("nonor"),
            });

        var orProc = new OperandName(OperandPosition.Unqualified, "ORPROC");

        Assert.True(builder.Build(Stay("J181", procedures: ["0DTJ4ZZ"]), out _).Contains(orProc));
        Assert.False(builder.Build(Stay("J181", procedures: ["5A1955Z"]), out _).Contains(orProc));
    }

    /// <summary>
    /// ALIVE is the absence of a death, not a status of its own. Note which
    /// statuses carry an operand: only three of CMS's thirty-odd do.
    /// </summary>
    [Theory]
    [InlineData("01", true, null)]
    [InlineData("07", true, "AMA")]
    [InlineData("20", false, "DIED")]
    [InlineData("63", true, null)]
    [InlineData("", true, "invalid_dstat")]
    [InlineData("99", true, "invalid_dstat")]
    public void The_discharge_status_contributes_alive_and_at_most_one_operand(
        string status, bool expectAlive, string? expectedOperand)
    {
        var builder = BuilderWith(diagnoses: new Dictionary<string, DiagnosisCodeAttributes>
        {
            ["J181"] = InMemoryCodeAttributeSource.Diagnosis(4, ComplicationLevel.Mcc, "stcc"),
        });

        var mask = builder.Build(Stay("J181", dischargeStatus: status), out _);

        Assert.Equal(
            expectAlive, mask.Contains(new OperandName(OperandPosition.Unqualified, "ALIVE")));

        foreach (var operand in (string[])["AMA", "DIED", "invalid_dstat"])
        {
            Assert.Equal(
                operand == expectedOperand,
                mask.Contains(new OperandName(OperandPosition.Unqualified, operand)));
        }
    }

    /// <summary>
    /// A status of 99 is not in CMS's list, so it reads as invalid — but the
    /// patient is still alive. Worth stating separately because the obvious
    /// implementation (treat anything unrecognised as a death sentinel) gets
    /// this backwards.
    /// </summary>
    [Fact]
    public void An_unrecognised_discharge_status_is_not_a_death()
    {
        var builder = BuilderWith(diagnoses: new Dictionary<string, DiagnosisCodeAttributes>
        {
            ["J181"] = InMemoryCodeAttributeSource.Diagnosis(4, ComplicationLevel.Mcc, "stcc"),
        });

        var mask = builder.Build(Stay("J181", dischargeStatus: "zzz"), out _);

        Assert.True(mask.Contains(new OperandName(OperandPosition.Unqualified, "ALIVE")));
        Assert.True(mask.Contains(new OperandName(OperandPosition.Unqualified, "invalid_dstat")));
    }

    [Theory]
    [InlineData("02", true)]
    [InlineData("05", true)]
    [InlineData("66", true)]
    [InlineData("82", true)]
    [InlineData("85", true)]
    [InlineData("94", true)]
    [InlineData("03", false)]
    [InlineData("62", false)]
    public void A_transfer_to_another_acute_hospital_is_marked(string status, bool expected)
    {
        var builder = BuilderWith(diagnoses: new Dictionary<string, DiagnosisCodeAttributes>
        {
            ["J181"] = InMemoryCodeAttributeSource.Diagnosis(4, ComplicationLevel.Mcc, "stcc"),
        });

        var mask = builder.Build(Stay("J181", dischargeStatus: status), out _);

        Assert.Equal(
            expected, mask.Contains(new OperandName(OperandPosition.Unqualified, "XFRNBA")));
    }

    /// <summary>
    /// ONLY:incident is the one operand that means "all of them". It is why the
    /// ONLY prefix exists, and the reason a grouper that ignored prefixes would
    /// group these stays wrongly.
    /// </summary>
    [Fact]
    public void Only_incident_needs_every_diagnosis_on_the_list()
    {
        var builder = BuilderWith(diagnoses: new Dictionary<string, DiagnosisCodeAttributes>
        {
            ["Z0000"] = InMemoryCodeAttributeSource.Diagnosis(0, ComplicationLevel.None, "incident"),
            ["Z0001"] = InMemoryCodeAttributeSource.Diagnosis(0, ComplicationLevel.None, "incident"),
            ["J181"] = InMemoryCodeAttributeSource.Diagnosis(4, ComplicationLevel.Mcc, "stcc"),
        });

        var only = new OperandName(OperandPosition.Only, "incident");

        Assert.True(builder.Build(Stay("Z0000", ["Z0001"]), out _).Contains(only));
        Assert.False(builder.Build(Stay("Z0000", ["J181"]), out _).Contains(only));
        Assert.False(builder.Build(Stay("J181", ["Z0001"]), out _).Contains(only));
    }

    /// <summary>
    /// An unrecognised secondary diagnosis does not spoil ONLY:incident, since
    /// CMS tests valid codes only — but it is still reported, because an
    /// unrecognised code has to make the grouper refuse rather than guess.
    /// </summary>
    [Fact]
    public void An_unrecognised_secondary_does_not_spoil_only_incident_but_is_reported()
    {
        var builder = BuilderWith(diagnoses: new Dictionary<string, DiagnosisCodeAttributes>
        {
            ["Z0000"] = InMemoryCodeAttributeSource.Diagnosis(0, ComplicationLevel.None, "incident"),
        });

        var mask = builder.Build(Stay("Z0000", ["NOTACODE"]), out var unrecognised);

        Assert.True(mask.Contains(new OperandName(OperandPosition.Only, "incident")));
        Assert.Equal(["NOTACODE"], unrecognised);
    }

    [Fact]
    public void An_unrecognised_principal_diagnosis_becomes_an_operand_of_its_own()
    {
        var mask = BuilderWith().Build(Stay("NOTACODE"), out var unrecognised);

        Assert.Equal(["NOTACODE"], unrecognised);
        Assert.True(mask.Contains(
            new OperandName(OperandPosition.PrincipalDiagnosis, "invalid_pdx")));
    }

    [Theory]
    [InlineData("V5789", true)]
    [InlineData("W891XXA", true)]
    [InlineData("X58XXXA", true)]
    [InlineData("Y92230", true)]
    [InlineData("w891xxa", true)]
    [InlineData("J181", false)]
    public void An_external_cause_principal_diagnosis_is_marked(string code, bool expected)
    {
        var builder = BuilderWith(diagnoses: new Dictionary<string, DiagnosisCodeAttributes>
        {
            [code] = InMemoryCodeAttributeSource.Diagnosis(0, ComplicationLevel.None, "strauma"),
        });

        var mask = builder.Build(Stay(code), out _);

        Assert.Equal(
            expected,
            mask.Contains(new OperandName(
                OperandPosition.PrincipalDiagnosis, "pdx_ecode")));
    }

    /// <summary>
    /// The sex edit, and the half of it that reads like a bug. An unknown sex
    /// against a sex-specific principal diagnosis raises INVALID_SEX; a stated
    /// sex that the code does not allow does not, because CMS falls the MDC
    /// back and groups the stay instead.
    /// </summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("U", true)]
    [InlineData("F", false)]
    [InlineData("M", false)]
    public void An_unknown_sex_against_a_sex_specific_diagnosis_is_an_edit(
        string? sex, bool expected)
    {
        var builder = BuilderWith(diagnoses: new Dictionary<string, DiagnosisCodeAttributes>
        {
            ["N4ary"] = new(
                0, ComplicationLevel.None, [], ["dxc1402"], [],
                new Dictionary<int, int> { [2] = 13 }),
        });

        var mask = builder.Build(Stay("N4ary", sex: sex), out _);

        Assert.Equal(
            expected, mask.Contains(new OperandName(OperandPosition.Unqualified, "invalid_sex")));
    }

    [Fact]
    public void A_diagnosis_that_is_not_sex_specific_never_raises_the_sex_edit()
    {
        var builder = BuilderWith(diagnoses: new Dictionary<string, DiagnosisCodeAttributes>
        {
            ["J181"] = InMemoryCodeAttributeSource.Diagnosis(4, ComplicationLevel.Mcc, "stcc"),
        });

        var mask = builder.Build(Stay("J181", sex: null), out _);

        Assert.False(mask.Contains(new OperandName(OperandPosition.Unqualified, "invalid_sex")));
    }

    [Fact]
    public void Every_claim_carries_anydx()
    {
        var mask = BuilderWith().Build(Stay("NOTACODE"), out _);

        Assert.True(mask.Contains(new OperandName(OperandPosition.Unqualified, "ANYDX")));
    }

    /// <summary>
    /// The gaps are declared rather than left to be discovered. A mask that
    /// silently lacks an operand produces a wrong DRG, not an error, so the
    /// list is a declaration and this test keeps it honest: the list is empty
    /// now, and the next gap belongs on it rather than in someone's memory.
    /// </summary>
    [Fact]
    public void The_remaining_gaps_are_declared()
    {
        Assert.Empty(MsDrgMaskBuilder.NotYetBuilt);
    }

    /// <summary>
    /// The builder hands back the claim's codes as well as its mask, because
    /// eleven V44 operands read the codes and not the mask. The principal
    /// diagnosis comes first, matching CMS's own evaluation context, which is
    /// what <c>MDC_20_FALL_THRU</c> depends on.
    /// </summary>
    [Fact]
    public void The_builder_returns_the_claims_codes_principal_first()
    {
        var builder = BuilderWith(
            diagnoses: new Dictionary<string, DiagnosisCodeAttributes>
            {
                ["S0690AA"] = InMemoryCodeAttributeSource.DiagnosisWithOperands(
                    24, ComplicationLevel.Mcc, "sthead"),
                ["S2740XA"] = InMemoryCodeAttributeSource.DiagnosisWithOperands(
                    24, ComplicationLevel.Cc, "stchest"),
            },
            procedures: new Dictionary<string, ProcedureCodeAttributes>
            {
                ["0SRB0JZ"] = InMemoryCodeAttributeSource.Procedure("righthip"),
            });

        var built = builder.BuildClaim(Stay("S0690AA", ["S2740XA"], ["0SRB0JZ"]));

        Assert.Empty(built.UnrecognisedCodes);
        Assert.Equal(
            ["S0690AA", "S2740XA"],
            built.Codes.Diagnoses.Select(diagnosis => diagnosis.Code));
        Assert.Equal(
            ["0SRB0JZ"],
            built.Codes.Procedures.Select(procedure => procedure.Code));
        Assert.True(built.Codes.Diagnoses[0].HasOperand("sthead"));
        Assert.True(built.Codes.Procedures[0].HasOperand("righthip"));

        // And the operands those codes carry do satisfy MULTST.
        Assert.True(DrgFormulaParser.Parse("MULTST").Evaluate(
            new DrgFormulaEvaluation(
                built.Mask, Mdc: 24, ApplySuppression: true, Claim: built.Codes)));
    }

    /// <summary>
    /// An unrecognised code keeps its place in the claim's code list, so that
    /// the operands that read positions do not see the list shift under them.
    /// It carries no operands and an MDC of -1, which is why an unrecognised
    /// code is reported as a reason to refuse rather than grouped around.
    /// </summary>
    [Fact]
    public void An_unrecognised_code_keeps_its_place_in_the_claims_codes()
    {
        var builder = BuilderWith(
            diagnoses: new Dictionary<string, DiagnosisCodeAttributes>
            {
                ["S2740XA"] = InMemoryCodeAttributeSource.DiagnosisWithOperands(
                    24, ComplicationLevel.Cc, "stchest"),
            });

        var built = builder.BuildClaim(Stay("NOPE1", ["S2740XA"]));

        Assert.Equal(["NOPE1"], built.UnrecognisedCodes);
        Assert.Equal(
            ["NOPE1", "S2740XA"],
            built.Codes.Diagnoses.Select(diagnosis => diagnosis.Code));
        Assert.Equal(-1, built.Codes.Diagnoses[0].Mdc);
        Assert.Empty(built.Codes.Diagnoses[0].Operands);
    }
}
