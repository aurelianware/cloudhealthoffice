using System.Text.Json.Nodes;
using CareManagementService.Models;

namespace CareManagementService.Services.Fhir;

/// <summary>
/// Projects a care case as FHIR R4 CarePlan, Goal and CareTeam.
///
/// The case is the record; these are a read-only view of it, built on demand.
/// Nothing is stored twice, so the projection can never drift from the case
/// it describes. Resources are emitted as JSON nodes rather than typed
/// Hl7.Fhir models, matching how the rest of the platform projects FHIR
/// without taking the SDK into every service.
///
/// Goal achievement statuses map straight through, which is why
/// <see cref="CareGoalStatus"/> is spelled the way it is.
/// </summary>
public static class CarePlanProjector
{
    private const string AchievementSystem = "http://terminology.hl7.org/CodeSystem/goal-achievement";
    private const string CareTeamRoleSystem = "http://terminology.hl7.org/CodeSystem/participant-role";

    /// <summary>The case's care plan as a FHIR CarePlan, or null when there is no plan.</summary>
    public static JsonObject? ToCarePlan(CareCase careCase)
    {
        ArgumentNullException.ThrowIfNull(careCase);

        if (careCase.CarePlan is not { } plan) return null;

        var resource = new JsonObject
        {
            ["resourceType"] = "CarePlan",
            ["id"] = careCase.Id,
            ["status"] = MapPlanStatus(plan.Status),
            // Care management plans are plans of record, not orders or proposals.
            ["intent"] = "plan",
            ["subject"] = Reference($"Patient/{careCase.MemberId}"),
            ["category"] = new JsonArray
            {
                new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["system"] = "http://snomed.info/sct",
                            ["code"] = "734163000",
                            ["display"] = "Care plan",
                        },
                    },
                    ["text"] = careCase.Program.ToString(),
                },
            },
            ["title"] = $"{careCase.Program} care plan",
        };

        if (plan.ActivatedDate is { } activated)
            resource["period"] = new JsonObject
            {
                ["start"] = activated.ToString("yyyy-MM-dd"),
            };

        if (careCase.ClosedDate is { } closed && resource["period"] is JsonObject period)
            period["end"] = closed.ToString("yyyy-MM-dd");

        if (plan.Goals.Count > 0)
            resource["goal"] = new JsonArray(
                plan.Goals.Select(g => (JsonNode)Reference($"Goal/{careCase.Id}-{g.Id}")).ToArray());

        if (careCase.CareTeam.Count > 0)
            resource["careTeam"] = new JsonArray(Reference($"CareTeam/{careCase.Id}"));

        // An intervention is what the plan actually asks anyone to do, so it
        // is the activity detail. The description carries who and how often,
        // because FHIR has no field for a care manager's "weekly call".
        if (plan.Interventions.Count > 0)
            resource["activity"] = new JsonArray(
                plan.Interventions.Select(i => (JsonNode)new JsonObject
                {
                    ["detail"] = new JsonObject
                    {
                        ["kind"] = "ServiceRequest",
                        ["status"] = i.Completed ? "completed" : "in-progress",
                        ["description"] = Describe(i),
                    },
                }).ToArray());

        // Problems and barriers are both things the plan addresses, and the
        // distinction is CHO's, not FHIR's, so both go in addresses/note form.
        if (plan.Problems.Count > 0)
            resource["addresses"] = new JsonArray(
                plan.Problems.Select(p => (JsonNode)new JsonObject
                {
                    ["concept"] = new JsonObject
                    {
                        ["text"] = p.Description,
                        ["coding"] = p.DiagnosisCode is { Length: > 0 } code
                            ? new JsonArray
                            {
                                new JsonObject
                                {
                                    ["system"] = "http://hl7.org/fhir/sid/icd-10-cm",
                                    ["code"] = code,
                                },
                            }
                            : null,
                    },
                }).ToArray());

        var openBarriers = plan.Barriers.Where(b => !b.Resolved).ToList();
        if (openBarriers.Count > 0)
            resource["note"] = new JsonArray(
                openBarriers.Select(b => (JsonNode)new JsonObject
                {
                    ["text"] = b.Category is { Length: > 0 } category
                        ? $"Barrier ({category}): {b.Description}"
                        : $"Barrier: {b.Description}",
                }).ToArray());

        return resource;
    }

    /// <summary>The plan's goals as FHIR Goal resources.</summary>
    public static IReadOnlyList<JsonObject> ToGoals(CareCase careCase)
    {
        ArgumentNullException.ThrowIfNull(careCase);

        if (careCase.CarePlan is not { } plan) return [];

        return plan.Goals.Select(goal =>
        {
            var resource = new JsonObject
            {
                ["resourceType"] = "Goal",
                ["id"] = $"{careCase.Id}-{goal.Id}",
                ["lifecycleStatus"] = MapGoalLifecycle(goal.Status),
                ["subject"] = Reference($"Patient/{careCase.MemberId}"),
                ["description"] = new JsonObject { ["text"] = goal.Description },
                ["achievementStatus"] = new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["system"] = AchievementSystem,
                            ["code"] = MapAchievement(goal.Status),
                        },
                    },
                },
            };

            if (goal.TargetDate is { } target)
                resource["target"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["dueDate"] = target.ToString("yyyy-MM-dd"),
                        ["measure"] = goal.Measure is { Length: > 0 } measure
                            ? new JsonObject { ["text"] = measure }
                            : null,
                    },
                };

            if (goal.Progress.Count > 0)
                resource["note"] = new JsonArray(
                    goal.Progress.Select(p => (JsonNode)new JsonObject
                    {
                        ["time"] = p.RecordedAt.ToString("o"),
                        ["text"] = p.Note,
                    }).ToArray());

            return resource;
        }).ToList();
    }

    /// <summary>The case's care team as a FHIR CareTeam, or null when nobody is on it.</summary>
    public static JsonObject? ToCareTeam(CareCase careCase)
    {
        ArgumentNullException.ThrowIfNull(careCase);

        if (careCase.CareTeam.Count == 0) return null;

        return new JsonObject
        {
            ["resourceType"] = "CareTeam",
            ["id"] = careCase.Id,
            ["status"] = careCase.Status is CareCaseStatus.Closed or CareCaseStatus.Declined
                ? "inactive"
                : "active",
            ["subject"] = Reference($"Patient/{careCase.MemberId}"),
            ["name"] = $"{careCase.Program} care team",
            ["participant"] = new JsonArray(
                careCase.CareTeam.Select(m => (JsonNode)new JsonObject
                {
                    ["role"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["text"] = m.Role,
                            ["coding"] = m.External
                                ? null
                                : new JsonArray
                                {
                                    new JsonObject { ["system"] = CareTeamRoleSystem, ["code"] = "PART" },
                                },
                        },
                    },
                    // An NPI identifies a practitioner; a family caregiver has
                    // no NPI and is named rather than referenced, because
                    // there is no resource in CHO to point at.
                    ["member"] = m.Npi is { Length: > 0 } npi
                        ? Reference($"Practitioner/{npi}", m.Name)
                        : new JsonObject { ["display"] = m.Name },
                }).ToArray()),
        };
    }

    private static string Describe(CareIntervention intervention)
    {
        var parts = new List<string> { intervention.Description };
        if (intervention.ResponsibleParty is { Length: > 0 } who) parts.Add($"responsible: {who}");
        if (intervention.Frequency is { Length: > 0 } frequency) parts.Add($"frequency: {frequency}");
        return string.Join("; ", parts);
    }

    private static JsonObject Reference(string reference, string? display = null)
    {
        var node = new JsonObject { ["reference"] = reference };
        if (display is { Length: > 0 }) node["display"] = display;
        return node;
    }

    private static string MapPlanStatus(CarePlanStatus status) => status switch
    {
        CarePlanStatus.Draft     => "draft",
        CarePlanStatus.Active    => "active",
        CarePlanStatus.OnHold    => "on-hold",
        CarePlanStatus.Completed => "completed",
        CarePlanStatus.Cancelled => "revoked",
        _                        => "unknown",
    };

    private static string MapGoalLifecycle(CareGoalStatus status) => status switch
    {
        CareGoalStatus.Proposed    => "proposed",
        CareGoalStatus.InProgress  => "active",
        CareGoalStatus.Achieved    => "completed",
        CareGoalStatus.NotAchieved => "completed",
        CareGoalStatus.Cancelled   => "cancelled",
        _                          => "proposed",
    };

    private static string MapAchievement(CareGoalStatus status) => status switch
    {
        CareGoalStatus.InProgress  => "in-progress",
        CareGoalStatus.Achieved    => "achieved",
        CareGoalStatus.NotAchieved => "not-achieved",
        _                          => "in-progress",
    };
}
