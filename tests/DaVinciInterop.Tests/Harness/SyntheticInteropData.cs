using Hl7.Fhir.Model;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// The synthetic interoperability identity set.
///
/// Every value an external implementation ever sees comes from here. The
/// identifiers are valid in *format* — an external RI must be able to accept and
/// echo them — but they name nobody: no real member, no real provider, no real
/// payer, and no data derived from any of those. The harness has no code path
/// that sends production data to a third-party system.
///
/// The NPI below is the conventional all-zeros-style test NPI 1234567893, whose
/// check digit is valid so format-checking implementations accept it, and which
/// is not issued to any provider.
/// </summary>
public static class SyntheticInteropData
{
    public const string MemberId = "interop-member-001";
    public const string MemberIdentifierSystem = "urn:cho:interop:member";
    public const string PayerId = "interop-payer-a";
    public const string PayerIdentifierSystem = "urn:cho:interop:payer";
    public const string ProviderId = "interop-provider-a";
    public const string ProviderNpi = "1234567893";
    public const string NpiSystem = "http://hl7.org/fhir/sid/us-npi";
    public const string PriorAuthId = "interop-pa-001";
    public const string ClaimIdentifierSystem = "urn:cho:interop:claim";
    public const string CoverageId = "interop-coverage-001";
    public const string CoverageIdentifierSystem = "urn:cho:interop:coverage";
    public const string RequestBundleIdentifierSystem = "urn:cho:interop:pas-request";

    // Stable fullUrls so a captured request artifact diffs cleanly between runs.
    public const string ClaimFullUrl = "urn:uuid:11111111-1111-4111-8111-111111111111";
    public const string PatientFullUrl = "urn:uuid:22222222-2222-4222-8222-222222222222";
    public const string InsurerFullUrl = "urn:uuid:33333333-3333-4333-8333-333333333333";
    public const string ProviderFullUrl = "urn:uuid:44444444-4444-4444-8444-444444444444";
    public const string CoverageFullUrl = "urn:uuid:55555555-5555-4555-8555-555555555555";

    private const string V2IdentifierTypeSystem = "http://terminology.hl7.org/CodeSystem/v2-0203";
    private const string ClaimTypeSystem = "http://terminology.hl7.org/CodeSystem/claim-type";
    private const string ProcessPrioritySystem = "http://terminology.hl7.org/CodeSystem/processpriority";
    private const string CptSystem = "http://www.ama-assn.org/go/cpt";
    private const string X12ServiceTypeSystem = "https://codesystem.x12.org/005010/1365";
    private const string PlaceOfServiceSystem =
        "https://www.cms.gov/Medicare/Coding/place-of-service-codes/Place_of_Service_Code_Set";

    /// <summary>The synthetic member. Carries the type=MB identifier PAS requires.</summary>
    public static Patient Member() => new()
    {
        Id = MemberId,
        Identifier =
        {
            new Identifier(MemberIdentifierSystem, MemberId)
            {
                Type = new CodeableConcept(V2IdentifierTypeSystem, "MB"),
            },
        },
        Name = { new HumanName { Family = "Interop", Given = ["Testcase"] } },
        Gender = AdministrativeGender.Female,
        BirthDate = "1970-01-01",
    };

    /// <summary>A second synthetic member the payer has never been told about.</summary>
    public const string OtherMemberId = "interop-member-002";

    /// <summary>
    /// A second synthetic member, structurally identical to <see cref="Member"/>
    /// but with a different member identifier.
    ///
    /// Exists for one purpose: to send a well-formed inquiry that quotes a real
    /// payer-issued authorization number alongside the WRONG member, and observe
    /// that the payer does not hand the authorization over. Without it, an
    /// inquiry scenario proves only that a correct request works — not that an
    /// authorization is unreachable by guessing its identifier.
    /// </summary>
    public static Patient OtherMember() => new()
    {
        Id = OtherMemberId,
        Identifier =
        {
            new Identifier(MemberIdentifierSystem, OtherMemberId)
            {
                Type = new CodeableConcept(V2IdentifierTypeSystem, "MB"),
            },
        },
        Name = { new HumanName { Family = "Interop", Given = ["Othercase"] } },
        Gender = AdministrativeGender.Male,
        BirthDate = "1970-01-02",
    };

