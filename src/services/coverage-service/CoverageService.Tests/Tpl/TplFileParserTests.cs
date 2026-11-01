using CoverageService.Models.Tpl;
using CoverageService.Services.Tpl;

namespace CoverageService.Tests.Tpl;

public class TplFileParserTests
{
    private static TplFileLayout Ca => TplLayouts.Find(TplLayouts.CaliforniaOhc)!;
    private static TplFileLayout Tx => TplLayouts.Find(TplLayouts.TexasTpr)!;
    private static TplFileLayout Cho => TplLayouts.Find(TplLayouts.ChoCsv)!;

    [Fact]
    public void California_MapsOhcCodesToScope()
    {
        const string file =
            "CIN,OHC Code,Carrier Code,Carrier Name,Policy Number,OHC Start Date,OHC End Date\n" +
            "91234567A,K,K001,Kaiser Permanente,P1,20260101,\n" +
            "91234568B,L,D002,Delta Dental,P2,20260101,20261231\n" +
            "91234569C,A,X003,Any Carrier,P3,2026-02-01,\n" +
            "91234570D,F,M004,Medicare Advantage Co,P4,01/01/2026,99991231\n";

        var result = TplFileParser.Parse(file, Ca);

        result.FileError.Should().BeNull();
        result.Rejected.Should().BeEmpty();
        result.Rows.Should().HaveCount(4);
        result.Rows[0].Scope.Should().Be(TplScope.Comprehensive);
        result.Rows[1].Scope.Should().Be(TplScope.Dental);
        result.Rows[1].EndDate.Should().Be(new DateTime(2026, 12, 31));
        result.Rows[2].PayAndChaseOnly.Should().BeTrue();
        result.Rows[3].Scope.Should().HaveFlag(TplScope.MedicarePartC).And.HaveFlag(TplScope.Hospital);
        result.Rows[3].EndDate.Should().BeNull("99991231 is an open end date");
    }

    [Fact]
    public void California_FlagCodesAreSkipped_AndNoneMeansEnd()
    {
        const string file =
            "CIN,OHC Code,Carrier Name,OHC Start Date\n" +
            "1,G,,20260101\n" +
            "2,N,,20260301\n";

        var result = TplFileParser.Parse(file, Ca);

        result.Skipped.Should().ContainSingle().Which.Reason.Should().Contain("parolee");
        result.Rows.Should().ContainSingle().Which.MeansNoCoverage.Should().BeTrue();
    }

    [Fact]
    public void California_LooseCodesAreReportedForVerification()
    {
        var result = TplFileParser.Parse("CIN,OHC Code,Carrier Name,OHC Start Date\n1,P,Some PPO,20260101\n", Ca);

        result.Rows.Should().ContainSingle();
        result.CodesNeedingVerification.Should().Contain("P");
    }

    [Fact]
    public void Texas_MapsCoverageTypeWords()
    {
        const string file =
            "Medicaid Number\tCarrier Name\tPolicy Number\tCoverage Type\tEffective Date\tTerm Date\n" +
            "500000001\tBlue Cross\tABC123\tMAJOR MEDICAL\t20250601\t\n" +
            "500000002\tTRICARE\tT1\tTRICARE\t20250601\t\n" +
            "500000003\tAcme Indemnity\tC1\tCANCER\t20250601\t\n";
        var layout = Tx;
        layout = TplControllerMergeHelper.WithDelimiter(layout, "\\t");

        var result = TplFileParser.Parse(file, layout);

        result.Rejected.Should().BeEmpty();
        result.Rows.Should().HaveCount(2);
        result.Rows[0].Scope.Should().Be(TplScope.Medical);
        result.Rows[1].Scope.Should().Be(TplScope.Comprehensive);
        result.Skipped.Should().ContainSingle("an indemnity cancer policy pays cash, not services");
    }

    [Fact]
    public void UnknownCode_IsRejectedNotGuessed()
    {
        var result = TplFileParser.Parse("CIN,OHC Code,Carrier Name,OHC Start Date\n1,Z9,Mystery,20260101\n", Ca);

        result.Rows.Should().BeEmpty();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("Z9");
    }

    [Fact]
    public void ChoCsv_ReadsScopeNames()
    {
        const string file =
            "member_id,carrier_name,policy_number,scope,effective_date,end_date,action\n" +
            "M1,Aetna,\"PN,1\",Hospital;Professional,2026-01-01,,A\n" +
            "M2,Aetna,PN2,Medicare Part B,2026-01-01,2026-06-30,T\n" +
            "M3,Aetna,PN3,Hospitl,2026-01-01,,\n";

        var result = TplFileParser.Parse(file, Cho);

        result.Rows.Should().HaveCount(2);
        result.Rows[0].PolicyNumber.Should().Be("PN,1");
        result.Rows[0].Scope.Should().Be(TplScope.Hospital | TplScope.Professional);
        result.Rows[1].Scope.Should().Be(TplScope.MedicarePartB);
        result.Rows[1].Action.Should().Be(TplRecordAction.End);
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("Hospitl");
    }

    [Fact]
    public void MissingRequiredColumn_IsAFileError()
    {
        var result = TplFileParser.Parse("carrier_name,effective_date\nAetna,2026-01-01\n", Cho);

        result.FileError.Should().Contain("MemberId");
    }

    [Fact]
    public void EndBeforeStart_IsRejected()
    {
        var result = TplFileParser.Parse(
            "member_id,carrier_name,scope,effective_date,end_date\nM1,Aetna,Medical,2026-05-01,2026-04-01\n", Cho);

        result.Rejected.Should().ContainSingle().Which.Reason.Should().Contain("before start");
    }

    [Fact]
    public void FixedWidth_ReadsPositions()
    {
        var layout = new TplFileLayout
        {
            Id = "ca-ohc",
            State = "CA",
            Format = TplFileFormat.FixedWidth,
            Fields = new()
            {
                [TplField.MemberId] = new() { Start = 1, Length = 9 },
                [TplField.CoverageCode] = new() { Start = 10, Length = 2 },
                [TplField.CarrierName] = new() { Start = 12, Length = 10 },
                [TplField.EffectiveDate] = new() { Start = 22, Length = 8 },
            },
            Codes = Ca.Codes,
        };
        var line = "91234567A" + "K " + "Kaiser    " + "20260101";

        var result = TplFileParser.Parse(line + "\n", layout);

        result.Rows.Should().ContainSingle();
        result.Rows[0].MemberId.Should().Be("91234567A");
        result.Rows[0].CarrierName.Should().Be("Kaiser");
        result.Rows[0].Scope.Should().Be(TplScope.Comprehensive);
    }

    [Theory]
    [InlineData("Comprehensive", TplScope.Comprehensive)]
    [InlineData("dental + vision", TplScope.Dental | TplScope.Vision)]
    [InlineData("Medicare Part D", TplScope.MedicarePartD | TplScope.Pharmacy)]
    public void TryParseScope_ReadsNames(string text, TplScope expected)
    {
        TplFileParser.TryParseScope(text, out var scope).Should().BeTrue();
        scope.Should().Be(expected);
    }
}

internal static class TplControllerMergeHelper
{
    public static TplFileLayout WithDelimiter(TplFileLayout layout, string delimiter) =>
        CoverageService.Controllers.TplController.Merge(layout, new TplFileLayout
        {
            Delimiter = delimiter,
            Verified = layout.Verified,
            VerificationNote = layout.VerificationNote,
        });
}
