using System.Text.Json;
using FhirService.Models;
using FhirService.Services;
using FluentAssertions;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// G-46, in part. CHO has declared
/// <c>Coverage?patient={{context.patientId}}&amp;status=active</c> in its CDS
/// Hooks discovery document since CRD was added, and never read it. It is read
/// now, because the <c>coverage-information</c> extension requires a Coverage
/// reference and the prefetch is where a conforming EHR has already put one.
///
/// <para>
/// Nothing here falls back to a placeholder, and these tests are mostly about
/// that: a Coverage CHO did not actually resolve, named in a resource the EHR is
/// being asked to store, is a fabricated reference — worse than the card-only
/// answer it would be replacing.
/// </para>
/// </summary>
public class CrdPrefetchTests
{
    [Fact]
    public void A_searchset_bundle_yields_the_first_active_coverage()
    {
        Reference("""
            {
              "resourceType": "Bundle",
              "type": "searchset",
              "entry": [
                { "resource": { "resourceType": "Coverage", "id": "cov-1", "status": "active" } },
                { "resource": { "resourceType": "Coverage", "id": "cov-2", "status": "active" } }
              ]
            }
            """).Should().Be("Coverage/cov-1");
    }

    /// <summary>An EHR that resolved the template to one resource is answering the same question.</summary>
    [Fact]
    public void A_bare_coverage_resource_is_accepted()
    {
        Reference("""{ "resourceType": "Coverage", "id": "cov-7", "status": "active" }""")
            .Should().Be("Coverage/cov-7");
    }

    /// <summary>
    /// The template asked for active coverage. A server that answered with
    /// something else has not given CHO what it asked for, and a determination
    /// made against lapsed coverage is the kind of wrong answer that reads as
    /// authoritative.
    /// </summary>
    [Fact]
    public void A_cancelled_coverage_is_skipped_even_when_it_is_the_only_one()
    {
        Reference("""
            {
              "resourceType": "Bundle",
              "entry": [
                { "resource": { "resourceType": "Coverage", "id": "cov-old", "status": "cancelled" } }
              ]
            }
            """).Should().BeNull();
    }

    [Fact]
    public void An_inactive_coverage_does_not_hide_an_active_one_behind_it()
    {
        Reference("""
            {
              "resourceType": "Bundle",
              "entry": [
                { "resource": { "resourceType": "Coverage", "id": "cov-old", "status": "entered-in-error" } },
                { "resource": { "resourceType": "Coverage", "id": "cov-now", "status": "active" } }
              ]
            }
            """).Should().Be("Coverage/cov-now");
    }

    /// <summary>Absent is not the same as contradicted.</summary>
    [Fact]
    public void A_coverage_with_no_status_is_accepted()
    {
        Reference("""{ "resourceType": "Coverage", "id": "cov-3" }""").Should().Be("Coverage/cov-3");
    }

    [Theory]
    [InlineData("""{ "resourceType": "Bundle", "entry": [] }""")]
    [InlineData("""{ "resourceType": "Bundle" }""")]
    [InlineData("""{ "resourceType": "Coverage", "status": "active" }""")]
    [InlineData("""{ "resourceType": "Patient", "id": "pat-1" }""")]
    [InlineData("""{ "resourceType": "Bundle", "entry": [ { "resource": { "resourceType": "Patient", "id": "p" } } ] }""")]
    [InlineData("""{ }""")]
    public void Anything_that_is_not_a_usable_coverage_yields_nothing(string prefetch)
    {
        Reference(prefetch).Should().BeNull(
            "CHO names only a Coverage it actually resolved");
    }

    [Fact]
    public void A_request_with_no_prefetch_at_all_yields_nothing()
    {
        CrdPrefetch.ActiveCoverageReference(new CrdHookRequest()).Should().BeNull();

        CrdPrefetch.ActiveCoverageReference(new CrdHookRequest
        {
            Prefetch = new Dictionary<string, JsonElement>(),
        }).Should().BeNull();
    }

    /// <summary>
    /// A prefetch key an EHR could not resolve is conventionally sent as null.
    /// Reading it as a resource would be a crash on a perfectly ordinary request.
    /// </summary>
    [Fact]
    public void A_null_prefetch_value_yields_nothing()
    {
        Reference("null").Should().BeNull();
    }

    private static string? Reference(string coverageJson) =>
        CrdPrefetch.ActiveCoverageReference(new CrdHookRequest
        {
            Prefetch = new Dictionary<string, JsonElement>
            {
                [CrdPrefetch.CoverageKey] = JsonDocument.Parse(coverageJson).RootElement.Clone(),
            },
        });
}
