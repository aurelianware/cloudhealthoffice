using System.Text.Json.Nodes;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// The CDS Hooks requests the CRD server suite (INFERNO-CRD-SERVER-001) sends
/// CHO, built from the seed.
///
/// <para>
/// The kit calls CHO with the bodies it is given and fills in only
/// <c>hookInstance</c>, <c>fhirServer</c> and <c>fhirAuthorization</c> (its
/// own simulated EHR FHIR API). Everything else is the EHR's to send, so it is
/// written here the way an EHR would: the draft order, the user, the patient,
/// and the prefetch CHO's discovery asks for.
/// </para>
///
/// <para>
/// The specific coverage responses each need a request built to provoke one
/// answer, and each is described where it is built.
/// </para>
/// </summary>
public static class CrdServerRequests
{
    /// <summary>A member CHO has never enrolled: no-member-found.</summary>
    public const string UnknownMemberId = "INTEROP-MEM-UNKNOWN";

    /// <summary>A Coverage id CHO does not hold, for a member it does: coverage-not-found.</summary>
    public const string UnknownCoverageId = "interop-coverage-unknown";

    /// <summary>The draft order for the order-select hook.</summary>
    public const string SelectOrderId = "interop-crd-order-select";

    /// <summary>order-sign: the MRI order, with the patient and Coverage prefetched.</summary>
    public static JsonArray OrderSign(ChoSeedFixture fixture, string coverageId)
        => new(Hook("order-sign", fixture.Member.MemberId, Coverage(fixture.Member.MemberId, coverageId, "active"),
            DtrPayerServerRequests.Order(fixture, coverageId)));

    /// <summary>order-select: the same order, as the clinician selects it, before it is signed.</summary>
    public static JsonArray OrderSelect(ChoSeedFixture fixture, string coverageId)
    {
        var order = DtrPayerServerRequests.Order(fixture, coverageId);
        order["id"] = SelectOrderId;
        var body = Hook("order-select", fixture.Member.MemberId,
            Coverage(fixture.Member.MemberId, coverageId, "active"), order);
        body["context"]!["selections"] = new JsonArray($"ServiceRequest/{SelectOrderId}");
        return new JsonArray(body);
    }

    /// <summary>The one request the Demonstrate a Hook Response group sends: order-sign.</summary>
    public static JsonObject AnyHook(ChoSeedFixture fixture, string coverageId)
        => (JsonObject)OrderSign(fixture, coverageId)[0]!.DeepClone();

    /// <summary>
    /// A hook with no prefetch at all, so CHO has to read the Coverage from
    /// the EHR's FHIR API. The kit answers those reads 500, which CHO must
    /// report as indeterminate for a technical reason, not as a determination;
    /// the Coverage it names is the one the order's <c>insurance</c> carries.
    /// </summary>
    public static JsonObject TechnicalIssues(ChoSeedFixture fixture, string coverageId)
    {
        var body = AnyHook(fixture, coverageId);
        body.Remove("prefetch");
        return body;
    }

    /// <summary>A patient CHO has no member for, whose EHR Coverage names CHO's plan: not-covered, no-member-found.</summary>
    public static JsonObject NoMemberFound(ChoSeedFixture fixture)
    {
        var order = DtrPayerServerRequests.Order(fixture, UnknownCoverageId);
        order["subject"] = new JsonObject { ["reference"] = $"Patient/{UnknownMemberId}" };
        return Hook("order-sign", UnknownMemberId, Coverage(UnknownMemberId, UnknownCoverageId, "active"), order);
    }

    /// <summary>A group CHO has never enrolled anyone in.</summary>
    public const string UnknownGroupNumber = "INTEROP-GRP-UNKNOWN";

    /// <summary>
    /// A known member, with a Coverage CHO does not hold: the EHR's Coverage
    /// names a group, as printed on the member's card, that the member has no
    /// coverage under with CHO. not-covered, coverage-not-found.
    /// </summary>
    public static JsonObject CoverageNotFound(ChoSeedFixture fixture)
    {
        var coverage = Coverage(fixture.Member.MemberId, UnknownCoverageId, "active");
        coverage["class"] = new JsonArray(new JsonObject
        {
            ["type"] = new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject
                {
                    ["system"] = "http://terminology.hl7.org/CodeSystem/coverage-class",
                    ["code"] = "group",
                }),
            },
            ["value"] = UnknownGroupNumber,
        });
        return Hook("order-sign", fixture.Member.MemberId, coverage,
            DtrPayerServerRequests.Order(fixture, UnknownCoverageId));
    }

    /// <summary>
    /// A known member whose only Coverage with CHO has ended (the seed's
    /// lapsed member): not-covered, no-active-coverage.
    /// </summary>
    public static JsonObject NoActiveCoverage(ChoSeedFixture fixture, string lapsedMemberId, string lapsedCoverageId)
    {
        var order = DtrPayerServerRequests.Order(fixture, lapsedCoverageId);
        order["subject"] = new JsonObject { ["reference"] = $"Patient/{lapsedMemberId}" };
        var coverage = Coverage(lapsedMemberId, lapsedCoverageId, "active");
        coverage["period"] = new JsonObject { ["start"] = "2025-01-01", ["end"] = "2025-12-31" };
        return Hook("order-sign", lapsedMemberId, coverage, order);
    }

    private static JsonObject Hook(string hook, string patientId, JsonObject coverage, JsonObject order) => new()
    {
        ["hookInstance"] = Guid.NewGuid().ToString(),
        ["hook"] = hook,
        ["context"] = new JsonObject
        {
            ["userId"] = "Practitioner/interop-dtr-practitioner",
            ["patientId"] = patientId,
            ["draftOrders"] = new JsonObject
            {
                ["resourceType"] = "Bundle",
                ["type"] = "collection",
                ["entry"] = new JsonArray(new JsonObject { ["resource"] = order }),
            },
        },
        ["prefetch"] = new JsonObject
        {
            ["patient"] = Patient(patientId),
            ["coverage"] = new JsonObject
            {
                ["resourceType"] = "Bundle",
                ["type"] = "searchset",
                ["entry"] = new JsonArray(new JsonObject { ["resource"] = coverage }),
            },
        },
    };

    private static JsonObject Coverage(string memberId, string coverageId, string status)
    {
        var fixtureLike = new ChoSeedFixture { Member = new ChoSeedMember { MemberId = memberId } };
        var coverage = DtrPayerServerRequests.Coverage(fixtureLike, coverageId);
        coverage["status"] = status;
        return coverage;
    }

    private static JsonObject Patient(string patientId) => new()
    {
        ["resourceType"] = "Patient",
        ["id"] = patientId,
        ["meta"] = new JsonObject
        {
            ["profile"] = new JsonArray("http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient"),
        },
        ["identifier"] = new JsonArray(new JsonObject
        {
            ["system"] = "urn:cho:interop:member",
            ["value"] = patientId,
        }),
        ["name"] = new JsonArray(new JsonObject { ["family"] = "Interop", ["given"] = new JsonArray("Testcase") }),
        ["gender"] = "female",
        ["birthDate"] = "1970-01-01",
    };
}
