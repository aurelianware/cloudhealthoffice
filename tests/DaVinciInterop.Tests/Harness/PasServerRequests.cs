using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// The requests the PAS server suite (INFERNO-PAS-SERVER-001) sends CHO.
///
/// <para>
/// They are the kit's own example requests, from the preset it ships for
/// running its server suite (config/presets/pas_server_v221_run_against_pas_client.json.erb
/// at the pinned tag), read from the checkout rather than copied here, so a
/// kit upgrade brings its examples with it. They describe a member, a Coverage
/// and a requesting provider CHO has never heard of, so each is re-pointed at
/// the seed: the member and their Coverage, and the billing provider the
/// kit's backend client is bound to (CHO refuses a submission for a provider
/// other than the caller's, D-26). Nothing else is changed, so the requests
/// still carry every element the kit's examples exercise.
/// </para>
/// </summary>
public static class PasServerRequests
{
    /// <summary>The preset at the pinned tag that carries the kit's example requests.</summary>
    public const string PresetPath = "config/presets/pas_server_v221_run_against_pas_client.json.erb";

    /// <summary>The NPI of the provider the seed's PAS client is bound to.</summary>
    public const string ProviderNpi = "1234567893";

    private const string NpiSystem = "http://hl7.org/fhir/sid/us-npi";
    private const string MemberSystem = "urn:cho:interop:member";

