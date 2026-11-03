using System.Globalization;
using System.Text.Json.Nodes;

namespace CloudHealthOffice.Tools.PaTimelinessChallenge;

/// <summary>
/// Builds the Da Vinci PAS request Bundle for a live case: the Claim
/// (use = preauthorization) and the Patient, Coverage and requesting
/// Practitioner it references. Plain JSON, so the tool carries no FHIR SDK.
/// The service location's state is left out on purpose: CHO's state-specific
/// rules then pend the request for review, and the validator decides it.
/// </summary>
public static class PasRequestBuilder
{
    public const string NpiSystem = "http://hl7.org/fhir/sid/us-npi";
    public const string CptSystem = "http://www.ama-assn.org/go/cpt";
    public const string HcpcsSystem = "https://bluebutton.cms.gov/resources/codesystem/hcpcs";
    public const string NdcSystem = "http://hl7.org/fhir/sid/ndc";
    public const string ClaimFilingSystem = "https://codesystem.x12.org/005010/1032";
    public const string ServiceTypeSystem = "https://codesystem.x12.org/005010/1365";
    public const string ProcessPrioritySystem = "http://terminology.hl7.org/CodeSystem/processpriority";
    public const string MemberIdSystem = "urn:cho:pa-timeliness-challenge:member-id";
    public const string RequestIdentifierSystem = "urn:cho:pa-timeliness-challenge:request";

    /// <summary>
    /// X12 claim filing indicator for the line of business. PAS has no code for
    /// an Exchange (QHP) plan: <c>CI</c> (commercial) is the closest, so CHO
    /// records an Exchange request as Commercial. The validator reports that.
    /// </summary>
    public static string ClaimFilingCode(PaLineOfBusiness lineOfBusiness) => lineOfBusiness switch
    {
        PaLineOfBusiness.Medicare => "16",
        PaLineOfBusiness.Medicaid => "MC",
        _ => "CI",
    };

    public static string SystemUrl(string codeSystem) => codeSystem switch
    {
        "CPT" => CptSystem,
        "HCPCS" => HcpcsSystem,
        "NDC" => NdcSystem,
        _ => codeSystem,
    };

    public static JsonObject Build(PaCase c, DateTimeOffset now)
    {
        if (c.Cohort != PaCohort.Live)
            throw new ArgumentException($"{c.CaseId} is a history case; only live cases are submitted.");
        var patientId = "pat-" + c.CaseId;
        var coverageId = "cov-" + c.CaseId;
        var practitionerId = "prac-" + c.CaseId;
        var serviceStart = DateOnly.FromDateTime(now.UtcDateTime.AddDays(7));

        var items = new JsonArray();
        foreach (var line in c.Lines)
        {
            items.Add(new JsonObject
            {
                ["sequence"] = line.LineNumber,
                ["category"] = Concept(ServiceTypeSystem, c.ServiceTypeCode),
                ["productOrService"] = Concept(SystemUrl(line.CodeSystem), line.ProcedureCode, line.Description),
                ["quantity"] = new JsonObject { ["value"] = line.RequestedUnits, ["unit"] = "units" },
                ["servicedPeriod"] = new JsonObject
                {
                    ["start"] = Date(serviceStart),
                    ["end"] = Date(serviceStart.AddDays(30)),
                },
            });
        }

        var diagnoses = new JsonArray();
        for (var i = 0; i < c.DiagnosisCodes.Count; i++)
        {
            diagnoses.Add(new JsonObject
            {
                ["sequence"] = i + 1,
                ["diagnosisCodeableConcept"] = Concept("http://hl7.org/fhir/sid/icd-10-cm", c.DiagnosisCodes[i]),
            });
        }

        var claim = new JsonObject
        {
            ["resourceType"] = "Claim",
            ["id"] = "claim-" + c.CaseId,
            ["identifier"] = new JsonArray(new JsonObject { ["system"] = RequestIdentifierSystem, ["value"] = c.CaseId }),
            ["status"] = "active",
            ["type"] = Concept("http://terminology.hl7.org/CodeSystem/claim-type", "professional"),
            ["use"] = "preauthorization",
            ["patient"] = Reference($"Patient/{patientId}"),
            ["created"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["insurer"] = Reference("Organization/cho-payer"),
            ["provider"] = Reference($"Practitioner/{practitionerId}"),
            ["priority"] = Concept(ProcessPrioritySystem, PaTimeframes.IsExpedited(c.LevelOfService) ? "stat" : "normal"),
            ["insurance"] = new JsonArray(new JsonObject
            {
                ["sequence"] = 1,
                ["focal"] = true,
                ["coverage"] = Reference($"Coverage/{coverageId}"),
            }),
            ["diagnosis"] = diagnoses,
            ["item"] = items,
        };

        var patient = new JsonObject
        {
            ["resourceType"] = "Patient",
            ["id"] = patientId,
            ["identifier"] = new JsonArray(new JsonObject
            {
                ["type"] = Concept("http://terminology.hl7.org/CodeSystem/v2-0203", "MB"),
                ["system"] = MemberIdSystem,
                ["value"] = c.Member.MemberId,
            }),
            ["name"] = new JsonArray(new JsonObject
            {
                ["family"] = c.Member.LastName,
                ["given"] = new JsonArray(c.Member.FirstName),
            }),
            ["birthDate"] = Date(c.Member.BirthDate),
            ["gender"] = "unknown",
        };

        var coverage = new JsonObject
        {
            ["resourceType"] = "Coverage",
            ["id"] = coverageId,
            ["status"] = "active",
            ["type"] = Concept(ClaimFilingSystem, ClaimFilingCode(c.LineOfBusiness)),
            ["subscriberId"] = c.Member.MemberId,
            ["beneficiary"] = Reference($"Patient/{patientId}"),
            ["payor"] = new JsonArray(Reference("Organization/cho-payer")),
        };

        var practitioner = new JsonObject
        {
            ["resourceType"] = "Practitioner",
            ["id"] = practitionerId,
            ["identifier"] = new JsonArray(new JsonObject { ["system"] = NpiSystem, ["value"] = c.RequestingProvider.Npi }),
            ["name"] = new JsonArray(new JsonObject
            {
                ["family"] = c.RequestingProvider.Name,
                ["given"] = new JsonArray("Synthetic"),
            }),
        };

        return new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["identifier"] = new JsonObject { ["system"] = RequestIdentifierSystem, ["value"] = c.CaseId },
            ["type"] = "collection",
            ["timestamp"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["entry"] = new JsonArray(
                Entry(claim), Entry(patient), Entry(coverage), Entry(practitioner)),
        };
    }

    private static JsonObject Entry(JsonObject resource) => new()
    {
        ["fullUrl"] = $"urn:uuid:{DeterministicUuid(resource["resourceType"]!.GetValue<string>() + "/" + resource["id"]!.GetValue<string>())}",
        ["resource"] = resource,
    };

    private static JsonObject Concept(string system, string code, string? display = null)
    {
        var coding = new JsonObject { ["system"] = system, ["code"] = code };
        if (display is not null) coding["display"] = display;
        return new JsonObject { ["coding"] = new JsonArray(coding) };
    }

    private static JsonObject Reference(string reference) => new() { ["reference"] = reference };

    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static Guid DeterministicUuid(string name)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name));
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}