    /// <summary>The synthetic payer organization (the external implementation's insurer).</summary>
    public static Organization Payer() => Payer(engageUpstreamPayerRules: false);

    /// <summary>
    /// The synthetic payer organization, optionally also carrying the payer
    /// identifier the pinned implementation's own rule fixtures are scoped to.
    ///
    /// A PAS payer decides coverage against the identifiers on the member's
    /// Coverage.payor. CHO's own synthetic payer id matches no rule on the
    /// reference implementation, so a request carrying only that id gets a
    /// content-independent answer. Adding the upstream fixture identifier is what
    /// makes the payer's rules run — the same thing <c>BR-CRD-001</c> does, and
    /// for the same reason. Both identifiers are synthetic and name no real payer.
    /// </summary>
    public static Organization Payer(bool engageUpstreamPayerRules)
    {
        var payer = new Organization
        {
            Id = PayerId,
            Identifier = { new Identifier(PayerIdentifierSystem, PayerId) },
            Active = true,
            Name = "Interop Payer A (synthetic)",
        };

        if (engageUpstreamPayerRules)
        {
            payer.Identifier.Add(new Identifier(
                UpstreamRulePayerIdentifierSystem, UpstreamRulePayerIdentifierValue));
        }

        return payer;
    }

    /// <summary>The synthetic requesting provider organization.</summary>
    public static Organization Provider() => new()
    {
        Id = ProviderId,
        Identifier = { new Identifier(NpiSystem, ProviderNpi) },
        Active = true,
        Name = "Interop Provider A (synthetic)",
    };

    /// <summary>The synthetic coverage linking member to payer.</summary>
    public static Coverage Coverage() => new()
    {
        Id = CoverageId,
        Identifier = { new Identifier(CoverageIdentifierSystem, CoverageId) },
        Status = FinancialResourceStatusCodes.Active,
        Beneficiary = new ResourceReference(PatientFullUrl),
        Payor = { new ResourceReference(InsurerFullUrl) },
    };

    /// <summary>
    /// The service a PAS request asks about, and whether the payer's own coverage
    /// rules should be engaged by it.
    ///
    /// Two scenarios need two different things from the same request builder, and
    /// separating them here keeps one builder rather than two.
    /// <see cref="OfficeVisit"/> deliberately matches no rule on the pinned payer,
    /// which is what makes <c>BR-PAS-SUBMIT-001</c> a content-independent proof of
    /// protocol interoperability. <see cref="PriorAuthorizationRequired"/>
    /// deliberately does match one, because an inquiry needs the payer to have
    /// issued an authorization identity — and a payer only issues one when its
    /// rules actually decided something.
    /// </summary>
    /// <param name="BillingCodeSystem">Code system of the requested service.</param>
    /// <param name="BillingCode">The requested service code.</param>
    /// <param name="BillingCodeDisplay">Display for the requested service.</param>
    /// <param name="EngageUpstreamPayerRules">
    /// When true, the payer Organization additionally carries the payer identifier
    /// the pinned implementation's own rule fixtures are scoped to, so its
    /// coverage rules engage. Upstream test-fixture data, exactly as CRD already
    /// uses it — see docs/interop/davinci.md, "Upstream fixture dependency".
    /// </param>
    public sealed record PasRequestedService(
        string BillingCodeSystem,
        string BillingCode,
        string BillingCodeDisplay,
        bool EngageUpstreamPayerRules)
    {
        /// <summary>
        /// An office visit that matches no PlanDefinition on the pinned payer, so
        /// the answer is content-independent. The default, and what
        /// <c>BR-PAS-SUBMIT-001</c> has always submitted.
        /// </summary>
        public static PasRequestedService OfficeVisit { get; } = new(
            CptSystem, "99213", "Office or other outpatient visit", EngageUpstreamPayerRules: false);

        /// <summary>
        /// A service the pinned payer's own <c>PriorAuthRequired</c> rule covers.
        ///
        /// Chosen because it is the same billing code <c>BR-CRD-001</c> and
        /// <c>BR-DTR-001</c> already prove the payer's rules evaluate, so the
        /// inquiry scenario rests on behaviour this repository has demonstrated
        /// rather than on a code picked hopefully. Which decision the rule
        /// produces is the payer's business and is never asserted — only that
        /// whatever it decided came with an identity to inquire on.
        /// </summary>
        public static PasRequestedService PriorAuthorizationRequired { get; } = new(
            HcpcsSystem, "L8000", "Breast prosthesis", EngageUpstreamPayerRules: true);
    }

