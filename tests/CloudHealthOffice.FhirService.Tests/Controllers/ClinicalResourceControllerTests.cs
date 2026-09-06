using System.Net;
using System.Net.Http.Headers;
using FhirService.Models.PayerToPayer;
using FhirService.Services.Clinical;
using FhirService.Services.PayerToPayer.Ingestion;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

/// <summary>
/// The PAT-02 clinical read path, driven over real HTTP through the whole
/// running pipeline: JWT validation, <c>SmartScopeEnforcementMiddleware</c>,
/// <c>TenantMiddleware</c>, the globally registered
/// <c>ProviderAccessAuthorizationFilter</c>, and
/// <c>ClinicalResourceController</c> over the store an ingestion committed into.
///
/// Nothing is faked below the HTTP boundary. The data these tests read is put
/// there by <see cref="PayerToPayerPackageIngestionService"/> — the production
/// write path — so what is proven is that a Payer-to-Payer package becomes
/// readable clinical data, not that a fixture can be echoed back.
/// </summary>
public class ClinicalResourceControllerTests : IClassFixture<FhirTestWebAppFactory>
{
    private const string Tenant = "test-tenant";
    private const string Member = "pat-001";
    private const string OtherMember = "pat-002";
    private const string Payer = "PRIOR-PLAN";

    private static readonly FhirJsonParser Parser = new(new ParserSettings { PermissiveParsing = true });

    private readonly FhirTestWebAppFactory _factory;

    public ClinicalResourceControllerTests(FhirTestWebAppFactory factory)
    {
        _factory = factory;
        Seed();
    }

    // ── Patient Access: the member reading their own record ────────────────────

    [Fact]
    public async Task PatientToken_CanReadItsOwnClinicalResource()
    {
        var id = IdFor("Observation", "OBS-1", Member);

        var response = await GetAsync($"/fhir/r4/Observation/{id}",
            _factory.IssueToken("patient/Observation.read", Member));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var observation = await ParseAsync<Observation>(response);
        observation.Id.Should().Be(id);

        // The served subject is CHO's member, not the prior payer's identifier —
        // the trusted binding, applied on the way out.
        observation.Subject.Reference.Should().Be($"Patient/{Member}");
    }

    [Fact]
    public async Task PatientToken_CanSearchItsOwnClinicalResourcesWithoutNamingItself()
    {
        // No patient parameter: the token's binding supplies it, exactly as it
        // does for every other Patient Access resource.
        var response = await GetAsync("/fhir/r4/Condition",
            _factory.IssueToken("patient/Condition.read", Member));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var bundle = await ParseAsync<Bundle>(response);
        bundle.Type.Should().Be(Bundle.BundleType.Searchset);
        bundle.Entry.Should().NotBeEmpty();
        bundle.Entry.Select(e => e.Resource).OfType<Condition>()
            .Should().OnlyContain(c => c.Subject.Reference == $"Patient/{Member}");
    }

