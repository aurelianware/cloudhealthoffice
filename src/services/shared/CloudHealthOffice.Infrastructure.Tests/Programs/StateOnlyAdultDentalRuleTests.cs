using CloudHealthOffice.Infrastructure.Programs.MediCal;
using Xunit;

namespace CloudHealthOffice.Infrastructure.Tests.Programs;

/// <summary>DHCS APL 26-009 (SB 164): adults 19+ not eligible for federally funded full-scope Medi-Cal lose non-emergency dental from July 1, 2027.</summary>
public class StateOnlyAdultDentalRuleTests
{
    // Synthetic aid codes: the plan sets the real ones from DHCS's aid code master chart.
    private static readonly StateOnlyAdultDentalOptions EmergencyOnly = new()
    {
        Mode = StateOnlyAdultDentalMode.EmergencyOnly,
        StateOnlyAidCodes = ["S1", "S2", "SP", "SF"],
        ExceptionAidCodes = ["SP", "SF"],
        EmergencyProcedureCodes = ["D0140", "D9110", "D7140"],
    };

    private static readonly StateOnlyAdultDentalOptions CarveOut = new()
    {
        Mode = StateOnlyAdultDentalMode.CarveOut,
        StateOnlyAidCodes = ["S1", "S2", "SP", "SF"],
    };

    private static readonly DateTime Adult = new(1990, 5, 20);
    private static readonly DateTime July1 = new(2027, 7, 1);

    private static AidCodeSpan[] Aid(string code) => [new AidCodeSpan(code, new DateTime(2024, 1, 1))];

    [Theory]
    [InlineData("2008-07-01", "2027-07-01")] // 19 on July 1, 2027: the 1st, so that month
    [InlineData("2008-07-02", "2027-08-01")] // 19 on July 2: the next month
    [InlineData("2008-08-01", "2027-08-01")] // 19 on Aug 1: Aug 1
    [InlineData("2008-08-15", "2027-09-01")]
    [InlineData("2008-01-31", "2027-07-01")] // already 19 before the date: the date
    [InlineData("1990-05-20", "2027-07-01")]
    [InlineData("2012-02-29", "2031-03-01")] // leap-day birth
    [InlineData("2011-12-01", "2030-12-01")]
    public void LossDate_FollowsTheAplsAgingOutRule(string dob, string expected) =>
        Assert.Equal(DateTime.Parse(expected), StateOnlyAdultDentalRule.LossDate(DateTime.Parse(dob)));

    [Fact]
    public void BeforeJuly2027_NothingChanges()
    {
        var d = StateOnlyAdultDentalRule.Evaluate(CarveOut, Adult, new DateTime(2027, 6, 30), Aid("S1"), ["D1110"]);
        Assert.Equal(StateOnlyAdultDentalOutcome.NotApplicable, d.Outcome);
    }

    [Fact]
    public void Off_DoesNothing()
    {
        var d = StateOnlyAdultDentalRule.Evaluate(new StateOnlyAdultDentalOptions(), Adult, July1, Aid("S1"), ["D1110"]);
        Assert.Equal(StateOnlyAdultDentalOutcome.NotApplicable, d.Outcome);
    }

    [Fact]
    public void Under19_StaysCovered_UntilTheAgingOutDate()
    {
        var dob = new DateTime(2008, 9, 10); // loses dental 2027-10-01
        Assert.Equal(StateOnlyAdultDentalOutcome.NotApplicable,
            StateOnlyAdultDentalRule.Evaluate(CarveOut, dob, new DateTime(2027, 9, 30), Aid("S1"), ["D1110"]).Outcome);
        Assert.Equal(StateOnlyAdultDentalOutcome.CarvedOut,
            StateOnlyAdultDentalRule.Evaluate(CarveOut, dob, new DateTime(2027, 10, 1), Aid("S1"), ["D1110"]).Outcome);
    }

    [Fact]
    public void FederallyFundedAidCode_IsNotTouched()
    {
        var d = StateOnlyAdultDentalRule.Evaluate(CarveOut, Adult, July1, Aid("1H"), ["D1110"]);
        Assert.Equal(StateOnlyAdultDentalOutcome.NotApplicable, d.Outcome);
    }

