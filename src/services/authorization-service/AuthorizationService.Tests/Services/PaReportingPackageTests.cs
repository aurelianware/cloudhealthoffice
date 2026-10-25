using AuthorizationService.Models;
using AuthorizationService.Repositories;
using System.ComponentModel.DataAnnotations;

namespace AuthorizationService.Tests.Services;

public class PaReportingPackageTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 18, 0, 0, DateTimeKind.Utc);
    private static FreezePaReportingPackage Command() => new()
    {
        RequestKey = "key", PlanId = "plan", PlanScopeReference = "mapping:1", Year = 2025, TimeZone = "UTC",
        LineOfBusiness = LineOfBusiness.Medicaid, OutcomeCutoff = Now.AddDays(-1), KnowledgeCutoff = Now.AddDays(-1),
    };
    [Fact]
    public void EquivalentOffsetInstants_HaveIdenticalCanonicalScope()
    {
        var one = Command(); var two = Command();
        two.OutcomeCutoff = two.OutcomeCutoff.ToOffset(TimeSpan.FromHours(-7));
        two.KnowledgeCutoff = two.KnowledgeCutoff.ToOffset(TimeSpan.FromHours(5));
        PaReportingPackageHash.Of(PaReportingPackageRepositoryMongo.Validate(one, Now))
            .Should().Be(PaReportingPackageHash.Of(PaReportingPackageRepositoryMongo.Validate(two, Now)));
    }
    [Theory]
    [InlineData("future")]
    [InlineData("missing-cutoff")]
    [InlineData("lob")]
    [InlineData("year")]
    public void InvalidScopeCannotBecomeAFrozenPackage(string error)
    {
        var command = Command();
        if (error == "future") command.KnowledgeCutoff = Now.AddDays(1);
        if (error == "missing-cutoff") command.OutcomeCutoff = default;
        if (error == "lob") command.LineOfBusiness = LineOfBusiness.Unknown;
        if (error == "year") command.Year = 0;
        ((Action)(() => PaReportingPackageRepositoryMongo.Validate(command, Now))).Should().Throw<ArgumentException>();
    }
    [Theory]
    [InlineData("")]
    [InlineData("narrative with spaces")]
    [InlineData("https://example.org/phi?token=secret")]
    public void EvidenceReferenceMustBeAnOpaqueKey(string value)
    {
        var command = Command(); command.PlanScopeReference = value;
        ((Action)(() => PaReportingPackageRepositoryMongo.Validate(command, Now))).Should().Throw<ValidationException>();
    }
    [Fact]
    public void FrozenOutputPreservesUnknownHistoricalLabels_InsteadOfRecreatingThemFromCurrentDto()
    {
        var package = new PaReportingPackage { ReportJson = "{\"HistoricalLabel\":\"original\",\"PublicationReady\":false}" };
        package.FrozenReport.GetProperty("HistoricalLabel").GetString().Should().Be("original");
        var original = PaReportingPackageHash.Header(package);
        package.ReportJson = package.ReportJson.Replace("original", "changed");
        PaReportingPackageHash.Header(package).Should().NotBe(original);
    }

    [Fact]
    public void InputDigestIsOrdinalOrdered_AndHeaderHashChangesWithScopeOrResults()
    {
        var rows = new[] { new PaReportingPackageInput { Ordinal = 1, InputHash = "BBBB" }, new PaReportingPackageInput { Ordinal = 0, InputHash = "AAAA" } };
        PaReportingPackageHash.Inputs(rows).Should().Be(PaReportingPackageHash.Inputs(rows.Reverse()));
        var package = new PaReportingPackage(); var original = PaReportingPackageHash.Header(package);
        var changed = package.Report; changed.Standard.Requests = 1;
        package.ReportJson = System.Text.Encoding.UTF8.GetString(PaReportingPackageHash.Bytes(changed));
        PaReportingPackageHash.Header(package).Should().NotBe(original);
        PaReportingPackageHash.Identity("a", "bc").Should().NotBe(PaReportingPackageHash.Identity("ab", "c"));
    }
}
