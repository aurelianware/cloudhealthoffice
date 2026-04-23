using System.Net;
using FhirService.Services;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

/// <summary>
/// Integration tests for <see cref="FhirService.Controllers.OperationDefinitionController"/>.
/// Verifies the cho-appeal-submit operation is served at its canonical URL.
/// </summary>
public class OperationDefinitionControllerTests : IClassFixture<FhirTestWebAppFactory>
{
    private readonly FhirTestWebAppFactory _factory;
    private static readonly FhirJsonParser _parser = new(new ParserSettings { PermissiveParsing = false });

    public OperationDefinitionControllerTests(FhirTestWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_OperationDefinition_by_id_returns_cho_appeal_submit()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/fhir/r4/OperationDefinition/cho-appeal-submit");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var od = _parser.Parse<OperationDefinition>(await response.Content.ReadAsStringAsync());

        od.Url.Should().Be(ChoFhirCanonicalUrls.AppealSubmitOperation);
        od.Code.Should().Be("cho-appeal-submit");
        od.Type.Should().BeTrue("operation is type-level on Task");
        od.Instance.Should().BeFalse();
        od.System.Should().BeFalse();
        od.Resource.Should().ContainSingle().Which.ToString().Should().Be("Task");

        var inParam = od.Parameter.Single(p => p.Use == OperationParameterUse.In);
        inParam.Name.Should().Be("resource");
        inParam.Type.Should().Be(FHIRAllTypes.Bundle);

        var outParam = od.Parameter.Single(p => p.Use == OperationParameterUse.Out);
        outParam.Name.Should().Be("return");
        outParam.Type.Should().Be(FHIRAllTypes.Bundle);
    }

    [Fact]
    public async Task Get_OperationDefinition_by_unknown_id_returns_404()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/fhir/r4/OperationDefinition/not-real");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_OperationDefinition_search_returns_Bundle_with_operation()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/fhir/r4/OperationDefinition");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bundle = _parser.Parse<Bundle>(await response.Content.ReadAsStringAsync());
        bundle.Type.Should().Be(Bundle.BundleType.Searchset);
        bundle.Entry.Select(e => ((OperationDefinition)e.Resource).Id)
            .Should().Contain("cho-appeal-submit");
    }
}
