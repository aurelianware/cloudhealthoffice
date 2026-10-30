using System.Net;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

/// <summary>
/// Inferno's CARIN BB kit fails a server whose CapabilityStatement does not
/// instantiate CARIN BB's server CapabilityStatement (run 40979709678).
/// </summary>
public class MetadataInstantiatesTests : IClassFixture<FhirTestWebAppFactory>
{
    private static readonly FhirJsonParser Parser = new(new ParserSettings { PermissiveParsing = true });

    private readonly FhirTestWebAppFactory _factory;

    public MetadataInstantiatesTests(FhirTestWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task The_capability_statement_instantiates_carin_bb()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/fhir/r4/metadata");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Parser.Parse<CapabilityStatement>(await response.Content.ReadAsStringAsync())
            .Instantiates.Should().Contain("http://hl7.org/fhir/us/carin-bb/CapabilityStatement/c4bb");
    }
}