    [Fact]
    public void CarveOut_IncludesEmergenciesAndTheExceptions_TheyGoToDentalFfs()
    {
        var emergency = StateOnlyAdultDentalRule.Evaluate(CarveOut, Adult, July1, Aid("S1"), ["D9110"]);
        var pregnant = StateOnlyAdultDentalRule.Evaluate(CarveOut, Adult, July1, Aid("SP"), ["D1110"]);

        Assert.Equal(StateOnlyAdultDentalOutcome.CarvedOut, emergency.Outcome);
        Assert.Equal(StateOnlyAdultDentalOutcome.CarvedOut, pregnant.Outcome);
        Assert.Contains("Medi-Cal Dental fee-for-service", emergency.Reason);
        Assert.Equal(July1, emergency.LossDate);
    }

    [Fact]
    public void EmergencyOnly_EmergencyCodesAreCovered_OthersAreNamed()
    {
        var covered = StateOnlyAdultDentalRule.Evaluate(EmergencyOnly, Adult, July1, Aid("S1"), ["D0140", "d9110"]);
        var mixed = StateOnlyAdultDentalRule.Evaluate(EmergencyOnly, Adult, July1, Aid("S1"), ["D0140", "D1110", "D2740", "D1110"]);

        Assert.Equal(StateOnlyAdultDentalOutcome.EmergencyCovered, covered.Outcome);
        Assert.Equal(StateOnlyAdultDentalOutcome.NonEmergencyProcedures, mixed.Outcome);
        Assert.Equal(["D1110", "D2740"], mixed.NonEmergencyProcedureCodes!);
    }

    [Theory]
    [InlineData("SP")] // pregnancy, including one year postpartum
    [InlineData("SF")] // foster youth / former foster youth
    public void EmergencyOnly_PregnancyAndFosterAidCodes_KeepFullDental(string code)
    {
        var d = StateOnlyAdultDentalRule.Evaluate(EmergencyOnly, Adult, July1, Aid(code), ["D2740"]);
        Assert.Equal(StateOnlyAdultDentalOutcome.Exception, d.Outcome);
    }

    [Fact]
    public void AidCodeSpans_AreReadOnTheDateOfService()
    {
        var spans = new[]
        {
            new AidCodeSpan("S1", new DateTime(2024, 1, 1), new DateTime(2027, 7, 31)),
            new AidCodeSpan("1H", new DateTime(2027, 8, 1)),
        };
        Assert.Equal(StateOnlyAdultDentalOutcome.CarvedOut,
            StateOnlyAdultDentalRule.Evaluate(CarveOut, Adult, new DateTime(2027, 7, 31), spans, ["D1110"]).Outcome);
        Assert.Equal(StateOnlyAdultDentalOutcome.NotApplicable,
            StateOnlyAdultDentalRule.Evaluate(CarveOut, Adult, new DateTime(2027, 8, 1), spans, ["D1110"]).Outcome);
    }

    [Fact]
    public void NoAidCodeOrNoBirthDate_IsUnknown_NeverCovered()
    {
        Assert.Equal(StateOnlyAdultDentalOutcome.Unknown,
            StateOnlyAdultDentalRule.Evaluate(CarveOut, Adult, July1, [], ["D1110"]).Outcome);
        Assert.Equal(StateOnlyAdultDentalOutcome.Unknown,
            StateOnlyAdultDentalRule.Evaluate(CarveOut, null, July1, Aid("S1"), ["D1110"]).Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\"off\"")]
    [InlineData("null")]
    public void Parse_OffForms(string? json) =>
        Assert.Equal(StateOnlyAdultDentalMode.Off, StateOnlyAdultDentalOptions.Parse(json).Mode);

    [Fact]
    public void Parse_ReadsTheSettingShape()
    {
        var o = StateOnlyAdultDentalOptions.Parse(
            """{"mode":"CarveOut","stateOnlyAidCodes":["S1"],"exceptionAidCodes":["SP"],"emergencyProcedureCodes":["D9110"]}""");
        Assert.Equal(StateOnlyAdultDentalMode.CarveOut, o.Mode);
        Assert.Equal(["S1"], o.StateOnlyAidCodes);
        Assert.Equal(["SP"], o.ExceptionAidCodes);
        Assert.Equal(["D9110"], o.EmergencyProcedureCodes);
    }

    [Fact]
    public void Parse_BrokenJson_Throws_SoTheRuleIsNotSilentlyOff() =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => StateOnlyAdultDentalOptions.Parse("""{"mode":"Sometimes"}"""));
}
