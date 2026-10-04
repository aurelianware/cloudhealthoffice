using BenefitPlanService.Models;
using BenefitPlanService.Services;
using CloudHealthOffice.Infrastructure.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.BenefitPlanService.Tests;

public class PlanDocumentLocationPolicyTests
{
    private static PlanDocumentLocationPolicy PolicyFor(params string[] hosts) =>
        new(Options.Create(new PlanDocumentLocationOptions { AllowedDocumentHosts = hosts.ToList() }),
            NullLogger<PlanDocumentLocationPolicy>.Instance);

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://docs.payer.example/sbc.pdf")]
    [InlineData("https://user@docs.payer.example/sbc.pdf")]
    [InlineData("https://10.1.2.3/sbc.pdf")]
    [InlineData("https://[fd00::1]/sbc.pdf")]
    [InlineData("https://localhost/sbc.pdf")]
    [InlineData("https://claims-service.cloudhealthoffice/sbc.pdf")]
    [InlineData("https://claims-service.cloudhealthoffice.svc/sbc.pdf")]
    [InlineData("https://other.example/sbc.pdf")]
    [InlineData("https://docs.payer.example /sbc.pdf")]
    [InlineData("")]
    public void Rejects(string location)
    {
        var policy = PolicyFor("docs.payer.example");

        Assert.False(policy.IsAllowed(location));
        var ex = Assert.Throws<ArgumentException>(() => policy.ValidateLocation(location, "location"));
        Assert.Equal("location", ex.ParamName);
    }

    [Fact]
    public void Internal_hosts_are_refused_even_when_listed()
    {
        var policy = PolicyFor("localhost", "member-service.cloudhealthoffice", "x.svc.cluster.local", "127.0.0.1");

        Assert.False(policy.IsAllowed("https://localhost/a.pdf"));
        Assert.False(policy.IsAllowed("https://member-service.cloudhealthoffice/a.pdf"));
        Assert.False(policy.IsAllowed("https://x.svc.cluster.local/a.pdf"));
        Assert.False(policy.IsAllowed("https://127.0.0.1/a.pdf"));
    }

    [Theory]
    [InlineData("https://docs.payer.example/sbc.pdf")]
    [InlineData("https://cdn1.files.payer.example/sbc.pdf?v=2")]
    [InlineData("documentreference/abc-123")]
    public void Accepts_allowed_hosts_and_internal_reference(string location)
    {
        Assert.True(PolicyFor("docs.payer.example", "*.files.payer.example").IsAllowed(location));
    }

    [Fact]
    public void Wildcard_does_not_match_apex()
    {
        Assert.False(PolicyFor("*.files.payer.example").IsAllowed("https://files.payer.example/a.pdf"));
    }

    [Fact]
    public void Default_options_allow_no_external_host()
    {
        var policy = new PlanDocumentLocationPolicy(
            Options.Create(new PlanDocumentLocationOptions()),
            NullLogger<PlanDocumentLocationPolicy>.Instance);

        Assert.False(policy.IsAllowed("https://example.com/sbc.pdf"));
        Assert.True(policy.IsAllowed("documentreference/abc"));
    }

    [Fact]
    public void SanitizeForRead_nulls_bad_location_without_touching_good_ones()
    {
        var policy = PolicyFor("docs.payer.example");
        var original = new PlanDocumentReference { Id = "bad", Location = "javascript:alert(1)" };
        var plan = new BenefitPlan
        {
            Documents = new List<PlanDocumentReference>
            {
                original,
                new() { Id = "good", Location = "https://docs.payer.example/a.pdf" },
            },
        };

        policy.SanitizeForRead(plan);

        Assert.Null(plan.Documents[0].Location);
        Assert.True(plan.Documents[0].LocationBlocked);
        Assert.Equal("https://docs.payer.example/a.pdf", plan.Documents[1].Location);
        Assert.False(plan.Documents[1].LocationBlocked);
        // The stored object is copied, not mutated.
        Assert.Equal("javascript:alert(1)", original.Location);
    }

    [Fact]
    public void LocationBlocked_is_omitted_from_json_when_false()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new PlanDocumentLink { DocType = "SBC", Location = "documentreference/a" });

        Assert.DoesNotContain("locationBlocked", json);
    }
}

/// <summary>
/// Host wiring: the allowlist binds from BenefitPlan:AllowedDocumentHosts
/// and the read-side result filter is registered globally.
/// </summary>
public class PlanDocumentLocationWiringTests : IClassFixture<ObservabilityTestFactory<Program>>
{
    private readonly ObservabilityTestFactory<Program> _factory;

    public PlanDocumentLocationWiringTests(ObservabilityTestFactory<Program> factory) => _factory = factory;

    [Fact]
    public void Allowlist_binds_from_configuration_and_filter_is_global()
    {
        using var app = _factory.WithWebHostBuilder(b =>
            b.UseSetting("BenefitPlan:AllowedDocumentHosts:0", "docs.payer.example"));
        var services = app.Services;

        var policy = services.GetRequiredService<PlanDocumentLocationPolicy>();
        Assert.True(policy.IsAllowed("https://docs.payer.example/sbc.pdf"));
        Assert.False(policy.IsAllowed("https://other.example/sbc.pdf"));

        var mvc = services.GetRequiredService<IOptions<MvcOptions>>().Value;
        Assert.Contains(mvc.Filters, f =>
            f is ServiceFilterAttribute s
            && s.ServiceType == typeof(global::BenefitPlanService.Middleware.PlanDocumentLocationResultFilter));
    }
}
