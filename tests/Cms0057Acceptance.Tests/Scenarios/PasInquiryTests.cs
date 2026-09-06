using System.Reflection;
using FhirService.Controllers;
using FhirService.Services;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// PAS-04 — Da Vinci PAS <c>Claim/$inquire</c>, executed against the REAL
/// PriorAuthorizationInquiryService and the REAL PasResponseBuilder projection
/// over a recording store that stands in for authorization-service.
///
/// The inquiry reads the ONE authoritative authorization record. There is no
/// inquiry-specific store and no second status field, so these tests drive the
/// same state the submit path writes and the rest of CHO updates.
///
/// Traceability:
///   route      src/services/fhir-service/Controllers/PasController.cs (Claim/$inquire)
///   lookup     src/services/fhir-service/Services/PriorAuthorizationInquiryService.cs
///   read seam  src/services/fhir-service/Services/PriorAuthorizationInquiry.cs
///   projection src/services/fhir-service/Services/PasResponseBuilder.BuildInquiryResponse
///   record     src/services/authorization-service/Models/Authorization.cs
/// </summary>
[Trait("Backend", "Replace")]
public class PasInquiryTests
{
    private const string AuthNumber = "PAS-20260906-ABCD1234";
    private const string Member = "pat-001";
    private const string ProviderNpi = "1234567890";

    // Authorization.Status values, as authorization-service defines them.
    private const int Submitted = 1;
    private const int InReview = 2;
    private const int Pended = 3;
    private const int Approved = 4;
    private const int Modified = 5;
    private const int Denied = 6;
    private const int Expired = 7;
    private const int Cancelled = 8;

