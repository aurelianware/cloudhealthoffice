using FhirService.Services.PublicDirectory;
using FluentAssertions;

namespace CloudHealthOffice.FhirService.Tests.Services.PublicDirectory;

/// <summary>
/// G-36: the public directory's FHIR search, over the fixture directory. The
/// cases follow what Plan-Net 1.2.0 requires of a server and what the Inferno
/// Plan-Net kit exercises: each parameter type, chains, reverse chains,
/// includes, revincludes and paging.
/// </summary>
public class PlanNetSearchEngineTests
{
    private static readonly PlanNetDirectorySnapshot Directory = PlanNetDirectoryFixture.Snapshot();

    private static PlanNetSearchResult Search(string type, params (string Key, string Value)[] query)
        => PlanNetSearchEngine.Search(Directory, type, query.Select(q => new KeyValuePair<string, string>(q.Key, q.Value)));

    private static IEnumerable<string> Ids(PlanNetSearchResult result)
        => result.Matches.Select(r => r["id"]!.GetValue<string>());

    [Fact]
    public void A_search_without_parameters_returns_every_resource_of_the_type()
    {
        var result = Search("PractitionerRole");

        result.Total.Should().Be(2);
        Ids(result).Should().Equal("r1", "r2");
    }

    [Theory]
    [InlineData("smi", "1234567893")]
    [InlineData("MULLER", "1111111111")]
    [InlineData("anna", "1111111111")]
    public void String_search_is_a_case_and_accent_insensitive_prefix_match(string value, string expected)
        => Ids(Search("Practitioner", ("name", value))).Should().Equal(expected);

    [Fact]
    public void String_modifiers_exact_and_contains_apply()
    {
        Ids(Search("Practitioner", ("family:exact", "Müller"))).Should().Equal(PlanNetDirectoryFixture.Muller);
        Ids(Search("Practitioner", ("family:exact", "muller"))).Should().BeEmpty();
        Ids(Search("Organization", ("name:contains", "general"))).Should().Equal(PlanNetDirectoryFixture.Hospital);
    }

    [Fact]
    public void Location_address_parameters_search_their_parts()
    {
        Ids(Search("Location", ("address-city", "Hartford"))).Should().Equal($"{PlanNetDirectoryFixture.Smith}-loc");
        Ids(Search("Location", ("address-postalcode", "065"))).Should().Equal($"{PlanNetDirectoryFixture.Hospital}-loc");
        Ids(Search("Location", ("address", "5 elm"))).Should().Equal($"{PlanNetDirectoryFixture.Smith}-loc");
        Search("Location", ("address-state", "CT")).Total.Should().Be(2);
    }

    [Fact]
    public void Token_search_honours_system_and_code()
    {
        Ids(Search("PractitionerRole", ("specialty", "http://nucc.org/provider-taxonomy|207R00000X"))).Should().Equal("r1");
        Ids(Search("PractitionerRole", ("specialty", "207R00000X"))).Should().Equal("r1");
        Ids(Search("PractitionerRole", ("specialty", "|207R00000X"))).Should().BeEmpty("the coding has a system");
        Ids(Search("PractitionerRole", ("specialty", "http://other|207R00000X"))).Should().BeEmpty();
        Ids(Search("Organization", ("type", "ntwk"))).Should().Equal(PlanNetDirectoryFixture.Network);
    }

    [Fact]
    public void Token_text_and_not_modifiers_apply()
    {
        Ids(Search("PractitionerRole", ("specialty:text", "internal"))).Should().Equal("r1");
        Ids(Search("PractitionerRole", ("specialty:not", "207R00000X"))).Should().Equal("r2");
    }

    [Fact]
    public void Identifier_search_finds_a_provider_by_npi()
        => Ids(Search("Practitioner", ("identifier", $"http://hl7.org/fhir/sid/us-npi|{PlanNetDirectoryFixture.Smith}")))
            .Should().Equal(PlanNetDirectoryFixture.Smith);

    [Theory]
    [InlineData("Organization/net-a")]
    [InlineData("net-a")]
    [InlineData("https://example.org/fhir/r4/directory/acme-health/Organization/net-a")]
    public void Reference_search_accepts_typed_bare_and_absolute_references(string value)
        => Search("PractitionerRole", ("network", value)).Total.Should().Be(2);

