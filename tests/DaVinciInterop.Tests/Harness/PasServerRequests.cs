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
                var bundle = JsonNode.Parse(value)!.AsObject();
                Repoint(bundle, fixture);
                value = bundle.ToJsonString();
            }

            inputs[name] = value;
        }

        return inputs;
    }

    /// <summary>The Subscription the kit creates, from its own example, unchanged: the kit points it at itself.</summary>
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
                    resource["name"] = new JsonArray(new JsonObject
                    {
                        ["family"] = fixture.Member.LastName,
                        ["given"] = new JsonArray(fixture.Member.FirstName),
                    });
                    resource["birthDate"] = fixture.Member.DateOfBirth.Length >= 10
                        ? fixture.Member.DateOfBirth[..10]
                        : fixture.Member.DateOfBirth;
                    resource["gender"] = fixture.Member.Gender switch { "M" => "male", "F" => "female", _ => "unknown" };
                    break;

                case "Coverage":
                    resource["subscriberId"] = fixture.Member.MemberId;
                    resource["status"] = "active";
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