    /// <summary>
    /// A Da Vinci PAS prior-authorization request bundle for a single office-visit
    /// item, built from the synthetic identities above.
    ///
    /// The shape follows the PAS request-bundle profile: a collection Bundle with a
    /// business identifier and timestamp, every entry carrying a fullUrl, and the
    /// Claim first with use=preauthorization plus the patient/insurer/provider/
    /// insurance references and an item carrying category and location.
    /// </summary>
    /// <param name="created">Timestamp for Bundle.timestamp and Claim.created.</param>
    public static Bundle PasRequestBundle(DateTimeOffset created) =>
        PasRequestBundle(created, PasRequestedService.OfficeVisit, PriorAuthId);

    /// <summary>
    /// The same PAS request bundle, for a caller that needs to choose the service
    /// and the request's own identifier.
    ///
    /// The identifier is a parameter because a submit→inquire scenario must be
    /// able to tell its own authorization apart from anything else the payer
    /// holds, and because a run must not depend on a previous run's state. It is
    /// the SUBMITTER's identifier for its request — not an authorization number,
    /// which only the payer may issue.
    /// </summary>
    /// <param name="created">Timestamp for Bundle.timestamp and Claim.created.</param>
    /// <param name="service">Which service to request, and whether to engage the payer's rules.</param>
    /// <param name="requestIdentifier">The submitter's identifier for this request.</param>
    public static Bundle PasRequestBundle(
        DateTimeOffset created,
        PasRequestedService service,
        string requestIdentifier)
    {
        var claim = new Claim
        {
            Id = PriorAuthId,
            Identifier = { new Identifier(ClaimIdentifierSystem, requestIdentifier) },
            Status = FinancialResourceStatusCodes.Active,
            Type = new CodeableConcept(ClaimTypeSystem, "professional"),
            Use = ClaimUseCode.Preauthorization,
            Patient = new ResourceReference(PatientFullUrl),
            Created = created.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            Insurer = new ResourceReference(InsurerFullUrl),
            Provider = new ResourceReference(ProviderFullUrl),
            Priority = new CodeableConcept(ProcessPrioritySystem, "normal"),
            Insurance =
            {
                new Claim.InsuranceComponent
                {
                    Sequence = 1,
                    Focal = true,
                    Coverage = new ResourceReference(CoverageFullUrl),
                },
            },
            Item =
            {
                new Claim.ItemComponent
                {
                    Sequence = 1,
                    Category = new CodeableConcept(X12ServiceTypeSystem, "3", "Consultation"),
                    ProductOrService = new CodeableConcept(
                        service.BillingCodeSystem, service.BillingCode, service.BillingCodeDisplay),
                    Quantity = new Quantity { Value = 1 },
                    Location = new CodeableConcept(PlaceOfServiceSystem, "11", "Office"),
                },
            },
        };

        return new Bundle
        {
            Id = "interop-pas-request-001",
            Type = Bundle.BundleType.Collection,
            Identifier = new Identifier(RequestBundleIdentifierSystem, requestIdentifier),
            Timestamp = created,
            Entry =
            {
                new Bundle.EntryComponent { FullUrl = ClaimFullUrl, Resource = claim },
                new Bundle.EntryComponent { FullUrl = PatientFullUrl, Resource = Member() },
                new Bundle.EntryComponent
                {
                    FullUrl = InsurerFullUrl,
                    Resource = Payer(service.EngageUpstreamPayerRules),
                },
                new Bundle.EntryComponent { FullUrl = ProviderFullUrl, Resource = Provider() },
                new Bundle.EntryComponent { FullUrl = CoverageFullUrl, Resource = Coverage() },
            },
        };
    }

