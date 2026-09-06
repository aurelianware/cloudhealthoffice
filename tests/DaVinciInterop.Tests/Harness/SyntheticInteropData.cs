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

    /// <summary>The synthetic payer organization (the external implementation's insurer).</summary>
    public static Organization Payer() => new()
    {
        Id = PayerId,
        Identifier = { new Identifier(PayerIdentifierSystem, PayerId) },
        Active = true,
        Name = "Interop Payer A (synthetic)",
    };

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
    /// A Da Vinci PAS prior-authorization request bundle for a single office-visit
    /// item, built from the synthetic identities above.
    ///
    /// The shape follows the PAS request-bundle profile: a collection Bundle with a
    /// business identifier and timestamp, every entry carrying a fullUrl, and the
    /// Claim first with use=preauthorization plus the patient/insurer/provider/
    /// insurance references and an item carrying category and location.
    /// </summary>
    /// <param name="created">Timestamp for Bundle.timestamp and Claim.created.</param>
    public static Bundle PasRequestBundle(DateTimeOffset created)
    {
        var claim = new Claim
        {
            Id = PriorAuthId,
            Identifier = { new Identifier(ClaimIdentifierSystem, PriorAuthId) },
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
                        CptSystem, "99213", "Office or other outpatient visit"),
                    Quantity = new Quantity { Value = 1 },
                    Location = new CodeableConcept(PlaceOfServiceSystem, "11", "Office"),
                },
            },
        };

        return new Bundle
        {
            Id = "interop-pas-request-001",
            Type = Bundle.BundleType.Collection,
            Identifier = new Identifier(RequestBundleIdentifierSystem, PriorAuthId),
            Timestamp = created,
            Entry =
            {
                new Bundle.EntryComponent { FullUrl = ClaimFullUrl, Resource = claim },
                new Bundle.EntryComponent { FullUrl = PatientFullUrl, Resource = Member() },
                new Bundle.EntryComponent { FullUrl = InsurerFullUrl, Resource = Payer() },
                new Bundle.EntryComponent { FullUrl = ProviderFullUrl, Resource = Provider() },
                new Bundle.EntryComponent { FullUrl = CoverageFullUrl, Resource = Coverage() },
            },
        };
    }

    /// <summary>
    /// Wraps a PAS request bundle in the Parameters resource the PAS
    /// <c>Claim/$submit</c> and <c>Claim/$inquire</c> operations take.
    /// </summary>
    public static Parameters AsSubmitParameters(Bundle requestBundle) => new()
    {
        Parameter = { new Parameters.ParameterComponent { Name = "resource", Resource = requestBundle } },
    };
}
