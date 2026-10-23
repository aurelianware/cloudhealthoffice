using System.Net;
using System.Text;
using ClaimsService.Repositories;
using Cms0057Acceptance.Tests.TestSupport;
using CloudHealthOffice.Consent.Contracts;
using FhirService.Services.Consent;
using FhirService.Services.PayerToPayer;
using FhirService.Services.ProviderAccess;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// PROV-02 — Provider Access is confined to the provider's own patients, and
/// the relationship is now evidenced by the plan's own claims rather than
/// declared in a configuration file.
///
/// The path these scenarios drive:
///
///   fhir-service   ProviderAccessAuthorizationService   (the composed decision)
///        │         CompositeProviderAttributionSource
///        │         ├── ConfiguredProviderAttributionSource   (panels)
///        │         └── ClaimsHistoryProviderAttributionSource
///        │ HTTP: GET /api/provider-attribution?provider=&amp;member=&amp;since=
///   claims-service    ProviderAttributionController → IProviderAttributionRepository
///
/// Every component on it is the real one, and the hop between the services is
/// a real HTTP request answered by claims-service's own controller. Only the
/// Mongo repository is a stand-in; the attribution RULE it implements —
/// billing or rendering NPI, current chain head, not voided, inside the
/// lookback — is proved against a real mongod in
/// <c>CloudHealthOffice.ClaimsService.Tests</c>'s
/// <c>ProviderAttributionRepositoryTests</c>. Synthetic data only.
///
/// What this replaces: PROV-02's original acceptance test asked the mock data
/// layer for a member id that does not exist, so "not attributed" and "no such
/// member" were the same assertion (§6.1, D-30). These scenarios distinguish
/// them: the negative cases are a provider who treated a DIFFERENT member of
/// the same plan, and a provider whose only treatment of this member falls
/// outside the window — both real members, both refused.
///
/// Traceability:
///   compose      src/services/fhir-service/Services/ProviderAccess/ProviderAccessAuthorizationService.cs
///   sources      src/services/fhir-service/Services/ProviderAccess/ProviderAttribution.cs
///                src/services/fhir-service/Services/ProviderAccess/ClaimsHistoryProviderAttributionSource.cs
///                src/services/fhir-service/Services/ProviderAccess/CompositeProviderAttributionSource.cs
///   answer       src/services/claims-service/Controllers/ProviderAttributionController.cs
///   rule         src/services/claims-service/Repositories/ProviderAttributionRepositoryMongo.cs
/// </summary>
public class ProviderAccessAttributionTests
{
    private const string Treated = "MEM-TREATED";
    private const string Untreated = "MEM-UNTREATED";
    private const string Panelled = "MEM-PANELLED";
    private const string Provider = "1234567893";

    // ── The relationship comes from claims, not from a panel ─────────────────

    [Fact]
    [Trait("Scenario", "PROV-02")]
    [Trait("Backend", "Replace")]
    public async Task PROV02_Replace_AProviderWhoTreatedTheMemberIsAttributedFromClaimsAlone()
    {
        // No panel at all: the only thing that can attribute this provider is
        // the plan's own record of them treating the member.
        var path = new Path(treated: [(Provider, Treated)]);

        var decision = await path.Service().AuthorizeAsync(Request(Treated));

        decision.Attributed.Should().BeTrue();
        decision.Allowed.Should().BeTrue();

        // And it really asked claims-service, naming both parties and a floor.
        path.Upstream.Should().ContainSingle()
            .Which.Should().StartWith("/api/provider-attribution?")
            .And.Contain("provider=" + Provider)
            .And.Contain("member=" + Treated)
            .And.Contain("since=");
    }

    [Fact]
    [Trait("Scenario", "PROV-02")]
    [Trait("Backend", "Replace")]
    public async Task PROV02_Replace_AProviderWhoTreatedADifferentMemberOfThisPlanIsRefused()
    {
        // The negative case the original test could not make: a real provider
        // with real patients in this plan, asking about a real member who is
        // not one of them. Nothing here is missing — only the relationship.
        var path = new Path(treated: [(Provider, Treated)]);

        var decision = await path.Service().AuthorizeAsync(Request(Untreated));

        decision.Allowed.Should().BeFalse();
        decision.Attributed.Should().BeFalse();
        decision.Reason.Should().Be(ProviderAccessDenialReason.NotAttributed);
    }