    // ── CRD (CDS Hooks) ──────────────────────────────────────────────────────
    //
    // Coverage Requirements Discovery is evaluated by the payer against the payer
    // identifier on the member's Coverage. The pinned HL7 burden-reduction payer
    // scopes its rule fixtures to a specific synthetic payer identifier, so a
    // request carrying CHO's own payer id would match no rule and the exchange
    // would prove only that the endpoint answers.
    //
    // The identifier below is therefore UPSTREAM TEST FIXTURE DATA, not CHO
    // production configuration: it is the payer id the reference implementation's
    // own scenario library uses, and it exists solely so its rule lookup engages.
    // It is synthetic on both sides and names no real payer.

    /// <summary>Identifier system of the payer the upstream CRD rule fixtures are scoped to.</summary>
    public const string UpstreamRulePayerIdentifierSystem = "urn:oid:2.16.840.1.113883.6.300";

    /// <summary>
    /// Payer identifier the pinned br-payer rule fixtures are scoped to. Upstream
    /// test data — see docs/interop/davinci.md, "Upstream fixture dependency".
    /// </summary>
    public const string UpstreamRulePayerIdentifierValue = "00001";

    public const string PractitionerId = "interop-practitioner-001";
    public const string OrderId = "interop-order-001";
    public const string HcpcsSystem = "http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets";

    /// <summary>
    /// Builds a CDS Hooks request for a CRD order-sign style hook: a draft device
    /// order for <paramref name="billingCode"/>, with the patient and coverage
    /// supplied as prefetch.
    ///
    /// Every prefetch key the service needs is supplied, so the payer has no
    /// reason to dereference <paramref name="fhirServer"/>. That is asserted by
    /// the scenario rather than assumed: the harness points fhirServer at a
    /// listener it controls and fails if a callback arrives unexpectedly.
    /// </summary>
    /// <param name="hook">The hook name, taken from discovery — never hard-coded by the caller's guess.</param>
    /// <param name="billingCode">HCPCS code on the draft order; what the payer's rules key off.</param>
    /// <param name="fhirServer">A FHIR base the harness actually runs or observes.</param>
    /// <param name="hookInstance">Stable id so a captured request artifact diffs cleanly.</param>
    public static CdsHooksRequest CrdOrderRequest(
        string hook,
        string billingCode,
        string fhirServer,
        string hookInstance)
    {
        var draftOrder = new Dictionary<string, object>
        {
            ["resourceType"] = "Bundle",
            ["type"] = "collection",
            ["entry"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["resource"] = new Dictionary<string, object>
                    {
                        ["resourceType"] = "DeviceRequest",
                        ["id"] = OrderId,
                        ["status"] = "draft",
                        ["intent"] = "original-order",
                        ["codeCodeableConcept"] = Concept(HcpcsSystem, billingCode),
                        ["subject"] = Reference($"Patient/{MemberId}"),
                        ["requester"] = Reference($"Practitioner/{PractitionerId}"),
                        ["insurance"] = new object[] { Reference($"Coverage/{CoverageId}") },
                    },
                },
            },
        };