    [Fact]
    public void Comma_is_or_and_repetition_is_and()
    {
        Search("PractitionerRole", ("specialty", "207R00000X,208D00000X")).Total.Should().Be(2);
        Ids(Search("PractitionerRole",
            ("specialty", "207R00000X,208D00000X"),
            ("practitioner", $"Practitioner/{PlanNetDirectoryFixture.Muller}"))).Should().Equal("r2");
    }

    [Theory]
    [InlineData("gt2026-01-01", "r1")]
    [InlineData("lt2026-01-01", "r2")]
    [InlineData("2026-04-27", "r1")]
    [InlineData("ge2026-04-27T10:00:00Z", "r1")]
    [InlineData("eb2025-06", "r2")]
    public void LastUpdated_honours_date_prefixes(string value, string expected)
        => Ids(Search("PractitionerRole", ("_lastUpdated", value))).Should().Equal(expected);

    [Fact]
    public void Period_search_compares_ranges()
    {
        Ids(Search("PractitionerRole", ("period", "sa2019-12-31"))).Should().Equal("r1", "r2");
        Ids(Search("PractitionerRole", ("period", "gt2031-01-01"))).Should().Equal("r1");
    }

    [Fact]
    public void Id_search_matches_any_of_the_listed_ids()
        => Ids(Search("PractitionerRole", ("_id", "r2,nope"))).Should().Equal("r2");

    [Fact]
    public void Id_search_accepts_a_reference_to_the_type_as_the_inferno_kit_sends_it()
    {
        Ids(Search("PractitionerRole", ("_id", "PractitionerRole/r2"))).Should().Equal("r2");
        Ids(Search("PractitionerRole", ("_id", "https://example.org/fhir/PractitionerRole/r1/_history/3"))).Should().Equal("r1");
        Ids(Search("PractitionerRole", ("_id", "Location/r2"))).Should().BeEmpty("the reference names a Location");
    }

    [Fact]
    public void A_revinclude_on_a_referenced_id_returns_the_resources_pointing_at_it()
    {
        // The kit's revinclude tests: _id is the reference it read off a PractitionerRole.
        var result = Search("Practitioner",
            ("_id", $"Practitioner/{PlanNetDirectoryFixture.Smith}"),
            ("_revinclude", "PractitionerRole:practitioner"));

        Ids(result).Should().Equal(PlanNetDirectoryFixture.Smith);
        result.Included.Select(r => r["resourceType"]!.GetValue<string>()).Should().Contain("PractitionerRole");
    }

    [Fact]
    public void Missing_modifier_tests_presence()
    {
        Search("PractitionerRole", ("organization:missing", "true")).Total.Should().Be(2);
        Ids(Search("PractitionerRole", ("location:missing", "false"))).Should().Equal("r1");
    }

    [Fact]
    public void Forward_chains_search_the_referenced_resource()
    {
        Ids(Search("PractitionerRole", ("location.address-city", "Hartford"))).Should().Equal("r1");
        Search("PractitionerRole", ("network.name", "Acme Gold")).Total.Should().Be(2);
        Ids(Search("PractitionerRole", ("practitioner.name", "muller"))).Should().Equal("r2");
        Ids(Search("OrganizationAffiliation", ("service.service-category", "hosp"))).Should().Equal("a1");
        Ids(Search("Location", ("organization.name", "Hartford General"))).Should().Equal($"{PlanNetDirectoryFixture.Hospital}-loc");
    }

    [Fact]
    public void Chains_nest()
        => Ids(Search("HealthcareService", ("location.organization.name", "Hartford")))
            .Should().Equal($"{PlanNetDirectoryFixture.Hospital}-svc");

    [Fact]
    public void Reverse_chains_select_what_matching_resources_point_at()
    {
        Ids(Search("Practitioner", ("_has:PractitionerRole:practitioner:specialty", "207R00000X")))
            .Should().Equal(PlanNetDirectoryFixture.Smith);
        Ids(Search("Practitioner", ("_has:PractitionerRole:practitioner:location", $"Location/{PlanNetDirectoryFixture.Smith}-loc")))
            .Should().Equal(PlanNetDirectoryFixture.Smith);
        Ids(Search("Organization", ("_has:OrganizationAffiliation:participating-organization:network", "Organization/net-a")))
            .Should().Equal(PlanNetDirectoryFixture.Hospital);
        Ids(Search("Organization", ("_has:InsurancePlan:owned-by:name", "Acme"))).Should().Equal("payer");
    }