    [Fact]
    [Trait("Scenario", "PROV-02")]
    [Trait("Backend", "Replace")]
    public async Task PROV02_Replace_TreatmentOlderThanTheLookbackDoesNotAttribute()
    {
        // The window is the policy fhir-service owns, and it reaches
        // claims-service as an absolute instant. This upstream answers true
        // only for treatment inside the window it was given, so a lookback the
        // caller shortens really does narrow who is attributed.
        var path = new Path(
            treated: [(Provider, Treated)],
            treatedAt: DateTime.UtcNow.AddMonths(-30));

        // 24 months: the treatment is outside it.
        (await path.Service().AuthorizeAsync(Request(Treated)))
            .Attributed.Should().BeFalse();

        // 36 months: the same treatment, now inside.
        (await path.Service(lookbackMonths: 36).AuthorizeAsync(Request(Treated)))
            .Attributed.Should().BeTrue();
    }

    // ── Panels and claims compose; neither subsumes the other ────────────────

    [Fact]
    [Trait("Scenario", "PROV-02")]
    [Trait("Backend", "Replace")]
    public async Task PROV02_Replace_APanelAssignmentStillAttributesWithoutAnyClaim()
    {
        // A member assigned to a PCP they have not yet seen. No claim can show
        // that relationship, which is why claims history is added to panels
        // rather than put in their place.
        var path = new Path(treated: []);

        var decision = await path.Service(panel: [Panelled]).AuthorizeAsync(Request(Panelled));

        decision.Attributed.Should().BeTrue();
    }

    [Fact]
    [Trait("Scenario", "PROV-02")]
    [Trait("Backend", "Replace")]
    public async Task PROV02_Replace_ClaimsHistoryAttributesAProviderNoPanelNames()
    {
        // The other direction: the specialist who operated last month and is
        // on no panel CHO was given. The panel is loaded and simply does not
        // mention this member.
        var path = new Path(treated: [(Provider, Treated)]);

        var decision = await path.Service(panel: [Panelled]).AuthorizeAsync(Request(Treated));

        decision.Attributed.Should().BeTrue();
    }

    // ── Fail-closed ──────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PROV-02")]
    [Trait("Backend", "Replace")]
    public async Task PROV02_Replace_AnUnreadableClaimsHistoryRefusesRatherThanFallingBackToThePanel()
    {
        // The failure that would be invisible if the source answered false: an
        // unreachable claims-service must not quietly leave attribution
        // resting on whatever the configuration file happened to say. The
        // member IS on the panel, and the request is still refused.
        var path = new Path(treated: [(Provider, Panelled)]) { Unavailable = true };

        var decision = await path.Service(panel: [Panelled]).AuthorizeAsync(Request(Panelled));

        decision.Allowed.Should().BeFalse();
        decision.Attributed.Should().BeFalse();
        decision.Reason.Should().Be(ProviderAccessDenialReason.NotAttributed);
    }

    [Fact]
    [Trait("Scenario", "PROV-02")]
    [Trait("Backend", "Replace")]
    public async Task PROV02_Replace_AttributionDoesNotSubstituteForTheMembersOptOut()
    {
        // The controls stay independent (CONSENT-01, D-11). Treatment the plan
        // can prove does not override the member's refusal to share it.
        var path = new Path(treated: [(Provider, Treated)]);

        var decision = await path.Service(optedOut: true).AuthorizeAsync(Request(Treated));

        decision.Attributed.Should().BeTrue("the treatment relationship is real");
        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ProviderAccessDenialReason.ConsentDenied);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static ProviderAccessRequest Request(string member) => new()
    {
        TenantId = AcceptanceContext.TenantId,
        MemberId = member,
        ProviderId = Provider,
    };

    /// <summary>
    /// The real composed decision over the real attribution sources, with a
    /// real HTTP hop into claims-service's own controller.
    /// </summary>
    private sealed class Path
    {
        private readonly HashSet<(string Provider, string Member)> _treated;
        private readonly DateTime _treatedAt;

        public Path(
            IEnumerable<(string Provider, string Member)> treated,
            DateTime? treatedAt = null)
        {
            _treated = treated.ToHashSet();
            _treatedAt = treatedAt ?? DateTime.UtcNow.AddMonths(-3);
        }

