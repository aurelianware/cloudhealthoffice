using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using FhirService.Services;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using CloudHealthOffice.FhirService.Tests.TestSupport;
using FhirService.Services.Pas;

namespace CloudHealthOffice.FhirService.Tests.Security;

/// <summary>
/// D-12 / G-10 / T-2. The PA rule engine selects its rule set by tenant. That
/// tenant must be the AUTHENTICATED one. It used to be read from the request
/// bundle's <c>meta.tag</c> (<c>cho:tenant</c>), so a caller in tenant A could
/// tag its $submit with tenant B and be adjudicated against B's rules.
///
/// Driven over HTTP through the real PasController and PasAutoAdjudicator;
/// only the compliance checker (not under test) and the rule engine (a
/// recorder, to see which tenant it was asked about) are replaced.
/// </summary>
public class PasRuleSetTenantTests : IClassFixture<FhirTestWebAppFactory>
{
    private const string AuthenticatedTenant = "test-tenant";
    private const string OtherTenant = "victim-tenant";

    private static readonly JsonSerializerOptions FhirJson =
        new JsonSerializerOptions().ForFhir(typeof(Patient).Assembly);

    private readonly FhirTestWebAppFactory _factory;

    public PasRuleSetTenantTests(FhirTestWebAppFactory factory) => _factory = factory;

    private sealed class RecordingRuleEngine : IPriorAuthRuleEngine
    {
        public List<string> Tenants { get; } = [];

        public Task<PaRuleDecision> EvaluateAsync(PaRuleContext context, CancellationToken ct = default)
        {
            lock (Tenants) Tenants.Add(context.TenantId);
            return Task.FromResult(new PaRuleDecision
            {
                Outcome = PaDecisionOutcome.Pend,
                FiringRuleId = "Recorder",
                FiringRuleName = "Recorder",
                ResolvedRuleSetKey = "test",
            });
        }

        public Task<IReadOnlyList<PaRuleDocument>> GetApplicableRulesAsync(RuleSetKey key, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PaRuleDocument>>([]);
    }

    private (WebApplicationFactory<Program> Host, RecordingRuleEngine Engine) Host()
    {
        var engine = new RecordingRuleEngine();
        var compliance = new Mock<ICms0057ComplianceChecker>();
        compliance.Setup(c => c.ValidateCompliance(It.IsAny<Resource>()))
            .Returns(new ComplianceResult(true, [], [],
                new ComplianceSummary("Claim", 10, 10, [], [], [], new TimelineCompliance(true))));

        var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IPriorAuthRuleEngine>();
            services.AddSingleton<IPriorAuthRuleEngine>(engine);
            services.RemoveAll<ICms0057ComplianceChecker>();
            services.AddSingleton(compliance.Object);

            // $submit now stores the request before it answers (D-01): stand
            // in for authorization-service, and keep the gold-card and
            // provider-verification lookups (now reached, because the
            // complete bundle names an NPI) off the network.
            services.RemoveAll<IPasAuthorizationRecorder>();
            services.AddSingleton<IPasAuthorizationRecorder>(new FakePasAuthorizationRecorder());
            services.AddHttpClient("AuthorizationService")
                .ConfigurePrimaryHttpMessageHandler(() => new NotFoundHandler());
            services.AddHttpClient("ProviderVerificationService")
                .ConfigurePrimaryHttpMessageHandler(() => new NotFoundHandler());
        }));
        return (host, engine);
    }

    /// <summary>
    /// A PAS bundle that reaches the rule engine: a procedure on no auto list,
    /// no cost. It is now a complete bundle (Patient, Coverage, Practitioner
    /// with NPI), because $submit refuses a Claim whose referenced resources
    /// are missing (D-03); the item has no location, so the enrollment gate is
    /// still skipped, and the gold-card lookup answers 404.
    /// </summary>
    private static string SubmitBundle(string? taggedTenant)
    {
        var bundle = PasBundles.Complete(procedureCode: "70553");
        if (taggedTenant != null)
            bundle.Meta = new Meta { Tag = [new Coding("cho:tenant", taggedTenant)] };

        return JsonSerializer.Serialize(bundle, FhirJson);
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private async Task<HttpResponseMessage> SubmitAsync(WebApplicationFactory<Program> host, string body)
    {
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/fhir/r4/Claim/$submit")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/fhir+json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.IssueToken("system/Claim.write", tenantId: AuthenticatedTenant));
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Submit_TaggedWithAnotherTenant_IsAdjudicatedAgainstTheAuthenticatedTenantsRules()
    {
        var (host, engine) = Host();

        var response = await SubmitAsync(host, SubmitBundle(taggedTenant: OtherTenant));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        engine.Tenants.Should().ContainSingle()
            .Which.Should().Be(AuthenticatedTenant,
                "the rule set is chosen by the authenticated tenant, never by a tag in the request body");
    }

    [Fact]
    public async Task Submit_WithNoTenantTag_StillUsesTheAuthenticatedTenantsRules()
    {
        // Before, an untagged bundle evaluated with an EMPTY tenant, i.e. only
        // platform rules, whatever the caller's tenant had configured.
        var (host, engine) = Host();

        var response = await SubmitAsync(host, SubmitBundle(taggedTenant: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        engine.Tenants.Should().ContainSingle().Which.Should().Be(AuthenticatedTenant);
    }
}
