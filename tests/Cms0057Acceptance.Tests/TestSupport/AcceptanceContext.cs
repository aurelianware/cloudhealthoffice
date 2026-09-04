using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cms0057Acceptance.Tests.TestSupport;

/// <summary>
/// Shared helpers for the CMS-0057-F acceptance harness. Keeps every scenario
/// in Demo/Cho mode with synthetic data only (no PHI) and provides the small
/// no-op collaborators the real services need at their edges.
/// </summary>
internal static class AcceptanceContext
{
    /// <summary>Synthetic tenant used by every scenario.</summary>
    public const string TenantId = "demo-tenant";

    public static ILogger<T> Logger<T>() => NullLogger<T>.Instance;

    /// <summary>An empty configuration (no MongoDB → services fall back to in-memory).</summary>
    public static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    /// <summary>Configuration carrying the Demo-mode SmartAuth issuer used by SEC-01.</summary>
    public static IConfiguration DemoConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SmartAuth:Issuer"] = "https://auth.cloudhealthoffice.com",
            ["SmartAuth:Audience"] = "fhir-api",
            ["Fhir:ServerBaseUrl"] = "https://api.cloudhealthoffice.com/fhir/r4",
            ["FhirAdapters:Mode"] = "Demo",
            ["FhirAdapters:DataClassification"] = "synthetic",
            ["FhirAdapters:TenantId"] = TenantId,
        }).Build();

    /// <summary>Attaches a Demo-tenant HttpContext to a controller (mirrors TenantMiddleware).</summary>
    public static T WithTenant<T>(this T controller) where T : ControllerBase
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = TenantId;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    /// <summary>
    /// Types defined in CHO product assemblies only (the *-service DLLs and
    /// CloudHealthOffice.* engines/shared libs). GAP "absence" scans must look
    /// only at product code, never at framework or NuGet assemblies that may
    /// happen to define a like-named type.
    /// </summary>
    public static IEnumerable<Type> ProductTypes()
    {
        bool IsProduct(System.Reflection.Assembly a)
        {
            var n = a.GetName().Name ?? string.Empty;
            return n.EndsWith("-service", StringComparison.Ordinal)
                || n.StartsWith("CloudHealthOffice", StringComparison.Ordinal);
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(IsProduct))
        {
            Type?[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException ex)
            {
                // A dependency failed to load: keep the types that DID load so
                // an absence-scan doesn't silently miss product types (false
                // negative). Only genuinely unreadable assemblies are skipped.
                types = ex.Types;
            }
            catch
            {
                continue;
            }

            foreach (var t in types)
            {
                if (t is not null) yield return t;
            }
        }
    }
}

/// <summary>
/// A prior-auth rule engine that reaches no conclusion (Pend). Lets scenarios
/// exercise the real CrdService card logic and CHO benefit classification
/// without standing up the full rule engine — the engine itself is covered by
/// CloudHealthOffice.PriorAuthRuleEngine.Tests.
/// </summary>
internal sealed class NoOpPriorAuthRuleEngine : IPriorAuthRuleEngine
{
    public Task<PaRuleDecision> EvaluateAsync(PaRuleContext context, CancellationToken ct = default)
        => Task.FromResult(new PaRuleDecision
        {
            Outcome = PaDecisionOutcome.Pend,
            FiringRuleId = "NoOp",
            FiringRuleName = "NoOp",
            ResolvedRuleSetKey = "acceptance",
        });

    public Task<IReadOnlyList<PaRuleDocument>> GetApplicableRulesAsync(
        RuleSetKey key, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PaRuleDocument>>([]);
}

/// <summary>
/// An <see cref="IHttpClientFactory"/> whose clients answer with a fixed status
/// and never touch the network — the acceptance suite runs offline. Downstream
/// services (terminology, provider-verification, authorization persistence) are
/// out of scope for the scenario under test and degrade gracefully in product
/// code when unreachable.
/// </summary>
internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly System.Net.HttpStatusCode _status;

    public StubHttpClientFactory(System.Net.HttpStatusCode status = System.Net.HttpStatusCode.ServiceUnavailable)
        => _status = status;

    public HttpClient CreateClient(string name) =>
        new(new FixedStatusHandler(_status)) { BaseAddress = new Uri("http://localhost.invalid/") };

    private sealed class FixedStatusHandler : HttpMessageHandler
    {
        private readonly System.Net.HttpStatusCode _status;
        public FixedStatusHandler(System.Net.HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status));
    }
}