        /// <summary>claims-service cannot be read.</summary>
        public bool Unavailable { get; init; }

        /// <summary>The upstream requests the claims-history source made.</summary>
        public List<string> Upstream { get; } = [];

        public IProviderAccessAuthorizationService Service(
            IEnumerable<string>? panel = null,
            int lookbackMonths = 24,
            bool optedOut = false)
        {
            var options = Options.Create(new ProviderAttributionOptions
            {
                PanelsByTenant = new()
                {
                    [AcceptanceContext.TenantId] =
                    [
                        new ConfiguredProviderPanel
                        {
                            ProviderId = Provider,
                            MemberIds = (panel ?? []).ToList(),
                        }
                    ],
                },
                ClaimsHistory = new ClaimsHistoryAttributionOptions
                {
                    Enabled = true,
                    LookbackMonths = lookbackMonths,
                },
            });

            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(new ClaimsServiceHandler(this))
                {
                    BaseAddress = new Uri("http://claims-service.test/"),
                });

            var attribution = new CompositeProviderAttributionSource(
                new IProviderAttributionSource[]
                {
                    new ConfiguredProviderAttributionSource(options),
                    new ClaimsHistoryProviderAttributionSource(
                        factory.Object, options,
                        NullLogger<ClaimsHistoryProviderAttributionSource>.Instance),
                });

            var consents = optedOut
                ? new List<ConfiguredConsentRecord>
                {
                    new()
                    {
                        MemberId = Treated,
                        ConsentId = "opt-out",
                        PurposeOfUse = ConsentPurposeOfUse.ProviderAccess,
                        Status = ConsentLifecycleStatus.Active,
                        Provision = ConsentProvision.Deny,
                    },
                }
                : [];

            var consentSource = new ConfiguredPayerToPayerConsentSource(
                Options.Create(new PayerToPayerConsentOptions
                {
                    ConsentsByTenant = new() { [AcceptanceContext.TenantId] = consents },
                }));

            return new ProviderAccessAuthorizationService(
                attribution,
                new RegistryConsentEvaluator(
                    consentSource, AcceptanceContext.Logger<RegistryConsentEvaluator>()),
                AcceptanceContext.Logger<ProviderAccessAuthorizationService>());
        }

        /// <summary>
        /// claims-service's own controller, over a repository that applies the
        /// same question the Mongo filter does: has this provider treated this
        /// member on or after the floor the caller named?
        /// </summary>
        internal ClaimsService.Controllers.ProviderAttributionController Controller()
        {
            var repository = new Mock<IProviderAttributionRepository>();
            repository
                .Setup(r => r.HasTreatedAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string tenantId, string provider, string member, DateTime since, CancellationToken _) =>
                {
                    tenantId.Should().Be(
                        AcceptanceContext.TenantId, "the upstream filters by the tenant it was told");
                    return _treated.Contains((provider, member)) && _treatedAt >= since;
                });

            var http = new DefaultHttpContext();
            http.Items["TenantId"] = AcceptanceContext.TenantId;
            return new ClaimsService.Controllers.ProviderAttributionController(
                repository.Object,
                NullLogger<ClaimsService.Controllers.ProviderAttributionController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };
        }
    }

    /// <summary>Answers the attribution call with claims-service's controller.</summary>
    private sealed class ClaimsServiceHandler(Path path) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            path.Upstream.Add(uri.PathAndQuery);

            if (path.Unavailable)
                throw new HttpRequestException("claims-service is unreachable");

            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            var result = await path.Controller().IsAttributed(
                query.TryGetValue("provider", out var p) ? p.ToString() : null,
                query.TryGetValue("member", out var m) ? m.ToString() : null,
                query.TryGetValue("since", out var s)
                    ? DateTime.Parse(s.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind)
                    : null,
                ct);

            return result switch
            {
                ObjectResult obj => new HttpResponseMessage((HttpStatusCode)(obj.StatusCode ?? 200))
                {
                    Content = new StringContent(
                        System.Text.Json.JsonSerializer.Serialize(obj.Value, Json),
                        Encoding.UTF8, "application/json"),
                },
                _ => throw new InvalidOperationException(result.GetType().Name),
            };
        }

        /// <summary>camelCase, as the services' shared JSON options produce.</summary>
        private static readonly System.Text.Json.JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        };
    }
}
