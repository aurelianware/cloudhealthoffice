using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FhirService.Services;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

public class AdapterStatusControllerTests : IClassFixture<FhirTestWebAppFactory>
{
    private readonly FhirTestWebAppFactory _factory;

    public AdapterStatusControllerTests(FhirTestWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AdapterStatus_is_public_and_labels_demo_mode()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/fhir/r4/adapter-status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = await response.Content.ReadFromJsonAsync<FhirAdapterStatusReport>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        report.Should().NotBeNull();
        report!.EffectiveMode.Should().BeOneOf("Demo", "Hybrid");
        report.DataClassification.Should().Be("synthetic");
        report.AttestationNote.Should().Contain("not legal attestation");
        report.Resources.Should().NotBeEmpty();
        report.Resources.Should().Contain(r => r.Resource == "Patient");
        report.Resources.Should().Contain(r => r.Resource == "PayerToPayer");
    }

    [Fact]
    public async Task Metadata_carries_adapter_label_headers()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/fhir/r4/metadata");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Should().ContainKey(FhirAdapterStatusService.HeaderMode);
        response.Headers.GetValues(FhirAdapterStatusService.HeaderMode).Single()
            .Should().BeOneOf("Demo", "Hybrid");
        response.Headers.GetValues(FhirAdapterStatusService.HeaderDataClass).Single()
            .Should().Be("synthetic");
        response.Headers.GetValues(FhirAdapterStatusService.HeaderLabel).Single()
            .Should().NotBeNullOrWhiteSpace();
    }
}