    private static readonly Regex Erb = new("<%=.*?%>", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>The kit checkout, as fetch-inferno.sh lays it out (or INTEROP_INFERNO_PAS_DIR).</summary>
    public static string KitDirectory =>
        Environment.GetEnvironmentVariable("INTEROP_INFERNO_PAS_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(InteropPaths.RepositoryRoot, "interop", ".external", "davinci-pas-test-kit");

    /// <summary>Every input the preset sets, by name, with the request bundles re-pointed at the seed.</summary>
    public static IReadOnlyDictionary<string, string> Inputs(ChoSeedFixture fixture)
    {
        var path = Path.Combine(KitDirectory, PresetPath);
        if (!File.Exists(path))
        {
            throw new InteropEnvironmentException(
                $"The PAS kit's server preset is not at {path}. Fetch the kit first: ./scripts/interop/fetch-inferno.sh pas");
        }

        // The ERB sits inside JSON strings, so it is removed, not replaced by a literal.
        var preset = JsonNode.Parse(Erb.Replace(File.ReadAllText(path), string.Empty))!;
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in preset["inputs"]!.AsArray().OfType<JsonObject>())
        {
            var name = input["name"]!.GetValue<string>();
            var value = input["value"]?.GetValue<string>();
            if (string.IsNullOrEmpty(value)) continue;

            if (name.EndsWith("_payload", StringComparison.Ordinal))
            {
                // A payload is one bundle or, for the must-support and
                // inquiry inputs, a list of them.
                JsonNode payload = JsonNode.Parse(value)!;
                var bundles = payload is JsonArray list ? list.OfType<JsonObject>().ToList() : [payload.AsObject()];
                foreach (var bundle in bundles)
                {
                    Repoint(bundle, fixture);
                    if (ServiceTypeFor(name) is { } serviceType)
                        SetServiceType(bundle, serviceType);
                }

                // CHO's own additions to the must-support requests (the kit
                // asks testers to supply requests that elicit every element).
                var additions = MustSupportAdditions(name, bundles);
                if (additions.Count > 0)
                {
                    var extended = payload as JsonArray ?? new JsonArray(payload.DeepClone());
                    foreach (var addition in additions)
                        extended.Add(addition);
                    payload = extended;
                }

                value = payload.ToJsonString();
            }
            else if (name == "subscription_resource")
            {
                value = RepointSubscription(JsonNode.Parse(value)!.AsObject()).ToJsonString();
            }

            inputs[name] = value;
        }

        return inputs;
    }

    /// <summary>The service type (X12 1365) CHO's interop profile approves on sight.</summary>
    public const string ApprovedServiceType = "3";

    /// <summary>The service type the interop profile does not cover, so a request for it is denied.</summary>
    public const string DeniedServiceType = "4";

    /// <summary>A service type neither list names, so with no state on the request it is pended.</summary>
    public const string PendedServiceType = "2";

    private const string ServiceTypeSystem = "https://codesystem.x12.org/005010/1365";

    /// <summary>X12 1032, the claim filing indicator: the system fhir-service reads a Coverage's line of business from.</summary>
    public const string ClaimFilingIndicatorSystem = "https://codesystem.x12.org/005010/1032";

    /// <summary>
    /// The kit's approval, denial and pended examples are the same request, so
    /// each is given the service type that has CHO decide it the way the test
    /// expects. Every other request keeps its own.
    /// </summary>
    public static string? ServiceTypeFor(string input) => input switch
    {
        "approval_pa_submit_request_payload" => ApprovedServiceType,
        "denial_pa_submit_request_payload" => DeniedServiceType,
        "pended_pa_submit_request_payload" => PendedServiceType,
        _ => null,
    };

    /// <summary>
    /// The submitter's identifier on CHO's pended must-support request (PAS-07),
    /// so the must-support inquiry can name that request and no other: every
    /// kit example shares one identifier, and an inquiry by it returns the
    /// latest request made with it.
    /// </summary>
    public const string DocumentationPendIdentifier = "111099-PAS07";

    private const string PatientEventTraceSystem = "http://davinci-pas.org/PATIENT_EVENT_TRACE_NUMBER";

    /// <summary>
    /// The submitter's identifier on CHO's must-support request that CHO
    /// refuses (<see cref="RefusedRequest"/>), and on the inquiry for a
    /// request CHO never received.
    /// </summary>
    public const string RefusedRequestIdentifier = "111099-PASERR";

    /// <summary>The fullUrl of the refused request's Claim, which has no id.</summary>
    public const string UnnamedClaimFullUrl = "urn:uuid:9b2e4f60-1c7a-4d2b-8e51-0f3c6a7d2e14";

    /// <summary>
    /// The requests CHO adds to the kit's must-support inputs (the kit asks
    /// testers to supply requests that elicit every element), each a copy of
    /// the kit's first request:
    /// <list type="bullet">
    /// <item>for a service that needs documentation (<see cref="PendedServiceType"/>):
    /// CHO pends it and asks for what it lacks, so the response carries a PAS
    /// Task and CommunicationRequest;</item>
    /// <item>one CHO refuses (<see cref="RefusedRequest"/>), so the response
    /// carries ClaimResponse.error and a request reference that says why it
    /// cannot name the Claim.</item>
    /// </list>
    /// And the inquiries: one about exactly the pended request, so the
    /// inquiry response carries the Task too, and one about a request CHO
    /// never received, so it carries the error and the absent reference.
    /// </summary>
    public static IReadOnlyList<JsonObject> MustSupportAdditions(string input, IReadOnlyList<JsonObject> kitBundles)
    {
        if (kitBundles.Count == 0
            || input is not ("must_support_pa_submit_request_payload" or "must_support_pa_inquire_request_payload"))
            return [];

        var submit = input == "must_support_pa_submit_request_payload";
        var pend = (JsonObject)kitBundles[0].DeepClone();
        if (!SetSubmitterIdentifier(pend, DocumentationPendIdentifier))
            return [];
        if (submit)
            SetServiceType(pend, PendedServiceType);

        // The refused request is not stored, so an inquiry by its identifier
        // finds nothing.
        var refused = (JsonObject)kitBundles[0].DeepClone();
        SetSubmitterIdentifier(refused, RefusedRequestIdentifier);
        if (submit && !RefusedRequest(refused))
            return [pend];
        return [pend, refused];
    }

    /// <summary>
    /// Makes a conformant request CHO refuses: its last service line asks for
    /// no units (Claim.item.quantity 0), and its Claim has no id, only a <c>urn:uuid</c> fullUrl. The
    /// response then names the Claim by identifier, with a data-absent reason
    /// on the reference. False when the request has no line with a quantity.
    /// </summary>
    public static bool RefusedRequest(JsonObject bundle)
    {
        var entry = ((bundle["entry"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .FirstOrDefault(e => e["resource"]?["resourceType"]?.GetValue<string>() == "Claim");
        if (entry?["resource"] is not JsonObject claim) return false;
        var line = (claim["item"] as JsonArray)?.OfType<JsonObject>().LastOrDefault(i => i["quantity"] is JsonObject);
        if (line is null) return false;

        ((JsonObject)line["quantity"]!)["value"] = 0;
        claim.Remove("id");
        entry["fullUrl"] = UnnamedClaimFullUrl;
        return true;
    }

    /// <summary>Replaces the Claim's patient-event trace number; false when it carries none.</summary>
    public static bool SetSubmitterIdentifier(JsonObject bundle, string value)
    {
        var claim = ((bundle["entry"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(e => e["resource"] as JsonObject)
            .FirstOrDefault(r => r?["resourceType"]?.GetValue<string>() == "Claim");
        var identifier = (claim?["identifier"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(i => i["system"]?.GetValue<string>() == PatientEventTraceSystem);
        if (identifier is null) return false;
        identifier["value"] = value;
        // A fresh Bundle identity for a request the kit did not send.
        if (bundle["identifier"] is JsonObject bundleIdentifier)
            bundleIdentifier["value"] = $"{bundleIdentifier["value"]?.GetValue<string>()}-{value}";
        return true;
    }

    /// <summary>Sets every Claim item's X12 service type code to <paramref name="code"/>.</summary>
    public static void SetServiceType(JsonObject bundle, string code)
    {
        var claims = ((bundle["entry"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(e => e["resource"] as JsonObject)
            .Where(r => r?["resourceType"]?.GetValue<string>() == "Claim");
        foreach (var item in claims.SelectMany(c => (c!["item"] as JsonArray)?.OfType<JsonObject>() ?? []))
        {
            foreach (var coding in (item["productOrService"]?["coding"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                if (coding["system"]?.GetValue<string>() == ServiceTypeSystem)
                    coding["code"] = code;
            }
        }
    }

    /// <summary>
    /// The kit's example Subscription filters on its example organization and
    /// ended in 2020. It is re-pointed at the seeded provider, the one the
    /// kit's requests are submitted for, and given an end in the future. Its
    /// notification credential is the session's (<see cref="InfernoSuiteScenario.PasNotificationToken"/>)
    /// alone: the kit adds that one beside the example's, and a subscriber
    /// cannot read two. The kit points the endpoint at itself.
    /// </summary>
    public static JsonObject RepointSubscription(JsonObject subscription)
    {
        foreach (var filter in (subscription["_criteria"]?["extension"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (filter["url"]?.GetValue<string>()?.EndsWith("/backport-filter-criteria", StringComparison.Ordinal) == true)
                filter["valueString"] = $"org-identifier={ProviderNpi}";
        }

        subscription["end"] = DateTimeOffset.UtcNow.AddYears(1).ToString("yyyy-MM-ddTHH:mm:ssZ");
        if (subscription["channel"] is JsonObject channel)
            channel["header"] = new JsonArray($"Authorization: Bearer {InfernoSuiteScenario.PasNotificationToken}");
        return subscription;
    }

    /// <summary>The Subscription the kit creates, from its own example, re-pointed by <see cref="RepointSubscription"/>.</summary>
    public static string Subscription(ChoSeedFixture fixture)
        => Inputs(fixture).TryGetValue("subscription_resource", out var subscription)
            ? subscription
            : throw new InteropEnvironmentException("The PAS kit's server preset carries no subscription_resource.");

    /// <summary>
    /// Re-points a PAS request (or inquiry) bundle at the seeded member, their
    /// Coverage, and the seeded billing provider.
    /// </summary>
    public static void Repoint(JsonObject bundle, ChoSeedFixture fixture)
    {
        var entries = (bundle["entry"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var claim = entries.Select(e => e["resource"] as JsonObject)
            .FirstOrDefault(r => r?["resourceType"]?.GetValue<string>() == "Claim");
        var providerRef = claim?["provider"]?["reference"]?.GetValue<string>();

        foreach (var resource in entries.Select(e => e["resource"]).OfType<JsonObject>())
        {
            switch (resource["resourceType"]?.GetValue<string>())
            {
                case "Patient":
                    resource["identifier"] = new JsonArray(new JsonObject
                    {
                        ["type"] = new JsonObject
                        {
                            ["coding"] = new JsonArray(new JsonObject
                            {
                                ["system"] = "http://terminology.hl7.org/CodeSystem/v2-0203",
                                ["code"] = "MB",
                            }),
                        },
                        ["system"] = MemberSystem,
                        ["value"] = fixture.Member.MemberId,
                    });
                    // The seeded member's name, keeping the rest of the kit's
                    // (prefix, suffix, use), which its must-support tests look for.
                    var name = ((resource["name"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?.DeepClone()
                        as JsonObject) ?? new JsonObject();
                    name["family"] = fixture.Member.LastName;
                    name["given"] = new JsonArray(fixture.Member.FirstName);
                    name.Remove("text");
                    resource["name"] = new JsonArray(name);
                    resource["birthDate"] = fixture.Member.DateOfBirth.Length >= 10
                        ? fixture.Member.DateOfBirth[..10]
                        : fixture.Member.DateOfBirth;
                    resource["gender"] = fixture.Member.Gender switch { "M" => "male", "F" => "female", _ => "unknown" };
                    break;

                case "Coverage":
                    resource["subscriberId"] = fixture.Member.MemberId;
                    resource["status"] = "active";
                    // The kit's example types its Coverage as v3 EHCPOL, which
                    // names no line of business, so CHO pended every request
                    // without adjudicating it (run 10). The seeded coverage's
                    // own insurance line, as its X12 claim filing indicator.
                    if (!string.IsNullOrEmpty(fixture.Coverage.InsuranceLineCode))
                    {
                        resource["type"] = new JsonObject
                        {
                            ["coding"] = new JsonArray(new JsonObject
                            {
                                ["system"] = ClaimFilingIndicatorSystem,
                                ["code"] = fixture.Coverage.InsuranceLineCode,
                            }),
                        };
                    }
                    break;

                case "Organization" when Matches(resource, providerRef):
                case "Practitioner" when Matches(resource, providerRef):
                    var identifiers = (resource["identifier"] as JsonArray) ?? new JsonArray();
                    var npi = identifiers.OfType<JsonObject>()
                        .FirstOrDefault(i => i["system"]?.GetValue<string>() == NpiSystem);
                    if (npi is null)
                    {
                        identifiers.Add(new JsonObject { ["system"] = NpiSystem, ["value"] = ProviderNpi });
                    }
                    else
                    {
                        npi["value"] = ProviderNpi;
                    }
                    resource["identifier"] = identifiers;
                    break;
            }
        }
    }

    private static bool Matches(JsonObject resource, string? reference)
        => reference is not null
           && resource["id"]?.GetValue<string>() is { } id
           && reference.EndsWith($"{resource["resourceType"]!.GetValue<string>()}/{id}", StringComparison.Ordinal);
}