    private static PriorAuthorizationRecord Record(
        int status = Submitted,
        string? reviewDecision = null,
        string tenant = AcceptanceContext.TenantId,
        string member = Member,
        string? npi = ProviderNpi,
        string? denialReasonCode = null,
        string? denialReason = null,
        string? pendReason = null) => new()
    {
        TenantId = tenant,
        Id = "auth-internal-id",
        AuthorizationNumber = AuthNumber,
        MemberId = member,
        RequestingProviderNpi = npi,
        Status = status,
        ReviewDecision = reviewDecision,
        DenialReasonCode = denialReasonCode,
        DenialReason = denialReason,
        PendReason = pendReason,
        SubmittedDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        LastUpdatedDate = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc),
        RequestedServices =
        [
            new PriorAuthorizationService { ProcedureCode = "70551", RequestedUnits = 1 }
        ],
    };

    private static PriorAuthorizationInquiryRequest Request(
        string? authNumber = AuthNumber,
        string? member = Member,
        string? npi = null,
        string? tenant = null) => new()
    {
        TenantId = tenant ?? AcceptanceContext.TenantId,
        AuthorizationNumber = authNumber,
        MemberReference = member,
        RequestingProviderNpi = npi,
    };

    private static (IPriorAuthorizationInquiryService Service, RecordingStore Store) ServiceOver(
        params PriorAuthorizationRecord[] records)
    {
        var store = new RecordingStore(records);
        return (new PriorAuthorizationInquiryService(store), store);
    }

    // ── Happy path across the lifecycle ─────────────────────────────────────────

    [Theory]
    [Trait("Scenario", "PAS-04")]
    [InlineData(Submitted, null, "pending")]
    [InlineData(InReview, null, "pending")]
    [InlineData(Pended, "A4", "pended-additional-information")]
    [InlineData(Approved, "A1", "approved")]
    [InlineData(Modified, "A2", "modified")]
    [InlineData(Denied, "A3", "denied")]
    [InlineData(Expired, null, "expired")]
    [InlineData(Cancelled, null, "cancelled")]
    public async Task PAS04_Replace_EveryAuthorizationStatus_ProjectsToItsPasDisposition(
        int status, string? reviewDecision, string expectedDisposition)
    {
        var (service, _) = ServiceOver(Record(status, reviewDecision));

        var result = await service.InquireAsync(Request());
        result.Found.Should().BeTrue();

        var bundle = new PasResponseBuilder().BuildInquiryResponse(result.Authorization!);
        var response = bundle.Entry.Select(e => e.Resource).OfType<ClaimResponse>().Single();

        response.Disposition.Should().Be(expectedDisposition);
        response.PreAuthRef.Should().Be(AuthNumber);
        response.Use.Should().Be(ClaimUseCode.Preauthorization);
    }

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public async Task PAS04_Replace_PendingIsDistinguishableFromPendedForAdditionalInformation()
    {
        // The caller must be able to tell "still being worked" from "we are
        // waiting on something from you" — without CHO claiming a CDex exchange
        // it does not implement (PAS-07 stays PARTIAL).
        var builder = new PasResponseBuilder();

        var (inReview, _) = ServiceOver(Record(InReview));
        var pendingResult = await inReview.InquireAsync(Request());
        var pending = Single(builder.BuildInquiryResponse(pendingResult.Authorization!));

        var (pendedSvc, _) = ServiceOver(Record(Pended, "A4", pendReason: "Awaiting clinical notes"));
        var pendedResult = await pendedSvc.InquireAsync(Request());
        var pended = Single(builder.BuildInquiryResponse(pendedResult.Authorization!));

        pending.Disposition.Should().Be("pending");
        pended.Disposition.Should().Be("pended-additional-information");
        pending.Disposition.Should().NotBe(pended.Disposition);

        // Both are still open, so both report as queued rather than decided.
        pending.Outcome.Should().Be(ClaimProcessingCodes.Queued);
        pended.Outcome.Should().Be(ClaimProcessingCodes.Queued);

        // The pended one carries the X12 A4 review action and says what is outstanding.
        pended.Extension.Should().NotBeEmpty();
        pended.ProcessNote.Should().ContainSingle()
            .Which.Text.Should().Be("Awaiting clinical notes");
    }

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public async Task PAS04_Replace_DeniedInquiryCarriesTheCodedDenialReason()
    {
        var (service, _) = ServiceOver(Record(
            Denied, "A3", denialReasonCode: "A3-CRITERIA", denialReason: "Does not meet clinical criteria."));

        var result = await service.InquireAsync(Request());
        var response = Single(new PasResponseBuilder().BuildInquiryResponse(result.Authorization!));

        response.Outcome.Should().Be(ClaimProcessingCodes.Complete);
        response.Error.Should().ContainSingle();
        response.Error[0].Code.Coding[0].Code.Should().Be("A3-CRITERIA");
    }

    // ── Status freshness ───────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public async Task PAS04_Replace_InquiryReflectsStatusChangedSinceSubmission()
    {
        // Submit-time state was pended; the payer has since approved. A second
        // inquiry must report the approval, not a cached submission snapshot.
        var store = new RecordingStore([Record(Pended, "A4")]);
        var service = new PriorAuthorizationInquiryService(store);
        var builder = new PasResponseBuilder();

        var first = await service.InquireAsync(Request());
        Single(builder.BuildInquiryResponse(first.Authorization!))
            .Disposition.Should().Be("pended-additional-information");

        store.Replace(Record(Approved, "A1"));

        var second = await service.InquireAsync(Request());
        var after = Single(builder.BuildInquiryResponse(second.Authorization!));

        after.Disposition.Should().Be("approved");
        after.Outcome.Should().Be(ClaimProcessingCodes.Complete);
        store.Reads.Should().Be(2, "each inquiry reads live state rather than caching");
    }

    // ── Read-only / idempotency ────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public async Task PAS04_Replace_RepeatedInquiryIsFreeOfConsequence()
    {
        var (service, store) = ServiceOver(Record(Approved, "A1"));

        for (var i = 0; i < 5; i++)
            (await service.InquireAsync(Request())).Found.Should().BeTrue();

        store.Writes.Should().Be(0, "an inquiry must never create or change an authorization");
        store.Reads.Should().Be(5);
        store.Snapshot().Should().ContainSingle()
            .Which.Status.Should().Be(Approved, "the record is untouched by being read");
    }

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public void PAS04_Replace_TheReadSeamHasNoWriteCapabilityAtAll()
    {
        // Structural, not behavioural: the store interface exposes no mutating
        // method, so no future caller can make an inquiry side-effecting without
        // changing the contract deliberately.
        var methods = typeof(IPriorAuthorizationStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToList();

        methods.Should().ContainSingle().Which.Should().Be(
            nameof(IPriorAuthorizationStore.GetByAuthorizationNumberAsync));
        methods.Should().NotContain(n =>
            n.StartsWith("Create", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("Update", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("Save", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("Submit", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("Delete", StringComparison.OrdinalIgnoreCase));
    }

    // ── Tenant and caller isolation ────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public async Task PAS04_Replace_AuthorizationFromAnotherTenantIsNotReturned()
    {
        var (service, _) = ServiceOver(Record(Approved, "A1", tenant: "some-other-tenant"));

        var result = await service.InquireAsync(Request());

        result.Found.Should().BeFalse();
        result.Outcome.Should().Be(PriorAuthorizationInquiryOutcome.TenantMismatch);
        result.Authorization.Should().BeNull("no cross-tenant record may reach the projection");
    }

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public async Task PAS04_Replace_AuthorizationNumberAloneDoesNotOpenARecord()
    {
        // A guessable tracking number must not be a lookup key on its own.
        var (service, _) = ServiceOver(Record(Approved, "A1"));

        var result = await service.InquireAsync(Request(member: null, npi: null));

        result.Found.Should().BeFalse();
        result.Outcome.Should().Be(PriorAuthorizationInquiryOutcome.MissingCorroboratingKey);
    }

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public async Task PAS04_Replace_WrongCorroboratingKeyIsRefused()
    {
        var (service, _) = ServiceOver(Record(Approved, "A1"));

        var wrongMember = await service.InquireAsync(Request(member: "pat-999"));
        var wrongProvider = await service.InquireAsync(Request(member: null, npi: "9999999999"));

        wrongMember.Found.Should().BeFalse();
        wrongMember.Outcome.Should().Be(PriorAuthorizationInquiryOutcome.NotAuthorizedForCaller);
        wrongProvider.Found.Should().BeFalse();
    }

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public async Task PAS04_Replace_MemberReferenceResolvesInEitherForm()
    {
        var (service, _) = ServiceOver(Record(Approved, "A1", member: "Patient/pat-001"));

        (await service.InquireAsync(Request(member: "pat-001"))).Found.Should().BeTrue();
        (await service.InquireAsync(Request(member: "Patient/pat-001"))).Found.Should().BeTrue();
    }

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public async Task PAS04_Replace_ProviderNpiAloneCorroborates()
    {
        var (service, _) = ServiceOver(Record(Approved, "A1"));

        var result = await service.InquireAsync(Request(member: null, npi: ProviderNpi));

        result.Found.Should().BeTrue();
    }

    // ── Anti-enumeration ───────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public async Task PAS04_Replace_UnknownWrongTenantAndNotYoursAreAllRefusedTheSameWay()
    {
        // The categories are kept for audit, but none of them may reach the
        // caller as a distinguishable answer — see the controller, which maps
        // every refusal onto one 404 OperationOutcome.
        var (unknown, _) = ServiceOver();
        var (otherTenant, _) = ServiceOver(Record(Approved, "A1", tenant: "elsewhere"));
        var (notYours, _) = ServiceOver(Record(Approved, "A1", member: "pat-777", npi: "0000000000"));

        var a = await unknown.InquireAsync(Request());
        var b = await otherTenant.InquireAsync(Request());
        var c = await notYours.InquireAsync(Request());

        a.Found.Should().BeFalse();
        b.Found.Should().BeFalse();
        c.Found.Should().BeFalse();

        // Distinct internally...
        new[] { a.Outcome, b.Outcome, c.Outcome }.Should().OnlyHaveUniqueItems();
        // ...and each carries no record to project.
        new[] { a.Authorization, b.Authorization, c.Authorization }.Should().AllSatisfy(
            r => r.Should().BeNull());
    }

    // ── Request validation ─────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public async Task PAS04_Replace_MissingAuthorizationIdentifierIsRefusedCleanly()
    {
        var (service, store) = ServiceOver(Record(Approved, "A1"));

        var result = await service.InquireAsync(Request(authNumber: null));

        result.Found.Should().BeFalse();
        result.Outcome.Should().Be(PriorAuthorizationInquiryOutcome.MissingIdentifier);
        store.Reads.Should().Be(0, "a request without an identifier never reaches the store");
    }

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public async Task PAS04_Replace_RequestDefectsAre400AndRecordRefusalsAre404()
    {
        // A defect in the REQUEST is the caller's to fix and reveals nothing
        // about what exists, so it is reported plainly. A refusal about a RECORD
        // is uniform. Collapsing the first into the second would tell a caller
        // who forgot an identifier that their authorization does not exist.
        var controller = InquiryController(Record(Approved, "A1"));

        var noIdentifier = await controller.ClaimInquire(BundleWith(new Claim
        {
            Use = ClaimUseCode.Preauthorization,
            Patient = new ResourceReference($"Patient/{Member}"),
        }));

        var noCorroboratingKey = await controller.ClaimInquire(BundleWith(new Claim
        {
            Use = ClaimUseCode.Preauthorization,
            Identifier = [new Identifier { Value = AuthNumber }],
        }));

        var notYours = await controller.ClaimInquire(BundleWith(new Claim
        {
            Use = ClaimUseCode.Preauthorization,
            Identifier = [new Identifier { Value = AuthNumber }],
            Patient = new ResourceReference("Patient/pat-999"),
        }));

        noIdentifier.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        noCorroboratingKey.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        // ...but a well-formed request for a record the caller may not have is
        // still the uniform 404.
        notYours.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
    }

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public void PAS04_Replace_OnlyRequestShapeDefectsAreSafeToDescribe()
    {
        // Structural guard on the classification itself: every outcome that
        // concerns a RECORD must map to the uniform answer, so a new outcome
        // cannot accidentally become a distinguishable one.
        PriorAuthorizationInquiryResult.Refused(PriorAuthorizationInquiryOutcome.MissingIdentifier)
            .FailureKind.Should().Be(PriorAuthorizationInquiryFailureKind.BadRequest);
        PriorAuthorizationInquiryResult.Refused(PriorAuthorizationInquiryOutcome.MissingCorroboratingKey)
            .FailureKind.Should().Be(PriorAuthorizationInquiryFailureKind.BadRequest);

        foreach (var recordOutcome in new[]
                 {
                     PriorAuthorizationInquiryOutcome.NotFound,
                     PriorAuthorizationInquiryOutcome.TenantMismatch,
                     PriorAuthorizationInquiryOutcome.NotAuthorizedForCaller,
                 })
        {
            PriorAuthorizationInquiryResult.Refused(recordOutcome)
                .FailureKind.Should().Be(PriorAuthorizationInquiryFailureKind.Unavailable,
                    "a refusal about a record must never be distinguishable");
        }
    }

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public void PAS04_Replace_LookupKeysComeFromTheServiceNotTheController()
    {
        // The controller routes and maps to HTTP; which Claim element carries
        // which lookup key is a property of the PAS request shape and lives with
        // the lookup rules.
        typeof(IPriorAuthorizationInquiryService)
            .GetMethod(nameof(IPriorAuthorizationInquiryService.FromInquiryClaim))
            .Should().NotBeNull();

        typeof(PasController)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Select(m => m.Name)
            .Should().NotContain("ExtractAuthorizationNumber",
                "identifier extraction belongs to the inquiry service");
    }

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public void PAS04_Replace_TenantIsNeverTakenFromTheRequestBody()
    {
        // FromInquiryClaim takes the tenant as an argument from the authenticated
        // context; there is no path by which a Claim could supply one.
        var service = new PriorAuthorizationInquiryService(new RecordingStore([]));

        var mapped = service.FromInquiryClaim(InquiryClaim(), "tenant-from-context", "caller-1");

        mapped.TenantId.Should().Be("tenant-from-context");
        mapped.CallerId.Should().Be("caller-1");
    }

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public async Task PAS04_Replace_MalformedBundleReturnsAnOperationOutcome()
    {
        var controller = InquiryController(Record(Approved, "A1"));

        var empty = await controller.ClaimInquire(new Bundle());
        var wrongUse = await controller.ClaimInquire(BundleWith(new Claim
        {
            Use = ClaimUseCode.Claim,
            Identifier = [new Identifier { Value = AuthNumber }],
        }));

        empty.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
        wrongUse.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    // ── Route and projection shape ─────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public async Task PAS04_Replace_InquireReturnsAPasConformantClaimResponseBundle()
    {
        var controller = InquiryController(Record(Approved, "A1"));

        var result = await controller.ClaimInquire(BundleWith(InquiryClaim()));

        var bundle = result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<Bundle>().Subject;

        bundle.Type.Should().Be(Bundle.BundleType.Collection);
        var response = bundle.Entry.Select(e => e.Resource).OfType<ClaimResponse>().Single();

        response.Meta.Profile.Should().Contain(
            "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-claimresponse");
        response.Status.Should().Be(FinancialResourceStatusCodes.Active);
        response.Use.Should().Be(ClaimUseCode.Preauthorization);
        response.Patient.Reference.Should().Be($"Patient/{Member}");
        response.Identifier.Should().ContainSingle().Which.Value.Should().Be(AuthNumber);
        response.Item.Should().ContainSingle("the requested service line is reported back");
    }

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public async Task PAS04_Replace_UnknownAuthorizationReturnsUniform404WithoutLeakingExistence()
    {
        var known = InquiryController(Record(Approved, "A1", tenant: "elsewhere"));
        var unknown = InquiryController();

        var wrongTenant = await known.ClaimInquire(BundleWith(InquiryClaim()));
        var noSuchAuth = await unknown.ClaimInquire(BundleWith(InquiryClaim()));

        var a = wrongTenant.Should().BeOfType<ObjectResult>().Subject;
        var b = noSuchAuth.Should().BeOfType<ObjectResult>().Subject;

        a.StatusCode.Should().Be(404);
        b.StatusCode.Should().Be(a.StatusCode);

        var outcomeA = a.Value.Should().BeOfType<OperationOutcome>().Subject;
        var outcomeB = b.Value.Should().BeOfType<OperationOutcome>().Subject;
        outcomeB.Issue[0].Diagnostics.Should().Be(outcomeA.Issue[0].Diagnostics,
            "the refusal must not reveal whether the authorization exists");
    }

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public void PAS04_Replace_InquireIsRoutedAsThePasOperation()
    {
        var templates = typeof(PasController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(m => m.GetCustomAttributes<HttpPostAttribute>()
                .Select(a => a.Template ?? string.Empty))
            .ToList();

        templates.Should().Contain("Claim/$inquire");
        templates.Should().Contain("Claim/$submit");
    }

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public void PAS04_Replace_CapabilityStatementAdvertisesTheOperationsThatExist()
    {
        var controller = new MetadataController(AcceptanceContext.DemoConfig()).WithTenant();
        var capability = controller.GetCapabilityStatement()
            .Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<CapabilityStatement>().Subject;

        var claim = capability.Rest.Single().Resource.Single(r => r.Type == "Claim");

        claim.Operation.Select(o => o.Name).Should().Contain(["submit", "inquire"]);
        claim.Operation.Single(o => o.Name == "inquire").Definition.Should()
            .Be("http://hl7.org/fhir/us/davinci-pas/OperationDefinition/Claim-inquire");
    }

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public void PAS04_Replace_TheProjectionCarriesNoInternalOrPhiFields()
    {
        // The read projection is deliberately narrow: the fields an inquiry does
        // not need have nowhere to land, so they cannot leak into a response.
        var properties = typeof(PriorAuthorizationRecord).GetProperties()
            .Select(p => p.Name).ToList();

        properties.Should().NotContain(n =>
            n.Contains("FirstName", StringComparison.OrdinalIgnoreCase)
            || n.Contains("LastName", StringComparison.OrdinalIgnoreCase)
            || n.Contains("DateOfBirth", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Attachment", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Notes", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Reviewer", StringComparison.OrdinalIgnoreCase));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ClaimResponse Single(Bundle bundle)
        => bundle.Entry.Select(e => e.Resource).OfType<ClaimResponse>().Single();

    private static Claim InquiryClaim() => new()
    {
        Use = ClaimUseCode.Preauthorization,
        Identifier = [new Identifier { Value = AuthNumber }],
        Patient = new ResourceReference($"Patient/{Member}"),
    };

    private static Bundle BundleWith(Resource resource) => new()
    {
        Type = Bundle.BundleType.Collection,
        Entry = [new Bundle.EntryComponent { Resource = resource }],
    };

    private static PasController InquiryController(params PriorAuthorizationRecord[] records)
        => new PasController(
                new Mock<IPasAutoAdjudicator>().Object,
                new PasResponseBuilder(),
                new Cms0057ComplianceChecker(),
                new PriorAuthorizationInquiryService(new RecordingStore(records)),
                Microsoft.Extensions.Options.Options.Create(new FhirService.Models.PasAutoAdjudicationConfig()),
                new Mock<IHttpClientFactory>().Object,
                AcceptanceContext.Logger<PasController>())
            .WithTenant();

    /// <summary>
    /// Stands in for authorization-service. Counts reads so freshness and
    /// read-only behaviour can be asserted, and has no write path at all.
    /// </summary>
    private sealed class RecordingStore : IPriorAuthorizationStore
    {
        private readonly List<PriorAuthorizationRecord> _records;

        public RecordingStore(IEnumerable<PriorAuthorizationRecord> records)
            => _records = records.ToList();

        public int Reads { get; private set; }

        /// <summary>Always zero: nothing here can write. Asserted, not assumed.</summary>
        public int Writes => 0;

        public void Replace(PriorAuthorizationRecord record)
        {
            _records.RemoveAll(r => r.AuthorizationNumber == record.AuthorizationNumber);
            _records.Add(record);
        }

        public IReadOnlyList<PriorAuthorizationRecord> Snapshot() => _records;

        public Task<PriorAuthorizationRecord?> GetByAuthorizationNumberAsync(
            string authorizationNumber, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(_records.FirstOrDefault(
                r => r.AuthorizationNumber == authorizationNumber));
        }
    }
}
