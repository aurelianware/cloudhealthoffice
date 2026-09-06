using CloudHealthOffice.Consent.Contracts;
using Cms0057Acceptance.Tests.TestSupport;
using FhirService.Controllers;
using FhirService.Models;
using FhirService.Models.PayerToPayer;
using FhirService.Services;
using FhirService.Services.Clinical;
using FhirService.Services.Consent;
using FhirService.Services.PayerToPayer;
using FhirService.Services.PayerToPayer.Ingestion;
using FhirService.Services.ProviderAccess;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// PAT-02 — the USCDI clinical resource types served through Patient and
/// Provider Access.
///
/// The scenario was PARTIAL because Cloud Health Office could receive a prior
/// payer's Condition, Observation, Procedure and the rest, but had nowhere to
/// put them and no way to serve them: they were counted as unsupported and left
/// in an archived package. These scenarios prove the whole path now exists —
/// durable member-scoped storage, provenance, the two authorization boundaries,
/// and standards-correct FHIR reads — using the PRODUCTION classes the running
/// service binds:
///
///   ingestion    PayerToPayerPackageIngestionService  (the only writer)
///   store        InMemoryPayerToPayerImportRepository as IClinicalResourceStore
///   read         ClinicalResourceService + ClinicalResourceController
///   authorize    ProviderAccessAuthorizationService + ProviderAccessAuthorizationFilter
///   advertise    MetadataController
///
/// Synthetic data only; no PHI.
///
/// Traceability:
///   inventory  src/services/fhir-service/Services/Clinical/ClinicalResourceInventory.cs
///   store      src/services/fhir-service/Services/Clinical/ClinicalResourceStore.cs
///   read       src/services/fhir-service/Services/Clinical/ClinicalResourceService.cs
///   controller src/services/fhir-service/Controllers/ClinicalResourceController.cs
///   migration  src/services/fhir-service/Services/Clinical/ClinicalBackfill.cs
/// </summary>
public class PatientAccessClinicalTests
{
    private const string Member = "pat-001";
    private const string OtherMember = "pat-002";
    private const string Provider = "provider-001";
    private const string Payer = "PRIOR-PLAN";

    private static readonly FhirJsonSerializer Serializer = new(new SerializerSettings { Pretty = false });

    // ── Inventory: what PAT-02 requires, from repository evidence ──────────────

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public void PAT02_Replace_TheClinicalInventoryCoversTheUscdiClassesTheRuleRequires()
    {
        // The USCDI clinical data classes CMS-0057-F Patient Access obliges, as
        // this repository documents them, minus those CHO already discharges
        // through Patient, Coverage, ExplanationOfBenefit and DocumentReference.
        ClinicalResourceInventory.UscdiDataClasses.Should().Contain(
        [
            "Allergies and Intolerances",
            "Assessment and Plan of Treatment",
            "Care Team Members",
            "Goals",
            "Health Concerns",
            "Immunizations",
            "Laboratory",
            "Medications",
            "Problems",
            "Procedures",
            "Smoking Status",
            "Unique Device Identifiers",
            "Vital Signs",
        ]);

        // The classes the acceptance rationale and the import policy named as the
        // PAT-02 gap are all in the inventory.
        ClinicalResourceInventory.ResourceTypes.Should().Contain(
            ["Condition", "Observation", "Procedure", "MedicationRequest", "AllergyIntolerance"]);
    }

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public async Task PAT02_Replace_EveryTypeInTheInventoryHasARealProductionReadPath()
    {
        // Data-driven over the WHOLE inventory rather than a couple of examples:
        // a type CHO advertises but cannot actually round-trip would be a false
        // claim, and adding one is exactly the mistake this catches.
        foreach (var entry in ClinicalResourceInventory.All)
        {
            var harness = await HarnessAsync(entry.ResourceType);
            var id = IdFor(entry.ResourceType, SourceId(entry.ResourceType));

            var read = await harness.Controller(PatientContext()).Read(entry.ResourceType, id, new ClinicalSearchParams(), default);
            AsResource(read).Should().NotBeNull($"{entry.ResourceType} must be readable by id");
            AsResource(read)!.TypeName.Should().Be(entry.ResourceType);

            var search = await harness.Controller(PatientContext())
                .Search(entry.ResourceType, new ClinicalSearchParams(), default);
            AsBundle(search).Entry.Should().ContainSingle($"{entry.ResourceType} must be searchable by member");
        }
    }

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public async Task PAT02_Replace_EveryServedResourceIsBoundToTheCorrectMember()
    {
        // The subject on the wire is CHO's member, taken from the trusted
        // exchange binding — not whatever the prior payer's payload asserted.
        foreach (var entry in ClinicalResourceInventory.All)
        {
            var harness = await HarnessAsync(entry.ResourceType);
            var id = IdFor(entry.ResourceType, SourceId(entry.ResourceType));

            var resource = AsResource(await harness.Controller(PatientContext())
                .Read(entry.ResourceType, id, new ClinicalSearchParams(), default))!;

            entry.ReadSubject(resource).Should().Be($"Patient/{Member}",
                $"{entry.ResourceType}.{entry.SubjectElement} must name the member CHO filed it under");
        }
    }

