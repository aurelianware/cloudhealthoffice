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

    /// <summary>The EHR's Coverage for the unknown member (a different id from the coverage-not-found one).</summary>
    public const string UnknownMemberCoverageId = "interop-coverage-unknown-member";

    /// <summary>
    /// Each specific-case request orders the MRI under its own id. They differ
    /// (another patient, another Coverage), and the kit's simulated EHR holds
    /// one resource per id: shared, the EHR's copy would not be the order the
    /// request sent, and CHO's answer would look like it changed the order.
    /// </summary>
    public const string NoMemberFoundOrderId = "interop-crd-order-no-member";
    public const string CoverageNotFoundOrderId = "interop-crd-order-coverage-not-found";
    public const string NoActiveCoverageOrderId = "interop-crd-order-no-active-coverage";

    /// <summary>
    /// A procedure CHO requires prior authorization for (Cms0057:Crd:AuthRequiredCodes
    /// in the interop profile), and the seed approves for the member.
    /// </summary>
    public const string KneeOrderCode = "27447";

    /// <summary>The seed's approved authorization for the knee procedure (interop/cho-seed.json).</summary>
    public const string ApprovedAuthorizationNumber = "INTEROP-PA-KNEE-1";

    /// <summary>A therapy CHO has no classification for, ordered as a follow-on to the knee procedure.</summary>
    public const string TherapyOrderCode = "97110";

    public const string KneeOrderId = "interop-crd-order-knee";
    public const string TherapyOrderId = "interop-crd-order-knee-therapy";
    public const string LaterKneeOrderId = "interop-crd-order-knee-after-coverage";

    /// <summary>The seed's member whose coverage has an end date (2099-12-31).</summary>
    public const string EndingCoverageMemberId = "INTEROP-MEM-2";
    public const string EndingCoverageId = "interop-coverage-ending";

    /// <summary>The draft order for the order-select hook.</summary>
    public const string SelectOrderId = "interop-crd-order-select";

    /// <summary>
    /// order-sign: the MRI order, with the patient and Coverage prefetched
    /// (first: the Demonstrate a Hook Response group sends it on its own), then
    /// the requests that provoke the rest of what a coverage-information
    /// extension can say.
    /// </summary>
    public static JsonArray OrderSign(ChoSeedFixture fixture, string coverageId)
        => new(
            Hook("order-sign", fixture.Member.MemberId, Coverage(fixture.Member.MemberId, coverageId, "active"),
                DtrPayerServerRequests.Order(fixture, coverageId)),
            ApprovedWithFollowOn(fixture, coverageId),
            AfterCoverageEnds(fixture));

    /// <summary>
    /// The knee procedure, which the member's approved authorization already
    /// covers (pa-needed satisfied, satisfied-pa-id), and a therapy order based
    /// on it in the same request (a dependency on the procedure's order).
    /// </summary>
    public static JsonObject ApprovedWithFollowOn(ChoSeedFixture fixture, string coverageId)
    {
        var knee = OrderFor(fixture.Member.MemberId, coverageId, KneeOrderId, KneeOrderCode);
        var therapy = OrderFor(fixture.Member.MemberId, coverageId, TherapyOrderId, TherapyOrderCode);
        therapy["basedOn"] = new JsonArray(new JsonObject { ["reference"] = $"ServiceRequest/{KneeOrderId}" });
        var body = Hook("order-sign", fixture.Member.MemberId,
            Coverage(fixture.Member.MemberId, coverageId, "active"), knee);
        body["context"]!["draftOrders"]!["entry"]!.AsArray().Add(new JsonObject { ["resource"] = therapy });
        return body;
    }

    /// <summary>
    /// The knee procedure for a member whose coverage ends 2099-12-31, planned
    /// for after it ends: covered conditional, info-needed contract-window.
    /// </summary>
    public static JsonObject AfterCoverageEnds(ChoSeedFixture fixture)
    {
        var order = OrderFor(EndingCoverageMemberId, EndingCoverageId, LaterKneeOrderId, KneeOrderCode);
        order["occurrenceDateTime"] = "2100-01-15";
        var coverage = Coverage(EndingCoverageMemberId, EndingCoverageId, "active");
        coverage["period"] = new JsonObject { ["start"] = "2026-01-01", ["end"] = "2099-12-31" };
        return Hook("order-sign", EndingCoverageMemberId, coverage, order);
    }

    private static JsonObject OrderFor(string memberId, string coverageId, string orderId, string code)
    {
        var fixtureLike = new ChoSeedFixture { Member = new ChoSeedMember { MemberId = memberId } };
        var order = DtrPayerServerRequests.Order(fixtureLike, coverageId);
        order["id"] = orderId;
        order["code"] = new JsonObject
        {
            ["coding"] = new JsonArray(new JsonObject { ["system"] = DtrPayerServerRequests.CptSystem, ["code"] = code }),
        };
        return order;
    }

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
        var order = DtrPayerServerRequests.Order(fixture, UnknownMemberCoverageId);
        order["id"] = NoMemberFoundOrderId;
        order["subject"] = new JsonObject { ["reference"] = $"Patient/{UnknownMemberId}" };
        return Hook("order-sign", UnknownMemberId, Coverage(UnknownMemberId, UnknownMemberCoverageId, "active"), order);
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
        var order = DtrPayerServerRequests.Order(fixture, UnknownCoverageId);
        order["id"] = CoverageNotFoundOrderId;
        return Hook("order-sign", fixture.Member.MemberId, coverage, order);
    }

    /// <summary>
    /// A known member whose only Coverage with CHO has ended (the seed's
    /// lapsed member): not-covered, no-active-coverage.
    /// </summary>
    public static JsonObject NoActiveCoverage(ChoSeedFixture fixture, string lapsedMemberId, string lapsedCoverageId)
    {
        var order = DtrPayerServerRequests.Order(fixture, lapsedCoverageId);
        order["id"] = NoActiveCoverageOrderId;
        order["subject"] = new JsonObject { ["reference"] = $"Patient/{lapsedMemberId}" };
        var coverage = Coverage(lapsedMemberId, lapsedCoverageId, "active");
        coverage["period"] = new JsonObject { ["start"] = "2025-01-01", ["end"] = "2025-12-31" };
        return Hook("order-sign", lapsedMemberId, coverage, order);
    }

    /// <summary>
    /// The data the kit's simulated EHR serves on its FHIR API and resolves
    /// references against: every Patient, Coverage, order and practitioner the
    /// requests above name, so a reference in CHO's answer (the Coverage a
    /// coverage-information extension names, say) resolves to the resource the
    /// EHR holds. Built from the requests themselves, so the two cannot drift.
    /// </summary>
    public static JsonObject MockEhrBundle(ChoSeedFixture fixture, string coverageId, string lapsedMemberId, string lapsedCoverageId)
    {
        var resources = new List<JsonObject>
        {
            new()
            {
                ["resourceType"] = "Practitioner",
                ["id"] = "interop-dtr-practitioner",
                ["identifier"] = new JsonArray(new JsonObject
                {
                    ["system"] = "http://hl7.org/fhir/sid/us-npi",
                    ["value"] = DtrPayerServerRequests.PractitionerNpi,
                }),
                ["name"] = new JsonArray(new JsonObject { ["family"] = "Practitioner", ["given"] = new JsonArray("Ordering") }),
            },
        };

        var hooks = new List<JsonObject>
        {
            AnyHook(fixture, coverageId),
            ApprovedWithFollowOn(fixture, coverageId),
            AfterCoverageEnds(fixture),
            (JsonObject)OrderSelect(fixture, coverageId)[0]!,
            NoMemberFound(fixture),
            CoverageNotFound(fixture),
            NoActiveCoverage(fixture, lapsedMemberId, lapsedCoverageId),
        };
        foreach (var hook in hooks)
        {
            resources.Add((JsonObject)hook["prefetch"]!["patient"]!.DeepClone());
            foreach (var entry in hook["prefetch"]!["coverage"]!["entry"]!.AsArray())
                resources.Add((JsonObject)entry!["resource"]!.DeepClone());
            foreach (var entry in hook["context"]!["draftOrders"]!["entry"]!.AsArray())
                resources.Add((JsonObject)entry!["resource"]!.DeepClone());
        }

        var unique = resources
            .GroupBy(r => $"{r["resourceType"]}/{r["id"]}")
            .Select(g => g.First());

        return new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "collection",
            ["entry"] = new JsonArray(unique.Select(r => (JsonNode)new JsonObject { ["resource"] = r }).ToArray()),
        };
    }

    private static JsonObject Hook(string hook, string patientId, JsonObject coverage, JsonObject order) => new()
    {
        ["hookInstance"] = Guid.NewGuid().ToString(),
        ["hook"] = hook,
        // The EHR's grant to the CDS service. The kit fills in access_token
        // (its simulated EHR's) and expires_in; the rest is the EHR's to say,
        // and CDS Hooks 2.0 requires all of it.
        ["fhirAuthorization"] = new JsonObject
        {
            ["token_type"] = "Bearer",
            ["expires_in"] = 300,
            ["scope"] = "patient/Patient.read patient/Coverage.read",
            ["subject"] = "cho-crd",
        },
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