    [Fact]
    public void Reverse_chain_combines_with_other_parameters()
        => Ids(Search("HealthcareService",
                ("specialty:missing", "true"),
                ("_has:OrganizationAffiliation:service:network", "Organization/net-a")))
            .Should().Equal($"{PlanNetDirectoryFixture.Hospital}-svc");

    [Fact]
    public void Include_adds_the_referenced_resources()
    {
        var result = Search("PractitionerRole",
            ("_id", "r1"),
            ("_include", "PractitionerRole:practitioner"),
            ("_include", "PractitionerRole:network"),
            ("_include", "PractitionerRole:location"));

        Ids(result).Should().Equal("r1");
        result.Included.Select(PlanNetDirectorySnapshot.Key).Should().BeEquivalentTo(
            $"Practitioner/{PlanNetDirectoryFixture.Smith}", "Organization/net-a", $"Location/{PlanNetDirectoryFixture.Smith}-loc");
    }

    [Fact]
    public void Include_star_follows_every_reference()
        => Search("OrganizationAffiliation", ("_include", "OrganizationAffiliation:*")).Included
            .Select(PlanNetDirectorySnapshot.Key).Should().BeEquivalentTo(
                $"Organization/{PlanNetDirectoryFixture.Hospital}", "Organization/net-a",
                $"HealthcareService/{PlanNetDirectoryFixture.Hospital}-svc", $"Location/{PlanNetDirectoryFixture.Hospital}-loc");

    [Fact]
    public void Revinclude_adds_the_resources_pointing_at_the_page()
    {
        var result = Search("Organization",
            ("_id", "net-a"),
            ("_revinclude", "PractitionerRole:network"),
            ("_revinclude", "OrganizationAffiliation:network"),
            ("_revinclude", "InsurancePlan:network"));

        result.Included.Select(PlanNetDirectorySnapshot.Key).Should().BeEquivalentTo(
            "PractitionerRole/r1", "PractitionerRole/r2", "OrganizationAffiliation/a1", "InsurancePlan/plan-1");
    }

    [Fact]
    public void An_include_for_another_type_is_reported_not_applied()
    {
        var result = Search("Practitioner", ("_include", "PractitionerRole:location"));

        result.Included.Should().BeEmpty();
        result.Ignored.Should().Contain("_include=PractitionerRole:location");
    }

    [Fact]
    public void Paging_returns_the_requested_slice_and_the_full_total()
    {
        var first = Search("PractitionerRole", ("_count", "1"));
        var second = Search("PractitionerRole", ("_count", "1"), ("_page", "2"));

        first.Total.Should().Be(2);
        Ids(first).Should().Equal("r1");
        Ids(second).Should().Equal("r2");
    }

    [Fact]
    public void An_unknown_parameter_is_ignored_and_reported()
    {
        var result = Search("PractitionerRole", ("near", "41.7|-72.6|10|mi"), ("_sort", "name"));

        result.Total.Should().Be(2);
        result.Ignored.Should().BeEquivalentTo("near", "_sort");
    }

    [Theory]
    [InlineData("name:below", "x")]
    [InlineData("_lastUpdated", "yesterday")]
    [InlineData("_lastUpdated", "xx2026")]
    [InlineData("organization:missing", "maybe")]
    [InlineData("_count", "ten")]
    [InlineData("_has:PractitionerRole:practitioner", "x")]
    public void A_malformed_search_is_an_error(string key, string value)
    {
        var type = key.StartsWith("name", StringComparison.Ordinal) || key.StartsWith("_has", StringComparison.Ordinal)
            ? "Practitioner"
            : "PractitionerRole";
        var act = () => Search(type, (key, value));

        act.Should().Throw<PlanNetSearchException>();
    }

    [Fact]
    public void Chaining_through_a_non_reference_parameter_is_an_error()
    {
        var act = () => Search("PractitionerRole", ("specialty.name", "x"));

        act.Should().Throw<PlanNetSearchException>();
    }
}