        return new CdsHooksRequest
        {
            HookInstance = hookInstance,
            Hook = hook,
            FhirServer = fhirServer,
            Context = new Dictionary<string, object>
            {
                ["userId"] = $"Practitioner/{PractitionerId}",
                ["patientId"] = MemberId,
                ["draftOrders"] = draftOrder,
            },
            Prefetch = new Dictionary<string, object>
            {
                ["patient"] = new Dictionary<string, object>
                {
                    ["resourceType"] = "Patient",
                    ["id"] = MemberId,
                    ["identifier"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = Concept("http://terminology.hl7.org/CodeSystem/v2-0203", "MB"),
                            ["system"] = MemberIdentifierSystem,
                            ["value"] = MemberId,
                        },
                    },
                    ["gender"] = "female",
                    ["birthDate"] = "1970-01-01",
                },
                ["coverage"] = new Dictionary<string, object>
                {
                    ["resourceType"] = "Bundle",
                    ["type"] = "collection",
                    ["entry"] = new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["resource"] = new Dictionary<string, object>
                            {
                                ["resourceType"] = "Coverage",
                                ["id"] = CoverageId,
                                ["status"] = "active",
                                ["beneficiary"] = Reference($"Patient/{MemberId}"),
                                ["payor"] = new object[] { Reference($"Organization/{PayerId}") },
                            },
                        },
                        new Dictionary<string, object>
                        {
                            ["resource"] = new Dictionary<string, object>
                            {
                                ["resourceType"] = "Organization",
                                ["id"] = PayerId,
                                ["active"] = true,
                                ["name"] = "Interop Payer A (synthetic)",
                                ["identifier"] = new object[]
                                {
                                    new Dictionary<string, object>
                                    {
                                        ["system"] = UpstreamRulePayerIdentifierSystem,
                                        ["value"] = UpstreamRulePayerIdentifierValue,
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    // ── DTR ($questionnaire-package) ─────────────────────────────────────────

    /// <summary>
    /// Builds the input Parameters for a DTR <c>$questionnaire-package</c>.
    ///
    /// The canonical is supplied by the caller because it comes from what the
    /// payer said in CRD, not from anything CHO decided: following the payer's own
    /// determination is what makes this independent evidence rather than CHO
    /// asking for a questionnaire it picked itself.
    ///
    /// Carries only what the operation requires — the pinned implementation
    /// declares <c>coverage</c> mandatory and needs at least one of
    /// <c>questionnaire</c>, <c>order</c> or <c>context</c>. Padding the request
    /// with resources the operation does not use would make it look richer while
    /// proving less.
    ///
    /// The Coverage carries <c>subscriberId</c> and a beneficiary identifier
    /// because the payer looks a member up by identifier rather than trusting a
    /// sender-supplied reference. It will not match a real member — the member is
    /// synthetic — and the payer says so in an OperationOutcome, which the
    /// scenario records rather than suppresses.
    /// </summary>
    public static Parameters DtrQuestionnairePackageRequest(string questionnaireCanonical) => new()
    {
        Parameter =
        {
            new Parameters.ParameterComponent
            {
                Name = "coverage",
                Resource = new Coverage
                {
                    Id = CoverageId,
                    Identifier = { new Identifier(CoverageIdentifierSystem, CoverageId) },
                    Status = FinancialResourceStatusCodes.Active,
                    SubscriberId = MemberId,
                    Beneficiary = new ResourceReference($"Patient/{MemberId}")
                    {
                        Identifier = new Identifier(MemberIdentifierSystem, MemberId)
                        {
                            Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/v2-0203", "MB"),
                        },
                    },
                    Payor = { new ResourceReference($"Organization/{PayerId}") },
                },
            },
            new Parameters.ParameterComponent
            {
                Name = "questionnaire",
                Value = new Canonical(questionnaireCanonical),
            },
        },
    };

    private static Dictionary<string, object> Concept(string system, string code) => new()
    {
        ["coding"] = new object[]
        {
            new Dictionary<string, object> { ["system"] = system, ["code"] = code },
        },
    };

    private static Dictionary<string, object> Reference(string reference) =>
        new() { ["reference"] = reference };

    /// <summary>
    /// Wraps a PAS request bundle in the Parameters resource the PAS
    /// <c>Claim/$submit</c> and <c>Claim/$inquire</c> operations take.
    /// </summary>
    public static Parameters AsSubmitParameters(Bundle requestBundle) => new()
    {
        Parameter =
        {
            new Parameters.ParameterComponent
            {
                Name = PasProtocol.ResourceParameter,
                Resource = requestBundle,
            },
        },
    };

    // ── PAS ($inquire) ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a Da Vinci PAS inquiry request bundle asking about one
    /// authorization the payer itself established.
    ///
    /// Everything that identifies the authorization comes from the payer's own
    /// submit response — <paramref name="authorizationIdentity"/> is quoted back
    /// exactly as issued and is never rewritten, padded or reformatted. CHO does
    /// not mint a tracking id of its own: if it did, the inquiry would prove only
    /// that CHO can echo a string it invented.
    ///
    /// The corroborating context is the SAME synthetic member, payer and provider
    /// the submit used, because a PAS payer scopes an inquiry by them. Supplying
    /// a different <paramref name="member"/> is how the negative subcase asks for
    /// a real authorization with the wrong corroborating key.
    ///
    /// Carries what the operation needs and no more: the inquiry Claim per
    /// <c>profile-claim-inquiry</c> (identifier required, status active,
    /// use=preauthorization, patient/insurer/provider/insurance), plus the
    /// resources those references resolve to. A PAS payer matches the member by
    /// identifier rather than by a sender-supplied logical id, so the Patient
    /// travels with the MB-typed identifier the inquiry profile requires.
    /// </summary>
    /// <param name="created">Timestamp for Bundle.timestamp and Claim.created.</param>
    /// <param name="authorizationIdentity">The payer-issued identity, exactly as received.</param>
    /// <param name="service">The service the original request named, for query-by-example matching.</param>
    /// <param name="inquiryIdentifier">The submitter's identifier for this inquiry.</param>
    /// <param name="member">
    /// The member to corroborate with. Defaults to the member the submit used;
    /// pass <see cref="OtherMember"/> to prove the payer refuses a mismatched key.
    /// </param>
    public static Bundle PasInquiryBundle(
        DateTimeOffset created,
        string authorizationIdentity,
        PasRequestedService service,
        string inquiryIdentifier,
        Patient? member = null)
    {
        var claim = new Claim
        {
            Meta = new Meta { Profile = [PasProtocol.ClaimInquiryProfile] },
            Identifier = { new Identifier(ClaimIdentifierSystem, inquiryIdentifier) },
            Status = FinancialResourceStatusCodes.Active,
            Type = new CodeableConcept(ClaimTypeSystem, "professional"),
            Use = ClaimUseCode.Preauthorization,
            Patient = new ResourceReference(PatientFullUrl),
            Created = created.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            Insurer = new ResourceReference(InsurerFullUrl),
            Provider = new ResourceReference(ProviderFullUrl),
            Priority = new CodeableConcept(ProcessPrioritySystem, "normal"),
            Insurance =
            {
                new Claim.InsuranceComponent
                {
                    Sequence = 1,
                    Focal = true,
                    Coverage = new ResourceReference(CoverageFullUrl),
                },
            },
            Item =
            {
                new Claim.ItemComponent
                {
                    Sequence = 1,
                    // The authorization number rides on Claim.item, which is where
                    // PAS contextualizes extension-authorizationNumber. It is NOT
                    // where the payer issued it — a payer issues it nested inside
                    // reviewAction on the response — and that asymmetry is the
                    // IG's, not an implementation's.
                    Extension =
                    {
                        new Extension(
                            PasProtocol.AuthorizationNumberExtension,
                            new FhirString(authorizationIdentity)),
                    },
                    ProductOrService = new CodeableConcept(
                        service.BillingCodeSystem, service.BillingCode, service.BillingCodeDisplay),
                },
            },
        };

        return new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Meta = new Meta { Profile = [PasProtocol.InquiryRequestBundleProfile] },
            Identifier = new Identifier(RequestBundleIdentifierSystem, inquiryIdentifier),
            Timestamp = created,
            Entry =
            {
                new Bundle.EntryComponent { FullUrl = ClaimFullUrl, Resource = claim },
                new Bundle.EntryComponent { FullUrl = PatientFullUrl, Resource = member ?? Member() },
                new Bundle.EntryComponent
                {
                    FullUrl = InsurerFullUrl,
                    Resource = Payer(service.EngageUpstreamPayerRules),
                },
                new Bundle.EntryComponent { FullUrl = ProviderFullUrl, Resource = Provider() },
                new Bundle.EntryComponent { FullUrl = CoverageFullUrl, Resource = Coverage() },
            },
        };
    }

    /// <summary>
    /// Wraps an inquiry request bundle in the Parameters resource
    /// <c>Claim/$inquire</c> takes.
    ///
    /// The same single <c>resource</c> parameter <c>$submit</c> takes — PAS
    /// defines both operations with one input parameter of type Bundle — so this
    /// is <see cref="AsSubmitParameters"/> under the name the calling code means.
    /// </summary>
    public static Parameters AsInquiryParameters(Bundle inquiryBundle) =>
        AsSubmitParameters(inquiryBundle);
}
