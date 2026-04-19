using System.Collections.Generic;
using System.Text.Json.Nodes;
using MemberService.Models;

namespace MemberService.Services;

/// <summary>
/// Projects <see cref="MemberAlert"/> records into FHIR R4 Flag resources
/// (US Core profile). Hand-built JSON to stay consistent with
/// <see cref="FhirPatientProjector"/> — no Hl7.Fhir.R4 dependency.
/// </summary>
public interface IFhirFlagProjector
{
    /// <summary>Project a single alert to a FHIR Flag resource.</summary>
    JsonObject Project(MemberAlert alert);

    /// <summary>Wrap a set of alerts in a FHIR searchset Bundle.</summary>
    JsonObject ProjectBundle(IEnumerable<MemberAlert> alerts);
}

public sealed class FhirFlagProjector : IFhirFlagProjector
{
    private const string FlagCategorySystem = "http://terminology.hl7.org/CodeSystem/flag-category";
    private const string CodeSystem = "https://cloudhealthoffice.com/fhir/CodeSystem/member-alert-type";

    public JsonObject Project(MemberAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var flag = new JsonObject
        {
            ["resourceType"] = "Flag",
            ["id"] = alert.Id,
            // FHIR Flag.status: active | inactive | entered-in-error.
            // Members in the past or end-dated map to "inactive"; future-dated
            // alerts are not in scope for this projector (callers filter first).
            ["status"] = alert.IsActive() ? "active" : "inactive"
        };

        // Severity → flag-category. FHIR Flag has no native severity slot, but
        // category is the conventional place for prioritisation hints; portals
        // and downstream subscribers key off the code below for the actual type.
        flag["category"] = new JsonArray
        {
            new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["system"] = FlagCategorySystem,
                        ["code"] = MapSeverityToCategoryCode(alert.Severity),
                        ["display"] = alert.Severity.ToString()
                    }
                },
                ["text"] = $"{alert.Severity} alert"
            }
        };

        flag["code"] = new JsonObject
        {
            ["coding"] = new JsonArray
            {
                new JsonObject
                {
                    ["system"] = CodeSystem,
                    ["code"] = alert.AlertType.ToString(),
                    ["display"] = HumanizeAlertType(alert.AlertType)
                }
            },
            ["text"] = alert.Reason
        };

        flag["subject"] = new JsonObject
        {
            ["type"] = "Patient",
            ["identifier"] = new JsonObject
            {
                ["system"] = FhirIdentifierSystems.MemberId,
                ["value"] = alert.MemberId
            }
        };

        var period = new JsonObject { ["start"] = alert.StartDate.ToString("o") };
        if (alert.EndDate.HasValue) period["end"] = alert.EndDate.Value.ToString("o");
        flag["period"] = period;

        if (!string.IsNullOrEmpty(alert.RequiredAction))
        {
            // No standard FHIR Flag slot for "required action" — surface via
            // an extension so downstream consumers can read it without parsing
            // the free-text reason.
            flag["extension"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = "https://cloudhealthoffice.com/fhir/StructureDefinition/required-action",
                    ["valueString"] = alert.RequiredAction
                }
            };
        }

        flag["meta"] = new JsonObject
        {
            ["lastUpdated"] = (alert.EndDate ?? alert.CreatedDate).ToString("o"),
            ["profile"] = new JsonArray("http://hl7.org/fhir/us/core/StructureDefinition/us-core-flag")
        };

        return flag;
    }

    public JsonObject ProjectBundle(IEnumerable<MemberAlert> alerts)
    {
        ArgumentNullException.ThrowIfNull(alerts);

        var entries = new JsonArray();
        var count = 0;
        foreach (var alert in alerts)
        {
            entries.Add(new JsonObject
            {
                ["fullUrl"] = $"Flag/{alert.Id}",
                ["resource"] = Project(alert)
            });
            count++;
        }

        return new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "searchset",
            ["total"] = count,
            ["entry"] = entries
        };
    }

    private static string MapSeverityToCategoryCode(MemberAlertSeverity severity) => severity switch
    {
        MemberAlertSeverity.Critical => "safety",
        MemberAlertSeverity.Warning => "admin",
        _ => "clinical"
    };

    private static string HumanizeAlertType(MemberAlertType type) => type switch
    {
        MemberAlertType.HighRisk => "High Risk",
        MemberAlertType.LitigationHold => "Litigation Hold",
        MemberAlertType.DoNotContact => "Do Not Contact",
        MemberAlertType.VIP => "VIP",
        MemberAlertType.CustodyDispute => "Custody Dispute",
        MemberAlertType.LanguageRequirement => "Language Requirement",
        MemberAlertType.AccessibilityNeed => "Accessibility Need",
        MemberAlertType.SecurityFreeze => "Security Freeze",
        MemberAlertType.KnownFraudRisk => "Known Fraud Risk",
        MemberAlertType.EligibilityDispute => "Eligibility Dispute",
        _ => type.ToString()
    };
}
