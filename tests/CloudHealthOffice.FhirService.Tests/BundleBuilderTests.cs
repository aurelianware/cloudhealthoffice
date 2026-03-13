using FluentAssertions;
using Hl7.Fhir.Model;
using FhirService.Services;
using Microsoft.Extensions.Configuration;

namespace CloudHealthOffice.FhirService.Tests;

/// <summary>
/// Unit tests for FhirBundleBuilder — validates Bundle structure, pagination links,
/// entry fullUrls, and search mode flags.
/// </summary>
public class BundleBuilderTests
{
    private static FhirBundleBuilder CreateBuilder()
    {
        var config = new ConfigurationBuilder().Build();
        return new FhirBundleBuilder(config);
    }

    [Fact]
    public void Build_SetsTypToSearchset()
    {
        var builder = CreateBuilder();
        var patients = new List<Patient> { new() { Id = "pat-001" } };

        var bundle = builder.Build(patients, 1, 1, 20, "Patient",
            "https://api.cho.local/fhir/r4", string.Empty);

        bundle.Type.Should().Be(Bundle.BundleType.Searchset);
    }

    [Fact]
    public void Build_TotalReflectsFullResultCount_NotPageCount()
    {
        var builder = CreateBuilder();
        var pageItems = new List<Patient>
        {
            new() { Id = "pat-001" },
            new() { Id = "pat-002" }
        };

        // 10 total, but only 2 on this page
        var bundle = builder.Build(pageItems, 10, 1, 2, "Patient",
            "https://api.cho.local/fhir/r4", string.Empty);

        bundle.Total.Should().Be(10);
        bundle.Entry.Should().HaveCount(2);
    }

    [Fact]
    public void Build_EntryFullUrl_UsesResourceTypeAndId()
    {
        var builder = CreateBuilder();
        var patients = new List<Patient> { new() { Id = "pat-001" } };

        var bundle = builder.Build(patients, 1, 1, 20, "Patient",
            "https://api.cho.local/fhir/r4", string.Empty);

        bundle.Entry[0].FullUrl.Should().Be("https://api.cho.local/fhir/r4/Patient/pat-001");
    }

    [Fact]
    public void Build_EntrySearchMode_IsMatch()
    {
        var builder = CreateBuilder();
        var patients = new List<Patient> { new() { Id = "pat-001" } };

        var bundle = builder.Build(patients, 1, 1, 20, "Patient",
            "https://api.cho.local/fhir/r4", string.Empty);

        bundle.Entry[0].Search!.Mode.Should().Be(Bundle.SearchEntryMode.Match);
    }

    [Fact]
    public void Build_SelfLinkAlwaysPresent()
    {
        var builder = CreateBuilder();
        var bundle = builder.Build(new List<Patient>(), 0, 1, 20, "Patient",
            "https://api.cho.local/fhir/r4", string.Empty);

        bundle.Link.Should().Contain(l => l.Relation == "self");
    }

    [Fact]
    public void Build_NextLink_AppearsWhenMorePagesExist()
    {
        var builder = CreateBuilder();
        var patients = Enumerable.Range(1, 5)
            .Select(i => new Patient { Id = $"pat-{i:D3}" })
            .ToList();

        // Page 1 of 3 (5 items per page, 15 total)
        var bundle = builder.Build(patients, 15, 1, 5, "Patient",
            "https://api.cho.local/fhir/r4", string.Empty);

        bundle.Link.Should().Contain(l => l.Relation == "next");
        bundle.Link.Should().NotContain(l => l.Relation == "prev");
    }

    [Fact]
    public void Build_PrevLink_AppearsOnPage2()
    {
        var builder = CreateBuilder();
        var patients = Enumerable.Range(6, 5)
            .Select(i => new Patient { Id = $"pat-{i:D3}" })
            .ToList();

        var bundle = builder.Build(patients, 15, 2, 5, "Patient",
            "https://api.cho.local/fhir/r4", string.Empty);

        bundle.Link.Should().Contain(l => l.Relation == "prev");
        bundle.Link.Should().Contain(l => l.Relation == "next");
    }

    [Fact]
    public void Build_NextLink_AbsentOnLastPage()
    {
        var builder = CreateBuilder();
        var patients = Enumerable.Range(11, 5)
            .Select(i => new Patient { Id = $"pat-{i:D3}" })
            .ToList();

        // Page 3 of 3
        var bundle = builder.Build(patients, 15, 3, 5, "Patient",
            "https://api.cho.local/fhir/r4", string.Empty);

        bundle.Link.Should().NotContain(l => l.Relation == "next");
        bundle.Link.Should().Contain(l => l.Relation == "prev");
    }

    [Fact]
    public void Build_NextLink_ContainsCorrectPageParameter()
    {
        var builder = CreateBuilder();
        var patients = new List<Patient> { new() { Id = "pat-001" } };

        var bundle = builder.Build(patients, 50, 1, 1, "Patient",
            "https://api.cho.local/fhir/r4", "?name=Smith");

        var next = bundle.Link.First(l => l.Relation == "next");
        next.Url.Should().Contain("_page=2");
        next.Url.Should().Contain("_count=1");
        // Original param preserved
        next.Url.Should().Contain("name=Smith");
    }

    [Fact]
    public void Build_EmptyResultSet_ReturnsZeroTotal()
    {
        var builder = CreateBuilder();
        var bundle = builder.Build(new List<Patient>(), 0, 1, 20, "Patient",
            "https://api.cho.local/fhir/r4", string.Empty);

        bundle.Total.Should().Be(0);
        bundle.Entry.Should().BeEmpty();
    }
}
