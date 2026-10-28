using FhirService.Services;
using FhirService.Services.BulkExport;
using FluentAssertions;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.FhirService.Tests.Services.BulkExport;

/// <summary>Bulk Data <c>_typeFilter</c>, judged with the R4 search parameters' own FHIRPath.</summary>
public sealed class BulkExportTypeFilterTests
{
    private static readonly IReadOnlyList<string> Types =
        ["Patient", "Coverage", "ExplanationOfBenefit", "Encounter", "Claim"];

    private static BulkExportTypeFilter Filter(params string[] queries)
        => BulkExportTypeFilter.Parse(BulkExportTypeFilter.Split(queries), Types);

    private static ExplanationOfBenefit Eob(string status, string typeCode, string created) => new()
    {
        Status = Enum.Parse<ExplanationOfBenefit.ExplanationOfBenefitStatus>(status, ignoreCase: true),
        Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/claim-type", typeCode),
        Created = created,
        Id = "eob-1",
        Meta = new Meta { LastUpdated = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero) },
        BillablePeriod = new Period { Start = created, End = created },
    };

    [Fact]
    public void Split_SeparatesQueries_ButNotTheValuesInsideOne()
        => BulkExportTypeFilter.Split(["Coverage?status=active,cancelled,ExplanationOfBenefit?type=pharmacy", "Patient?gender=male"])
            .Should().Equal("Coverage?status=active,cancelled", "ExplanationOfBenefit?type=pharmacy", "Patient?gender=male");

    [Theory]
    [InlineData("ExplanationOfBenefit?status=active", true)]
    [InlineData("ExplanationOfBenefit?status=cancelled", false)]
    [InlineData("ExplanationOfBenefit?status=cancelled,active", true)]
    [InlineData("ExplanationOfBenefit?type=http://terminology.hl7.org/CodeSystem/claim-type|pharmacy", true)]
    [InlineData("ExplanationOfBenefit?type=http://other.example|pharmacy", false)]
    [InlineData("ExplanationOfBenefit?type=pharmacy", true)]
    [InlineData("ExplanationOfBenefit?type=|pharmacy", false)]
    [InlineData("ExplanationOfBenefit?status=active&type=professional", false)]
    [InlineData("ExplanationOfBenefit?created=2026-03", true)]
    [InlineData("ExplanationOfBenefit?created=ge2026-03-02", true)]
    [InlineData("ExplanationOfBenefit?created=gt2026-03-02", false)]
    [InlineData("ExplanationOfBenefit?created=lt2026-03-02", false)]
    [InlineData("ExplanationOfBenefit?created=le2026-03-02", true)]
    [InlineData("ExplanationOfBenefit?created=ne2026", false)]
    [InlineData("ExplanationOfBenefit?service-date=2026-03-02", true)]
    [InlineData("ExplanationOfBenefit?service-date=ge2026-04", false)]
    [InlineData("ExplanationOfBenefit?_lastUpdated=ge2026-10-01", true)]
    [InlineData("ExplanationOfBenefit?_lastUpdated=lt2026-10-01", false)]
    [InlineData("ExplanationOfBenefit?_id=eob-1", true)]
    public void Eob_Matches(string query, bool expected)
        => Filter(query).Matches(Eob("active", "pharmacy", "2026-03-02")).Should().Be(expected);

    [Fact]
    public void QueriesForOneType_AreAlternatives_AndOtherTypesPassUnfiltered()
    {
        var filter = Filter("ExplanationOfBenefit?status=cancelled", "ExplanationOfBenefit?type=pharmacy");

        filter.Matches(Eob("active", "pharmacy", "2026-03-02")).Should().BeTrue();
        filter.Matches(Eob("active", "professional", "2026-03-02")).Should().BeFalse();
        filter.Filters("Coverage").Should().BeFalse();
        filter.Matches(new Coverage { Status = FinancialResourceStatusCodes.Cancelled }).Should().BeTrue();
    }

    [Theory]
    [InlineData("Patient?name=interop", true)]
    [InlineData("Patient?name=TEST", true)]
    [InlineData("Patient?name=case", false)]
    [InlineData("Patient?birthdate=1970", true)]
    [InlineData("Patient?birthdate=1971", false)]
    [InlineData("Patient?gender=female", true)]
    public void Patient_StringDateAndTokenParameters(string query, bool expected)
    {
        var patient = new Patient
        {
            Name = [new HumanName { Family = "Interop", Given = ["Testcase"] }],
            BirthDate = "1970-01-01",
            Gender = AdministrativeGender.Female,
        };
        Filter(query).Matches(patient).Should().Be(expected);
    }

    [Theory]
    [InlineData("Encounter?date=2026-03-02", false)] // eq: the stay must lie within the day
    [InlineData("Encounter?date=2026-02", false)]
    [InlineData("Encounter?date=2026", true)]
    [InlineData("Encounter?date=ge2026-03-05", false)]
    [InlineData("Encounter?date=le2026-03-01", true)]
    public void Encounter_PeriodIsARange(string query, bool expected)
    {
        // 2026-02-27 to 2026-03-03: a stay that spans the searched day.
        var encounter = new Encounter { Period = new Period { Start = "2026-02-27", End = "2026-03-03" } };
        Filter(query).Matches(encounter).Should().Be(expected);
    }

    [Theory]
    [InlineData("Coverage")]
    [InlineData("Coverage?")]
    [InlineData("Observation?code=1")]
    [InlineData("Coverage?status:not=active")]
    [InlineData("Coverage?payor.name=x")]
    [InlineData("Coverage?beneficiary=Patient/1")]
    [InlineData("Coverage?nonsense=1")]
    [InlineData("ExplanationOfBenefit?created=yesterday")]
    public void AQueryThatCannotBeApplied_IsRefused(string query)
    {
        var act = () => Filter(query);
        act.Should().Throw<BulkExportRequestException>();
    }

    [Fact]
    public void TheWorker_ParsesOnlyTheTypesAFilterNames()
    {
        var filter = Filter("Patient?gender=female");
        BulkExportWorker.Passes(filter, "Coverage", "not even json").Should().BeTrue();
        BulkExportWorker.Passes(filter, "Patient", """{"resourceType":"Patient","id":"a","gender":"female"}""").Should().BeTrue();
        BulkExportWorker.Passes(filter, "Patient", """{"resourceType":"Patient","id":"b","gender":"male"}""").Should().BeFalse();
    }
}
