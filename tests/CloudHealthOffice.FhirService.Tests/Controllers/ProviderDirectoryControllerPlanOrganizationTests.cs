using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FhirService.Controllers;
using FhirService.Services.PlanOrganization;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

/// <summary>
/// The plan's own Organization (<see cref="PlanOrganizationOptions"/>): the
/// insurer and payor CARIN BB follows from an EOB and a Coverage. It is
/// served from configuration, and a search judged only by <c>_id</c> and
/// <c>_lastUpdated</c> finds it beside provider-service's Organizations.
/// </summary>
public class ProviderDirectoryControllerPlanOrganizationTests
{
    private static readonly DateTimeOffset LastUpdated = new(2026, 10, 31, 4, 0, 0, 123, TimeSpan.Zero);

    private readonly Mock<IHttpClientFactory> _factory = new();
    private readonly StubHandler _providerService = new();
    private ProviderDirectoryController _controller;

    public ProviderDirectoryControllerPlanOrganizationTests()
    {
        _factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_providerService) { BaseAddress = new Uri("http://provider-service.test/") });
        _controller = Controller(new PlanOrganizationOptions
        {
            Id = "plan",
            Name = "Example Health Plan",
            PayerId = "12345",
            NaicCode = "67890",
            Phone = "555-0199",
            AddressLine = "1 Plan Way",
            City = "Springfield",
            State = "FL",
            PostalCode = "32401",
            LastUpdated = LastUpdated,
        });
    }

    private ProviderDirectoryController Controller(PlanOrganizationOptions options, string query = "")
    {
        var controller = new ProviderDirectoryController(
            _factory.Object, NullLogger<ProviderDirectoryController>.Instance, Options.Create(options));
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private static JsonObject Body(IActionResult result)
    {
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(200);
        return JsonNode.Parse(content.Content!)!.AsObject();
    }

    private void ProviderServiceAnswers(params string[] organizationIds)
    {
        var entries = string.Join(",", organizationIds.Select(id =>
            $"{{\"resource\":{{\"resourceType\":\"Organization\",\"id\":\"{id}\"}},\"search\":{{\"mode\":\"match\"}}}}"));
        _providerService.Body = $"{{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":{organizationIds.Length},\"entry\":[{entries}]}}";
    }

    [Fact]
    public async Task Read_serves_a_carin_bb_organization_without_asking_provider_service()
    {
        var organization = Body(await _controller.ReadOrganization("plan", default));

        organization["id"]!.GetValue<string>().Should().Be("plan");
        organization["active"]!.GetValue<bool>().Should().BeTrue();
        organization["name"]!.GetValue<string>().Should().Be("Example Health Plan");
        organization["meta"]!["lastUpdated"]!.GetValue<string>().Should().Be("2026-10-31T04:00:00.123Z");
        organization["meta"]!["profile"]!.AsArray().Select(p => p!.GetValue<string>()).Should().Equal(
            "http://hl7.org/fhir/us/carin-bb/StructureDefinition/C4BB-Organization",
            "http://hl7.org/fhir/us/carin-bb/StructureDefinition/C4BB-Organization|2.0.0");
        organization["identifier"]!.AsArray().Select(i => i!["type"]!["coding"]![0]!["code"]!.GetValue<string>())
            .Should().Equal("payerid", "naiccode");
        organization["address"]![0]!["country"]!.GetValue<string>().Should().Be("US");
        _providerService.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Any_other_id_is_still_read_from_provider_service()
    {
        _providerService.Body = """{"resourceType":"Organization","id":"1234567893"}""";

        Body(await _controller.ReadOrganization("1234567893", default))["id"]!.GetValue<string>().Should().Be("1234567893");
        _providerService.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Search_by_its_id_finds_it_alone()
    {
        var controller = Controller(PlanOptions(), "?_id=plan");

        var bundle = Body(await controller.SearchOrganizations(default));

        bundle["total"]!.GetValue<int>().Should().Be(1);
        bundle["entry"]![0]!["resource"]!["id"]!.GetValue<string>().Should().Be("plan");
        _providerService.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Search_by_its_last_update_adds_it_to_what_provider_service_found()
    {
        ProviderServiceAnswers();
        var controller = Controller(PlanOptions(), "?_lastUpdated=2026-10-31T04:00:00.123Z");

        var bundle = Body(await controller.SearchOrganizations(default));

        bundle["entry"]!.AsArray().Select(e => e!["resource"]!["id"]!.GetValue<string>()).Should().Equal("plan");
        bundle["total"]!.GetValue<int>().Should().Be(1);
    }

    [Theory]
    [InlineData("?_lastUpdated=2026-10-31T04:00:01Z")]
    [InlineData("?_id=1234567893")]
    [InlineData("?name=Acme")]
    public async Task A_search_it_does_not_match_is_provider_service_alone(string query)
    {
        ProviderServiceAnswers("1234567893");
        var controller = Controller(PlanOptions(), query);

        var bundle = Body(await controller.SearchOrganizations(default));

        bundle["entry"]!.AsArray().Select(e => e!["resource"]!["id"]!.GetValue<string>()).Should().Equal("1234567893");
    }

    [Fact]
    public async Task Without_configuration_nothing_changes()
    {
        ProviderServiceAnswers("1234567893");
        var controller = Controller(new PlanOrganizationOptions(), "?_lastUpdated=2026");

        var bundle = Body(await controller.SearchOrganizations(default));

        bundle["entry"]!.AsArray().Should().ContainSingle();
        _providerService.Body = """{"resourceType":"OperationOutcome"}""";
        _providerService.Status = HttpStatusCode.NotFound;
        (await controller.ReadOrganization("plan", default)).Should().BeOfType<ContentResult>()
            .Which.StatusCode.Should().Be(404);
    }

    private static PlanOrganizationOptions PlanOptions() => new()
    {
        Id = "plan",
        Name = "Example Health Plan",
        LastUpdated = LastUpdated,
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        public string Body { get; set; } = "{}";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/fhir+json"),
            });
        }
    }
}