    // ── Patient Access ─────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public async Task PAT02_Replace_TheCorrectPatientCanReadTheirOwnClinicalResource()
    {
        var harness = await HarnessAsync("Condition");

        var result = await harness.Controller(PatientContext())
            .Read("Condition", IdFor("Condition", SourceId("Condition")), new ClinicalSearchParams(), default);

        var condition = (Condition)AsResource(result)!;
        condition.Id.Should().Be(IdFor("Condition", SourceId("Condition")));
        condition.Code.Coding[0].Code.Should().Be("E11.9", "the payer's clinical content is preserved");
    }

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public async Task PAT02_Replace_APatientCannotReadAnotherMembersResourceByItsId()
    {
        // Knowing an opaque id buys nothing: the member is part of the query.
        var harness = await HarnessAsync("Condition", alsoFor: OtherMember);

        var result = await harness.Controller(PatientContext())
            .Read("Condition", IdFor("Condition", SourceId("Condition"), member: OtherMember), new ClinicalSearchParams(), default);

        Status(result).Should().Be(404);
    }

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public async Task PAT02_Replace_APatientSearchReturnsOnlyTheirOwnRecords()
    {
        var harness = await HarnessAsync("Observation", alsoFor: OtherMember);

        var bundle = AsBundle(await harness.Controller(PatientContext())
            .Search("Observation", new ClinicalSearchParams(), default));

        bundle.Type.Should().Be(global::Hl7.Fhir.Model.Bundle.BundleType.Searchset);
        bundle.Entry.Should().ContainSingle();
        bundle.Entry.Select(e => (Observation)e.Resource)
            .Should().OnlyContain(o => o.Subject.Reference == $"Patient/{Member}");
    }

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public async Task PAT02_Replace_APatientCannotSearchForAnotherMember()
    {
        var harness = await HarnessAsync("Observation", alsoFor: OtherMember);

        var result = await harness.Controller(PatientContext())
            .Search("Observation", new ClinicalSearchParams { Patient = $"Patient/{OtherMember}" }, default);

        Status(result).Should().Be(403);
    }

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public async Task PAT02_Replace_SubjectIsNotASecondUnguardedWayToNameAnotherMember()
    {
        // Most clinical types accept `subject` as well as `patient`. Enforcing the
        // binding on one and not the other would leave the second open.
        var harness = await HarnessAsync("Observation", alsoFor: OtherMember);

        var result = await harness.Controller(PatientContext())
            .Search("Observation", new ClinicalSearchParams { Subject = $"Patient/{OtherMember}" }, default);

        Status(result).Should().Be(403);
    }

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public async Task PAT02_Replace_ARequestWithNoMemberContextIsRefusedNotServedTheTenant()
    {
        var harness = await HarnessAsync("Condition");

        var result = await harness.Controller(NoMemberContext())
            .Search("Condition", new ClinicalSearchParams(), default);

        Status(result).Should().Be(403);
    }

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public void PAT02_Replace_EveryClinicalTypeRequiresItsOwnSmartScope()
    {
        // The SMART layer builds `{context}/{Type}.read` from the resource type in
        // the path. A clinical type it did not recognise would fall through its
        // unknown-path branch and be served with no scope check at all.
        var known = (HashSet<string>)typeof(FhirService.Middleware.SmartScopeEnforcementMiddleware)
            .GetField("KnownResources",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;

        known.Should().Contain(ClinicalResourceInventory.ResourceTypes);
    }

    // ── Provider Access: the four controls from CONSENT-01 apply unchanged ─────

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public async Task PAT02_Replace_ProviderWithScopeAttributionAndConsent_MayReadClinicalData()
    {
        var decision = await ProviderDecisionAsync(
            consents: [ProviderAccessConsent()]);

        decision.Allowed.Should().BeTrue();
        decision.Attributed.Should().BeTrue();
        decision.AuthorizingConsentId.Should().Be("consent-provider-access");
    }

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public async Task PAT02_Replace_ProviderWithoutAttribution_IsDeniedClinicalData()
    {
        var decision = await ProviderDecisionAsync(
            panel: [OtherMember], consents: [ProviderAccessConsent()]);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ProviderAccessDenialReason.NotAttributed);
    }

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public async Task PAT02_Replace_ProviderWithoutProviderAccessConsent_IsDeniedClinicalData()
    {
        var decision = await ProviderDecisionAsync(consents: []);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ProviderAccessDenialReason.ConsentDenied);
    }

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public async Task PAT02_Replace_APayerToPayerConsentDoesNotOpenClinicalDataToAProvider()
    {
        // The consent that authorized the EXCHANGE that brought this data in is
        // not the consent that authorizes a provider to read it.
        var decision = await ProviderDecisionAsync(consents:
        [
            new ConfiguredConsentRecord
            {
                MemberId = Member,
                ConsentId = "consent-p2p",
                PurposeOfUse = ConsentPurposeOfUse.PayerToPayerExchange,
                Status = ConsentLifecycleStatus.Active,
            },
        ]);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(ProviderAccessDenialReason.ConsentDenied);
    }

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public async Task PAT02_Replace_ProviderAccessForTheWrongTenantOrMemberIsDenied()
    {
        (await ProviderDecisionAsync(consents: [ProviderAccessConsent()], tenant: "other-tenant"))
            .Allowed.Should().BeFalse();

        (await ProviderDecisionAsync(consents: [ProviderAccessConsent()], member: "pat-999"))
            .Allowed.Should().BeFalse();
    }

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public void PAT02_Replace_EveryClinicalTypeIsInsideTheProviderAccessBoundary()
    {
        // The structural guard that matters most: a clinical resource reachable
        // through SMART but absent from the governed set would be readable by any
        // provider with a scope, attributed or not, consented or not.
        ProviderAccessAuthorizationFilter.GovernedResources
            .Should().Contain(ClinicalResourceInventory.ResourceTypes);

        // And the member-naming parameters the clinical searches use are ones the
        // filter can resolve a member from — otherwise a legitimate provider
        // search is refused as member-less.
        var filterParameters = (string[])typeof(ProviderAccessAuthorizationFilter)
            .GetField("MemberSearchParameters",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;

        filterParameters.Should().Contain(ClinicalResourceInventory.MemberSearchParameters);
    }

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public void PAT02_Replace_NoClinicalControllerBypassesTheSharedAuthorizationBoundary()
    {
        // Clinical reads go through the ONE controller whose routes the inventory
        // constrains, which the global filter governs like every other
        // member-scoped resource. A second clinical controller is exactly the way
        // that boundary would be escaped.
        var clinicalControllers = AcceptanceContext.ProductTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t)
                     && !t.IsAbstract
                     && t.Name.Contains("Clinical", StringComparison.OrdinalIgnoreCase))
            .ToList();

        clinicalControllers.Should().ContainSingle()
            .Which.Should().Be(typeof(ClinicalResourceController));
    }

    // ── Freshness: committed exchange state only ───────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public async Task PAT02_Replace_AnUncommittedExchangesClinicalDataIsNotServed()
    {
        var store = new InMemoryPayerToPayerImportRepository();

        var ledger = await store.OpenLedgerAsync(new PayerToPayerImportLedgerEntry
        {
            ExchangeId = "exchange-staged",
            TenantId = AcceptanceContext.TenantId,
            MemberId = Member,
            SourcePayerId = Payer,
        });
        await store.StageAsync([Row("exchange-staged", Sample("Condition", SourceId("Condition")))]);
        ledger.Status.Should().Be(PayerToPayerIngestionStatus.Staging);

        var controller = ControllerFor(store, PatientContext());

        Status(await controller.Read("Condition", IdFor("Condition", SourceId("Condition")), new ClinicalSearchParams(), default)).Should().Be(404);

        // ...and once the exchange commits, the same read succeeds.
        await store.CommitAsync(ledger);
        AsResource(await ControllerFor(store, PatientContext())
            .Read("Condition", IdFor("Condition", SourceId("Condition")), new ClinicalSearchParams(), default)).Should().NotBeNull();
    }

    // ── Provenance ─────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public async Task PAT02_Replace_ImportedClinicalDataIsNotIndistinguishableFromChoAuthoredData()
    {
        var harness = await HarnessAsync("Condition");

        var meta = AsResource(await harness.Controller(PatientContext())
            .Read("Condition", IdFor("Condition", SourceId("Condition")), new ClinicalSearchParams(), default))!.Meta;

        meta.Source.Should().StartWith(ClinicalResourceProjector.ImportedSourceScheme)
            .And.Contain(Payer)
            .And.Contain(SourceId("Condition"));
        meta.LastUpdated.Should().NotBeNull();
        meta.VersionId.Should().NotBeNullOrEmpty();

        // No profile conformance is asserted — CHO serves valid R4 here and does
        // not re-shape a prior payer's content to satisfy US Core invariants.
        meta.Profile.Should().BeEmpty();
    }

    // ── CapabilityStatement ────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public void PAT02_Replace_TheCapabilityStatementAdvertisesExactlyWhatIsServed()
    {
        var controller = new MetadataController(AcceptanceContext.DemoConfig()).WithTenant();
        var statement = (CapabilityStatement)((OkObjectResult)controller.GetCapabilityStatement()).Value!;

        var advertised = statement.Rest[0].Resource
            .Where(r => ClinicalResourceInventory.IsClinical(r.Type))
            .ToList();

        advertised.Select(r => r.Type).Should().BeEquivalentTo(ClinicalResourceInventory.ResourceTypes);

        foreach (var resource in advertised)
        {
            var entry = ClinicalResourceInventory.Find(resource.Type)!;

            resource.Interaction.Select(i => i.Code).Should().BeEquivalentTo(
            [
                CapabilityStatement.TypeRestfulInteraction.Read,
                CapabilityStatement.TypeRestfulInteraction.SearchType,
            ]);
            resource.SearchParam.Select(p => p.Name).Should().BeEquivalentTo(entry.SearchParameters);
            resource.SupportedProfile.Should().BeEmpty("no profile is claimed that is not validated");
        }
    }

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public void PAT02_Replace_NoUnsupportedResourceIsAdvertisedAsSupported()
    {
        var controller = new MetadataController(AcceptanceContext.DemoConfig()).WithTenant();
        var statement = (CapabilityStatement)((OkObjectResult)controller.GetCapabilityStatement()).Value!;

        // A type CHO still archives rather than serves must not appear.
        statement.Rest[0].Resource.Select(r => r.Type)
            .Should().NotContain(["RiskAssessment", "NutritionOrder", "FamilyMemberHistory"]);
    }

    // ── Unsupported handling survives ──────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public async Task PAT02_Replace_ATypeOutsideTheInventoryIsStillNamedAndArchivedNotDropped()
    {
        var store = new InMemoryPayerToPayerImportRepository();

        var result = await Ingestion(store).IngestAsync(
            Context("exchange-1"),
            Package(Sample("Condition", SourceId("Condition")),
                    new RiskAssessment { Id = "RSK-1", Status = ObservationStatus.Final }));

        result.Counts.Clinical.Should().Be(1);
        result.Counts.Unsupported.Should().Be(1);
        result.Counts.UnsupportedTypes.Should().BeEquivalentTo(new[] { "RiskAssessment" });

        var ledger = await store.GetLedgerAsync(AcceptanceContext.TenantId, "exchange-1");
        ledger!.ArchivedPackageJson.Should().Contain("RiskAssessment");
    }

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public async Task PAT02_Replace_AMalformedClinicalResourceIsRefusedNotStored()
    {
        var store = new InMemoryPayerToPayerImportRepository();

        // No id: there is no source identity to key it on, so it has nowhere
        // deterministic to live and is refused by name rather than dropped.
        var result = await Ingestion(store).IngestAsync(
            Context("exchange-1"),
            Package(Sample("Condition", SourceId("Condition")),
                    new Observation { Status = ObservationStatus.Final, Code = new CodeableConcept("s", "c") }));

        result.Counts.Clinical.Should().Be(1);
        (await store.SearchAsync(new ClinicalResourceQuery
        {
            TenantId = AcceptanceContext.TenantId, MemberId = Member, ResourceType = "Observation",
        })).Total.Should().Be(0);
    }

    // ── Member binding comes from trusted context ──────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public async Task PAT02_Replace_AHostilePackageCannotFileClinicalDataUnderAnotherMember()
    {
        var store = new InMemoryPayerToPayerImportRepository();

        var hostile = new Condition
        {
            Id = SourceId("Condition"),
            Code = new CodeableConcept("http://hl7.org/fhir/sid/icd-10-cm", "E11.9"),
            Subject = new ResourceReference($"Patient/{OtherMember}"),
        };

        await Ingestion(store).IngestAsync(Context("exchange-1"), Package(hostile));

        // Filed under the member the exchange resolved...
        (await store.SearchAsync(new ClinicalResourceQuery
        {
            TenantId = AcceptanceContext.TenantId, MemberId = Member, ResourceType = "Condition",
        })).Total.Should().Be(1);

        // ...and not under the one the payload named.
        (await store.SearchAsync(new ClinicalResourceQuery
        {
            TenantId = AcceptanceContext.TenantId, MemberId = OtherMember, ResourceType = "Condition",
        })).Total.Should().Be(0);

        // Nor is it SERVED to that member, whatever the payload said.
        Status(await ControllerFor(store, PatientContext(OtherMember))
            .Read("Condition", IdFor("Condition", SourceId("Condition")), new ClinicalSearchParams(), default)).Should().Be(404);
    }

    // ── Audit ──────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public void PAT02_Replace_TheClinicalAccessContextCarriesNoClinicalContent()
    {
        // What the audit line can say is bounded by what the context can hold:
        // ids, a category and an instant. There is no field for an observation
        // value, a diagnosis, a medication name, a narrative, or a token.
        var properties = typeof(ClinicalAccessContext).GetProperties().Select(p => p.Name).ToList();

        properties.Should().Contain(["TenantId", "AuthorizedMemberId", "CallerId"]);
        properties.Should().NotContain(n =>
            n.Contains("Value", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Code", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Diagnos", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Medication", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Note", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Payload", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Token", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Resource", StringComparison.OrdinalIgnoreCase));
    }

    // ── Migration ──────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-02")]
    [Trait("Backend", "Replace")]
    public async Task PAT02_Replace_ClinicalDataImportedBeforeThisFeatureBecomesReadableWithoutReRunningTheExchange()
    {
        // A member's prior-payer history that CHO already holds must not require
        // asking that payer for it again — which would be impossible once the
        // payer relationship has ended.
        var store = new InMemoryPayerToPayerImportRepository();

        // The pre-feature world: the package archived, no clinical rows staged.
        var ledger = await store.OpenLedgerAsync(new PayerToPayerImportLedgerEntry
        {
            ExchangeId = "exchange-legacy",
            TenantId = AcceptanceContext.TenantId,
            MemberId = Member,
            SourcePayerId = Payer,
            ArchivedPackageJson = Serializer.SerializeToString(Package(Sample("Condition", SourceId("Condition"))).Bundle),
        });
        await store.CommitAsync(ledger);

        Status(await ControllerFor(store, PatientContext())
            .Read("Condition", IdFor("Condition", SourceId("Condition")), new ClinicalSearchParams(), default)).Should().Be(404);

        await new ClinicalBackfillService(
                store, store, new ClinicalPayloadValidator(),
                Options.Create(new ClinicalBackfillOptions { Enabled = true }),
                AcceptanceContext.Logger<ClinicalBackfillService>())
            .RunAsync();

        AsResource(await ControllerFor(store, PatientContext())
            .Read("Condition", IdFor("Condition", SourceId("Condition")), new ClinicalSearchParams(), default))
            .Should().NotBeNull("data CHO already held becomes readable in place");
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    private sealed record Harness(InMemoryPayerToPayerImportRepository Store)
    {
        public ClinicalResourceController Controller(ClinicalCallContext context)
            => ControllerFor(Store, context);
    }

    /// <summary>
    /// Ingests one sample resource of the given type for the member (and
    /// optionally a second member), through the production ingestion service.
    /// </summary>
    private static async Task<Harness> HarnessAsync(string resourceType, string? alsoFor = null)
    {
        var store = new InMemoryPayerToPayerImportRepository();
        var ingestion = Ingestion(store);

        await ingestion.IngestAsync(
            Context("exchange-1"), Package(Sample(resourceType, SourceId(resourceType))));

        if (alsoFor is not null)
        {
            await ingestion.IngestAsync(
                Context("exchange-2", member: alsoFor),
                Package(Sample(resourceType, SourceId(resourceType))));
        }

        return new Harness(store);
    }

    private static IPayerToPayerPackageIngestionService Ingestion(IPayerToPayerImportRepository store)
        => new PayerToPayerPackageIngestionService(
            store,
            AcceptanceContext.Logger<PayerToPayerPackageIngestionService>(),
            new ClinicalPayloadValidator());

    private static ClinicalResourceController ControllerFor(
        IClinicalResourceStore store, ClinicalCallContext context)
    {
        var service = new ClinicalResourceService(
            store,
            new ClinicalResourceProjector(),
            AcceptanceContext.Logger<ClinicalResourceService>());

        var controller = new ClinicalResourceController(
            service, new FhirBundleBuilder(AcceptanceContext.DemoConfig()));

        var http = new DefaultHttpContext();
        http.Items["TenantId"] = context.TenantId;
        if (context.SmartPatientId is not null) http.Items["SmartPatientId"] = context.SmartPatientId;
        http.Items["SmartScopes"] = context.Scopes;
        http.Request.QueryString = context.Query;

        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>The request facts the middleware and the filter would have established.</summary>
    private sealed record ClinicalCallContext(
        string TenantId, string? SmartPatientId, HashSet<string> Scopes, QueryString Query);

    private static ClinicalCallContext PatientContext(string member = Member, string? tenant = null)
        => new(tenant ?? AcceptanceContext.TenantId, member, ["patient/*.read"], QueryString.Empty);

    /// <summary>A caller whose request established no member at all.</summary>
    private static ClinicalCallContext NoMemberContext()
        => new(AcceptanceContext.TenantId, null, ["user/*.read"], QueryString.Empty);

    private static async Task<ProviderAccessDecision> ProviderDecisionAsync(
        IEnumerable<string>? panel = null,
        IEnumerable<ConfiguredConsentRecord>? consents = null,
        string tenant = AcceptanceContext.TenantId,
        string member = Member)
    {
        var attribution = new ConfiguredProviderAttributionSource(
            Options.Create(new ProviderAttributionOptions
            {
                PanelsByTenant = new()
                {
                    [AcceptanceContext.TenantId] =
                    [
                        new ConfiguredProviderPanel
                        {
                            ProviderId = Provider,
                            MemberIds = (panel ?? [Member]).ToList(),
                        },
                    ],
                },
            }));

        var service = new ProviderAccessAuthorizationService(
            attribution,
            AcceptanceContext.ConsentEvaluatorFor((consents ?? []).ToArray()),
            AcceptanceContext.Logger<ProviderAccessAuthorizationService>());

        return await service.AuthorizeAsync(new ProviderAccessRequest
        {
            TenantId = tenant,
            MemberId = member,
            ProviderId = Provider,
        });
    }

    private static ConfiguredConsentRecord ProviderAccessConsent() => new()
    {
        MemberId = Member,
        ConsentId = "consent-provider-access",
        PurposeOfUse = ConsentPurposeOfUse.ProviderAccess,
        Status = ConsentLifecycleStatus.Active,
    };

    // ── Fixtures ───────────────────────────────────────────────────────────────

    private static PayerToPayerIngestionContext Context(string exchangeId, string member = Member) => new()
    {
        TenantId = AcceptanceContext.TenantId,
        MemberId = member,
        SourcePayerId = Payer,
        ExchangeId = exchangeId,
        RemoteMemberId = "remote-1",
    };

    private static PayerToPayerReceivedPackage Package(params Resource[] resources) => new()
    {
        RemoteMemberId = "remote-1",
        Bundle = new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry = [.. resources.Select(r => new Bundle.EntryComponent { Resource = r })],
        },
    };

    private static string SourceId(string resourceType) => $"SRC-{resourceType}";

    private static string IdFor(string resourceType, string sourceId, string member = Member)
        => ClinicalResourceIdentity.ForImported(PayerToPayerImportPolicy.ImportKey(
            AcceptanceContext.TenantId, member, Payer, resourceType, sourceId));

    /// <summary>
    /// A minimally valid instance of any clinical type, with its subject set to
    /// the REMOTE member so the binding rewrite is exercised on every one.
    /// </summary>
    private static Resource Sample(string resourceType, string id)
    {
        var entry = ClinicalResourceInventory.Find(resourceType)
            ?? throw new ArgumentException($"{resourceType} is not a clinical type", nameof(resourceType));

        Resource resource = resourceType switch
        {
            "Condition" => new Condition
            {
                Code = new CodeableConcept("http://hl7.org/fhir/sid/icd-10-cm", "E11.9"),
            },
            "Observation" => new Observation
            {
                Status = ObservationStatus.Final,
                Code = new CodeableConcept("http://loinc.org", "8867-4"),
            },
            "Procedure" => new Procedure { Status = EventStatus.Completed },
            "MedicationRequest" => new MedicationRequest
            {
                Status = MedicationRequest.MedicationrequestStatus.Active,
                Intent = MedicationRequest.MedicationRequestIntent.Order,
            },
            "MedicationDispense" => new MedicationDispense
            {
                Status = MedicationDispense.MedicationDispenseStatusCodes.Completed,
            },
            "AllergyIntolerance" => new AllergyIntolerance(),
            "Immunization" => new Immunization { Status = Immunization.ImmunizationStatusCodes.Completed },
            "DiagnosticReport" => new DiagnosticReport
            {
                Status = DiagnosticReport.DiagnosticReportStatus.Final,
                Code = new CodeableConcept("http://loinc.org", "58410-2"),
            },
            "CarePlan" => new CarePlan
            {
                Status = RequestStatus.Active,
                Intent = CarePlan.CarePlanIntent.Plan,
            },
            "CareTeam" => new CareTeam { Status = CareTeam.CareTeamStatus.Active },
            "Goal" => new Goal
            {
                LifecycleStatus = Goal.GoalLifecycleStatus.Active,
                // Text only: a Goal needs a description, and the acceptance
                // scenarios turn on member binding and authorization, not on a
                // coded goal. `Text` says that without passing nulls for a
                // system and a code the fixture does not have.
                Description = new CodeableConcept { Text = "Lower A1c" },
            },
            "Device" => new Device(),
            _ => throw new ArgumentException(
                $"{resourceType} is in the inventory but this fixture has no sample for it — "
                + "add one so the data-driven scenarios keep covering the whole inventory.",
                nameof(resourceType)),
        };

        resource.Id = id;
        entry.BindSubject(resource, new ResourceReference("Patient/remote-1"));
        return resource;
    }

    private static ImportedFhirResource Row(string exchangeId, Resource resource)
    {
        var json = Serializer.SerializeToString(resource);
        return new ImportedFhirResource
        {
            ImportKey = PayerToPayerImportPolicy.ImportKey(
                AcceptanceContext.TenantId, Member, Payer, resource.TypeName, resource.Id!),
            TenantId = AcceptanceContext.TenantId,
            MemberId = Member,
            SourcePayerId = Payer,
            ExchangeId = exchangeId,
            ResourceType = resource.TypeName,
            SourceResourceId = resource.Id!,
            RemoteMemberId = "remote-1",
            Classification = ImportedResourceClass.ClinicalRecord,
            ResourceJson = json,
            ContentHash = PayerToPayerImportPolicy.ContentHash(json),
        };
    }

    // ── Result readers ─────────────────────────────────────────────────────────

    private static Resource? AsResource(IActionResult result)
        => (result as OkObjectResult)?.Value as Resource;

    private static Bundle AsBundle(IActionResult result)
        => (Bundle)((OkObjectResult)result).Value!;

    private static int Status(IActionResult result) => result switch
    {
        OkObjectResult => 200,
        ObjectResult o => o.StatusCode ?? 0,
        StatusCodeResult s => s.StatusCode,
        _ => 0,
    };
}