    [Fact]
    public async Task PatientToken_SearchReturnsAFhirBundleNotARawArray()
    {
        var response = await GetAsync("/fhir/r4/Observation?patient=Patient/" + Member,
            _factory.IssueToken("patient/Observation.read", Member));

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"resourceType\":\"Bundle\"").And.Contain("\"type\":\"searchset\"");
    }

    [Fact]
    public async Task PatientToken_CannotReadAnotherMembersClinicalResourceById()
    {
        // The id is real and resolves for pat-002. Knowing it must buy nothing.
        var foreignId = IdFor("Observation", "OBS-9", OtherMember);

        var response = await GetAsync($"/fhir/r4/Observation/{foreignId}",
            _factory.IssueToken("patient/Observation.read", Member));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatientToken_CannotSearchAnotherMembersClinicalResources()
    {
        var response = await GetAsync($"/fhir/r4/Observation?patient=Patient/{OtherMember}",
            _factory.IssueToken("patient/Observation.read", Member));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PatientToken_CannotReachAnotherMemberThroughSubjectEitherOfThem()
    {
        // `subject` is a second way to name a member on the clinical resources.
        // Enforcing the token binding on `patient` alone would have left this open.
        var response = await GetAsync($"/fhir/r4/Observation?subject=Patient/{OtherMember}",
            _factory.IssueToken("patient/Observation.read", Member));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MissingSmartScope_IsDeniedBeforeAnyClinicalDataIsRead()
    {
        // A Coverage scope is not an Observation scope.
        var response = await GetAsync("/fhir/r4/Observation",
            _factory.IssueToken("patient/Coverage.read", Member));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UnauthenticatedRequest_IsRefused()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/fhir/r4/Condition?patient=Patient/{Member}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Provider Access: attribution + ProviderAccess consent ──────────────────

    [Fact]
    public async Task ProviderToken_WithAttributionAndConsent_CanSearchClinicalData()
    {
        var response = await GetAsync($"/fhir/r4/Condition?patient=Patient/{Member}",
            _factory.IssueToken("user/Condition.read"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ParseAsync<Bundle>(response)).Entry.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ProviderToken_ForAMemberItIsNotAttributedTo_IsDenied()
    {
        // pat-404 is on nobody's panel. The clinical controller never runs.
        var response = await GetAsync("/fhir/r4/Condition?patient=Patient/pat-404",
            _factory.IssueToken("user/Condition.read"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ProviderToken_NamingNoMember_IsDeniedRatherThanServedTheWholeTenant()
    {
        var response = await GetAsync("/fhir/r4/Condition",
            _factory.IssueToken("user/Condition.read"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ProviderToken_DirectIdRead_WithoutAMemberContext_IsDenied()
    {
        // Provider Access authorizes a MEMBER, not an id. A direct read that
        // names no member cannot be judged for attribution or consent, so it is
        // refused — knowing the id is not a substitute.
        var id = IdFor("Observation", "OBS-1", Member);

        var response = await GetAsync($"/fhir/r4/Observation/{id}",
            _factory.IssueToken("user/Observation.read"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ProviderToken_DirectIdRead_NamingTheMemberItIsAuthorizedFor_Succeeds()
    {
        var id = IdFor("Observation", "OBS-1", Member);

        var response = await GetAsync(
            $"/fhir/r4/Observation/{id}?patient=Patient/{Member}",
            _factory.IssueToken("user/Observation.read"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ProviderToken_CannotUseOneMembersAuthorizationToReadAnothersResource()
    {
        // Attributed and consented for pat-001, asking for a resource that belongs
        // to pat-002 while naming pat-001. The store query is keyed on the
        // authorized member, so the foreign id resolves to nothing.
        var foreignId = IdFor("Observation", "OBS-9", OtherMember);

        var response = await GetAsync(
            $"/fhir/r4/Observation/{foreignId}?patient=Patient/{Member}",
            _factory.IssueToken("user/Observation.read"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnInaccessibleResourceAndANonexistentOneAreIndistinguishable()
    {
        var token = _factory.IssueToken("patient/Observation.read", Member);

        var foreignId = IdFor("Observation", "OBS-9", OtherMember);
        var absentId = new string('a', 64);

        var foreign = await GetAsync($"/fhir/r4/Observation/{foreignId}", token);
        var absent = await GetAsync($"/fhir/r4/Observation/{absentId}", token);

        foreign.StatusCode.Should().Be(absent.StatusCode);

        // Same OperationOutcome, differing only where it echoes back the id the
        // caller themselves supplied. Nothing in the response says whether the
        // resource exists for somebody else — which is the whole difference an
        // enumerating caller would be looking for.
        var foreignBody = (await foreign.Content.ReadAsStringAsync()).Replace(foreignId, "{id}");
        var absentBody = (await absent.Content.ReadAsStringAsync()).Replace(absentId, "{id}");

        foreignBody.Should().Be(absentBody,
            "telling 'someone else has this' from 'nobody has this' is what enumeration needs");
    }

    // ── Cross-tenant ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AnotherTenantsToken_SeesNoneOfThisTenantsClinicalData()
    {
        // Same member id, same resource id, different tenant in the TOKEN — which
        // is where TenantMiddleware takes the tenant from. The tenant is part of
        // every clinical store query, so there is nothing to find.
        var response = await GetAsync(
            $"/fhir/r4/Observation/{IdFor("Observation", "OBS-1", Member)}",
            _factory.IssueToken("patient/Observation.read", Member, tenantId: "other-tenant"),
            tenantHeader: "other-tenant");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnotherTenantsSearch_ReturnsAnEmptyBundleNotThisTenantsRecords()
    {
        var response = await GetAsync(
            $"/fhir/r4/Condition?patient=Patient/{Member}",
            _factory.IssueToken("patient/Condition.read", Member, tenantId: "other-tenant"),
            tenantHeader: "other-tenant");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var bundle = await ParseAsync<Bundle>(response);
        bundle.Total.Should().Be(0);
        bundle.Entry.Should().BeEmpty();
    }

    // ── Search parameter honesty ───────────────────────────────────────────────

    [Fact]
    public async Task SubjectSearch_IsRefusedForTypesFhirR4DoesNotDefineItOn()
    {
        // Immunization has `patient` and no `subject` in R4. Quietly ignoring the
        // parameter would return a Bundle that looks like an answer to a question
        // the server did not ask.
        var response = await GetAsync($"/fhir/r4/Immunization?subject=Patient/{Member}",
            _factory.IssueToken("patient/Immunization.read", Member));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ContradictoryPatientAndSubject_IsABadRequestNotAGuess()
    {
        var response = await GetAsync(
            $"/fhir/r4/Observation?patient=Patient/{Member}&subject=Patient/{OtherMember}",
            _factory.IssueToken("user/Observation.read"));

        // The SMART layer stops a patient token first; for a provider token the
        // controller refuses to pick one of the two.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task IdSearch_NarrowsToOneResourceWithinTheMember()
    {
        var id = IdFor("Condition", "CND-1", Member);

        var response = await GetAsync($"/fhir/r4/Condition?_id={id}",
            _factory.IssueToken("patient/Condition.read", Member));

        var bundle = await ParseAsync<Bundle>(response);
        bundle.Total.Should().Be(1);
        bundle.Entry.Should().ContainSingle().Which.Resource.Id.Should().Be(id);
    }

    // ── Provenance on the served resource ──────────────────────────────────────

    [Fact]
    public async Task AnImportedResource_CarriesItsOriginAndItsVersion()
    {
        var response = await GetAsync($"/fhir/r4/Condition/{IdFor("Condition", "CND-1", Member)}",
            _factory.IssueToken("patient/Condition.read", Member));

        var condition = await ParseAsync<Condition>(response);

        condition.Meta.Should().NotBeNull();
        condition.Meta.Source.Should().StartWith(ClinicalResourceProjector.ImportedSourceScheme)
            .And.Contain(Payer, "a reader must be able to tell imported data from CHO's own")
            .And.Contain("CND-1", "and trace it back to what the payer called it");
        condition.Meta.LastUpdated.Should().NotBeNull();
        condition.Meta.VersionId.Should().NotBeNullOrEmpty();

        // No profile is claimed: CHO serves valid R4 here, not re-shaped US Core.
        condition.Meta.Profile.Should().BeEmpty();
    }

    // ── Test data ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Ingests a clinical package for each member through the PRODUCTION
    /// ingestion service. Idempotent: the exchange ids are fixed, so the class
    /// fixture's shared host is seeded once however many tests run.
    /// </summary>
    private void Seed()
    {
        using var scope = _factory.Services.CreateScope();
        var ingestion = scope.ServiceProvider
            .GetRequiredService<IPayerToPayerPackageIngestionService>();

        Ingest(ingestion, Member, "exchange-clinical-pat-001", Package(Member, "OBS-1", "CND-1"));
        Ingest(ingestion, OtherMember, "exchange-clinical-pat-002", Package(OtherMember, "OBS-9", "CND-9"));
    }

    private static void Ingest(
        IPayerToPayerPackageIngestionService ingestion, string memberId, string exchangeId, Bundle bundle)
        => ingestion.IngestAsync(
            new PayerToPayerIngestionContext
            {
                TenantId = Tenant,
                MemberId = memberId,
                SourcePayerId = Payer,
                ExchangeId = exchangeId,
                RemoteMemberId = $"remote-{memberId}",
            },
            new PayerToPayerReceivedPackage { Bundle = bundle, RemoteMemberId = $"remote-{memberId}" })
            .GetAwaiter().GetResult();

    private static Bundle Package(string memberId, string observationId, string conditionId) => new()
    {
        Type = Bundle.BundleType.Collection,
        Entry =
        [
            new Bundle.EntryComponent
            {
                Resource = new Observation
                {
                    Id = observationId,
                    Status = ObservationStatus.Final,
                    Code = new CodeableConcept("http://loinc.org", "8867-4"),
                    // Deliberately the REMOTE identity: the served resource must
                    // show CHO's member instead.
                    Subject = new ResourceReference($"Patient/remote-{memberId}"),
                },
            },
            new Bundle.EntryComponent
            {
                Resource = new Condition
                {
                    Id = conditionId,
                    Code = new CodeableConcept("http://hl7.org/fhir/sid/icd-10-cm", "E11.9"),
                    Subject = new ResourceReference($"Patient/remote-{memberId}"),
                },
            },
        ],
    };

    /// <summary>
    /// The logical id CHO serves a given imported resource under — derived the
    /// same way production derives it, so the tests address real resources
    /// rather than ids the test invented.
    /// </summary>
    private static string IdFor(string resourceType, string sourceId, string memberId)
        => ClinicalResourceIdentity.ForImported(
            PayerToPayerImportPolicy.ImportKey(Tenant, memberId, Payer, resourceType, sourceId));

    [Fact]
    public async Task ATokenAndHeaderNamingDifferentTenants_IsRefusedOverHttp()
    {
        // The whole request, through the real pipeline: two statements of tenant
        // authority that disagree. Before SEC-01 the header was simply ignored
        // whenever the token carried a claim, so a mismatch went unnoticed
        // instead of being refused.
        var response = await GetAsync(
            $"/fhir/r4/Observation/{IdFor("Observation", "OBS-1", Member)}",
            _factory.IssueToken("patient/Observation.read", Member, tenantId: "other-tenant"),
            tenantHeader: Tenant);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <param name="tenantHeader">
    /// The X-Tenant-ID the request carries. Defaults to this fixture's tenant.
    /// A cross-tenant test has to move BOTH this and the token's tenant claim:
    /// since SEC-01, a header contradicting the token is refused outright as a
    /// tenant conflict (see TenantBindingTests), so leaving them disagreeing
    /// would prove the conflict check rather than the data-layer isolation the
    /// test is aiming at.
    /// </param>
    private async Task<HttpResponseMessage> GetAsync(
        string path, string token, string? tenantHeader = null)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenantHeader ?? Tenant);
        return await client.GetAsync(path);
    }

    private static async Task<T> ParseAsync<T>(HttpResponseMessage response) where T : Resource
        => Parser.Parse<T>(await response.Content.ReadAsStringAsync());
}
