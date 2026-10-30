using FhirService.Controllers;
using FhirService.Services.ChoNative;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

/// <summary>
/// The CARIN BB ExplanationOfBenefit searches, translated into claims-service's
/// structured filters. Before, every parameter but patient and _id was
/// forwarded and ignored, so a type or date search answered with every EOB the
/// patient had (nightly 2026-10-30).
/// </summary>
public class ExplanationOfBenefitSearchTranslationTests
{
    private static string? Translate(string query, string? patient = "M-1")
        => ExplanationOfBenefitController.BuildUpstreamQueryString(
            new QueryCollection(QueryHelpers(query)), patient);

    private static Dictionary<string, StringValues> QueryHelpers(string query)
        => Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query);

    private static string Decoded(string? upstream) => Uri.UnescapeDataString(upstream ?? "");

    [Theory]
    [InlineData("?type=institutional", "claimType=Institutional")]
    [InlineData("?type=http://terminology.hl7.org/CodeSystem/claim-type|oral", "claimType=Dental")]
    [InlineData("?type=professional,institutional", "claimType=Professional&claimType=Institutional")]
    [InlineData("?identifier=INTEROP-CLM-INST-1", "claimNumber=INTEROP-CLM-INST-1")]
    [InlineData("?identifier=urn:cho:claim-number|C-9", "claimNumber=C-9")]
    [InlineData("?status=cancelled", "status=cancelled")]
    public void Token_parameters_become_structured_filters(string query, string expected)
        => Decoded(Translate(query)).Should().Be("?patient=M-1&" + expected);

    [Fact]
    public void Service_date_is_a_window_the_service_dates_must_overlap()
        => Decoded(Translate("?service-date=gt2026-04-05T00:00:00%2B00:00")).Should()
            .Be("?patient=M-1&serviceFrom=2026-04-05T00:00:01.0000000Z");

    [Fact]
    public void Service_start_date_and_billable_period_start_narrow_the_same_first_day()
        => Decoded(Translate("?service-start-date=ge2026-04-01&billable-period-start=2026-04-06")).Should()
            .Be("?patient=M-1&serviceStartFrom=2026-04-06T00:00:00.0000000Z&serviceStartTo=2026-04-07T00:00:00.0000000Z");

    [Fact]
    public void Last_updated_and_created_become_ranges()
        => Decoded(Translate("?_lastUpdated=2026-10-30&created=lt2026-01-01")).Should()
            .Be("?patient=M-1&lastUpdatedFrom=2026-10-30T00:00:00.0000000Z&lastUpdatedTo=2026-10-31T00:00:00.0000000Z"
                + "&createdTo=2026-01-01T00:00:00.0000000Z");

    [Theory]
    [InlineData("?type=pharmacy")]
    [InlineData("?type=vision,pharmacy")]
    [InlineData("?use=predetermination")]
    [InlineData("?status=draft")]
    [InlineData("?service-date=ge2026-05-01&service-date=lt2026-04-01")]
    public void A_search_nothing_CHO_holds_could_match_is_answered_without_asking(string query)
        => Translate(query).Should().BeNull();

    [Theory]
    [InlineData("?provider=Practitioner/1")]
    [InlineData("?_sort=created")]
    [InlineData("?_include=ExplanationOfBenefit:organization")]
    [InlineData("?type=http://other.example|institutional")]
    [InlineData("?type=dental")]
    public void A_parameter_or_value_it_cannot_apply_is_refused(string query)
    {
        var act = () => Translate(query);
        act.Should().Throw<FhirSearchNotSupportedException>();
    }

    [Theory]
    [InlineData("ExplanationOfBenefit:care-team")]
    [InlineData("ExplanationOfBenefit:payee")]
    [InlineData("ExplanationOfBenefit:*")]
    public void Carin_includes_are_accepted_and_change_nothing_upstream(string include)
        => Decoded(Translate("?_id=E-1&_include=" + include, patient: null)).Should().Be("?_id=E-1");

    [Fact]
    public void Paging_and_the_patient_are_forwarded()
        => Decoded(Translate("?_count=25&_page=2&patient=Patient/M-9", patient: "M-9")).Should()
            .Be("?patient=M-9&_count=25&_page=2");
}
