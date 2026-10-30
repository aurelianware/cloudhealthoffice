using System.Text.Json.Nodes;
using FhirService.Services.PublicDirectory;
using FluentAssertions;

namespace CloudHealthOffice.FhirService.Tests.Services.PublicDirectory;

public class PlanNetLinkingTests
{
    private static JsonObject Payer() => new()
    {
        ["resourceType"] = "Organization", ["id"] = "payer", ["name"] = "Acme Health Plan",
    };

    private static JsonObject Network() => new()
    {
        ["resourceType"] = "Organization", ["id"] = "net-1", ["name"] = "Acme Gold Network",
        ["partOf"] = new JsonObject { ["reference"] = "Organization/payer" },
    };

    private static JsonObject Hospital() => new() { ["resourceType"] = "Organization", ["id"] = "1999999992" };

    private static JsonObject Affiliation(string network = "Organization/net-1") => new()
    {
        ["resourceType"] = "OrganizationAffiliation", ["id"] = "aff-1",
        ["participatingOrganization"] = new JsonObject { ["reference"] = "Organization/1999999992" },
        ["network"] = new JsonArray { new JsonObject { ["reference"] = network } },
    };

    private static JsonObject Role(string practice) => new()
    {
        ["resourceType"] = "PractitionerRole", ["id"] = "role-1",
        ["organization"] = new JsonObject { ["reference"] = $"Organization/{practice}" },
        ["healthcareService"] = new JsonArray { new JsonObject { ["reference"] = $"HealthcareService/{practice}-svc" } },
        ["location"] = new JsonArray
        {
            new JsonObject { ["reference"] = "Location/1234567893-loc" },
            new JsonObject { ["reference"] = $"Location/{practice}-loc" },
        },
    };

    private static JsonObject Resource(string type, string id) => new() { ["resourceType"] = type, ["id"] = id };

    [Fact]
    public void An_affiliation_is_primarily_with_the_organization_that_manages_its_network()
    {
        var affiliation = Affiliation();

        PlanNetLinking.Link([Payer(), Network(), Hospital(), affiliation], "payer");

        affiliation["organization"]!["reference"]!.GetValue<string>().Should().Be("Organization/payer");
        affiliation["organization"]!["display"]!.GetValue<string>().Should().Be("Acme Health Plan");
    }

    [Fact]
    public void An_affiliation_whose_network_is_not_published_gets_no_primary_organization()
    {
        var affiliation = Affiliation("Organization/net-unknown");

        PlanNetLinking.Link([Payer(), Network(), Hospital(), affiliation], "payer");

        affiliation.ContainsKey("organization").Should().BeFalse();
    }

    [Fact]
    public void A_role_keeps_the_practice_links_the_directory_publishes()
    {
        var role = Role("1999999992");

        PlanNetLinking.Link(
            [Payer(), Hospital(), role, Resource("HealthcareService", "1999999992-svc"),
             Resource("Location", "1999999992-loc"), Resource("Location", "1234567893-loc")],
            "payer");

        role["organization"]!["reference"]!.GetValue<string>().Should().Be("Organization/1999999992");
        role["healthcareService"]!.AsArray().Should().ContainSingle();
        role["location"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public void A_role_drops_the_links_to_a_hospital_the_directory_does_not_publish()
    {
        var role = Role("1888888884");

        PlanNetLinking.Link([Payer(), role, Resource("Location", "1234567893-loc")], "payer");

        role.ContainsKey("organization").Should().BeFalse();
        role.ContainsKey("healthcareService").Should().BeFalse();
        role["location"]!.AsArray().Select(l => l!["reference"]!.GetValue<string>())
            .Should().Equal("Location/1234567893-loc");
    }

    [Fact]
    public void The_payer_lists_the_endpoints_it_manages()
    {
        var payer = Payer();
        var planDocument = Resource("Endpoint", "ep-sbc");
        planDocument["managingOrganization"] = new JsonObject { ["reference"] = "Organization/payer" };
        var other = Resource("Endpoint", "ep-other");
        other["managingOrganization"] = new JsonObject { ["reference"] = "Organization/1999999992" };

        PlanNetLinking.Link([payer, Hospital(), planDocument, other], "payer");

        payer["endpoint"]!.AsArray().Select(e => e!["reference"]!.GetValue<string>())
            .Should().Equal("Endpoint/ep-sbc");
    }
}
