using System.Text.Json.Nodes;
using ClaimsService.Models;

namespace ClaimsService.Fhir;

/// <summary>
/// The legacy claim line stores a procedure without its X12 qualifier. Infer
/// only recognizable code shapes, never label every procedure as AMA CPT.
/// Unknown/local identifiers stay local pending explicit source-system data.
/// This does not change adjudication, pricing or the stored claim.
/// </summary>
internal static class ClaimProcedureCoding
{
    public static JsonObject Concept(ClaimLine line)
    {
        var code = line.ProcedureCode.Trim();
        var system = "urn:cho:procedure-code";
        if (code.Length == 5)
        {
            var upper = code.ToUpperInvariant();
            if (char.IsAsciiLetter(upper[0]) && upper.Skip(1).All(char.IsAsciiDigit))
            {
                system = upper[0] == 'D'
                    ? "http://www.ada.org/cdt"
                    : "https://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets";
                code = upper;
            }
            else if (upper.All(char.IsAsciiDigit)
                || (upper.Take(4).All(char.IsAsciiDigit) && char.IsAsciiLetter(upper[4])))
            {
                system = "http://www.ama-assn.org/go/cpt";
                code = upper;
            }
        }

        var concept = new JsonObject
        {
            ["coding"] = new JsonArray { new JsonObject { ["system"] = system, ["code"] = code } },
        };
        // Source descriptions need not be canonical terminology displays.
        if (!string.IsNullOrWhiteSpace(line.ProcedureDescription))
            concept["text"] = line.ProcedureDescription;
        return concept;
    }
}
