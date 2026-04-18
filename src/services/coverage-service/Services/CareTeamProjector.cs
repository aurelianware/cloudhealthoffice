using CoverageService.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace CoverageService.Services;

/// <summary>
/// Projects an aggregate of <see cref="CareTeamMember"/> entries (PCP today;
/// specialists, care managers, value-based partners later) into a FHIR R4
/// CareTeam resource aligned to the US Core 6.1 CareTeam profile.
///
/// Design notes for the next person to extend this:
/// <list type="bullet">
///   <item>Each member becomes one <c>participant[]</c> entry with its own
///         <c>role</c>, <c>member</c> Practitioner reference, and <c>period</c>.</item>
///   <item>The projector itself does NOT know about specialists vs care
///         managers — it just renders whatever <see cref="CareTeamMember"/>s it
///         is handed. Source services decide whose roles to include.</item>
///   <item>No empty placeholders are emitted. If only PCP is supplied the
///         resulting CareTeam has exactly one participant.</item>
/// </list>
/// </summary>
public interface ICareTeamProjector
{
    JsonObject Project(string memberId, Coverage? coverage, IEnumerable<CareTeamMember> members);
}

/// <summary>
/// Input row for <see cref="ICareTeamProjector"/>. Decoupled from
/// <see cref="PcpAssignment"/> so future sources (care management service,
/// specialist referrals, embedded behavioral health) can populate the same
/// projector without taking a dependency on the PCP types.
/// </summary>
public sealed class CareTeamMember
{
    public CareTeamRole Role { get; init; }
    public string PractitionerNpi { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public DateTime EffectiveDate { get; init; }
    public DateTime? EndDate { get; init; }

    /// <summary>
    /// Source of this care-team entry — used downstream to attribute the row
    /// (e.g., "pcp-assignment-service", "care-management-service"). Free-form on
    /// purpose; source services should pick a stable string.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    public static CareTeamMember FromPcp(PcpAssignment a) => new()
    {
        Role = CareTeamRole.PrimaryCareProvider,
        PractitionerNpi = a.ProviderNpi,
        DisplayName = a.ProviderName ?? a.ProviderNpi,
        EffectiveDate = a.EffectiveDate,
        EndDate = a.EndDate,
        Source = "pcp-assignment-service"
    };
}

/// <summary>
/// Care-team roles we know how to render today. Add new values as new sources
/// land — keep the enum stable; the int values are persisted nowhere.
/// </summary>
public enum CareTeamRole
{
    PrimaryCareProvider = 1,
    Specialist = 2,
    CareManager = 3,
    BehavioralHealth = 4
}

public sealed class CareTeamProjector : ICareTeamProjector
{
    // Practitioner role codes (FHIR + US Core care-team-member role).
    private const string PractitionerRoleSystem = "http://terminology.hl7.org/CodeSystem/practitioner-role";
    private const string NpiSystem = "http://hl7.org/fhir/sid/us-npi";
    private const string UsCoreCareTeamProfile = "http://hl7.org/fhir/us/core/StructureDefinition/us-core-careteam";

    public JsonObject Project(string memberId, Coverage? coverage, IEnumerable<CareTeamMember> members)
    {
        ArgumentNullException.ThrowIfNull(memberId);
        ArgumentNullException.ThrowIfNull(members);

        var memberList = members.ToList();

        var careTeam = new JsonObject
        {
            ["resourceType"] = "CareTeam",
            ["id"] = $"care-team-{memberId}",
            ["status"] = MapStatus(coverage, memberList),
            ["subject"] = new JsonObject
            {
                ["reference"] = $"Patient/{memberId}"
            },
            ["meta"] = new JsonObject
            {
                ["profile"] = new JsonArray(UsCoreCareTeamProfile)
            }
        };

        if (memberList.Count > 0)
        {
            var earliest = memberList.Min(m => m.EffectiveDate);
            var latestEnd = memberList.All(m => m.EndDate.HasValue)
                ? memberList.Max(m => m.EndDate!.Value)
                : (DateTime?)null;

            var period = new JsonObject { ["start"] = earliest.ToString("o") };
            if (latestEnd.HasValue) period["end"] = latestEnd.Value.ToString("o");
            careTeam["period"] = period;
        }

        if (memberList.Count > 0)
        {
            var participants = new JsonArray();
            foreach (var m in memberList)
            {
                participants.Add(BuildParticipant(m));
            }
            careTeam["participant"] = participants;
        }

        return careTeam;
    }

    private static JsonObject BuildParticipant(CareTeamMember m)
    {
        var (code, display) = MapRole(m.Role);

        var participant = new JsonObject
        {
            ["role"] = new JsonArray(new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject
                {
                    ["system"] = PractitionerRoleSystem,
                    ["code"] = code,
                    ["display"] = display
                })
            }),
            ["member"] = new JsonObject
            {
                ["type"] = "Practitioner",
                ["identifier"] = new JsonObject
                {
                    ["system"] = NpiSystem,
                    ["value"] = m.PractitionerNpi
                },
                ["display"] = m.DisplayName
            }
        };

        var period = new JsonObject { ["start"] = m.EffectiveDate.ToString("o") };
        if (m.EndDate.HasValue) period["end"] = m.EndDate.Value.ToString("o");
        participant["period"] = period;

        return participant;
    }

    private static string MapStatus(Coverage? coverage, IReadOnlyList<CareTeamMember> members)
    {
        if (coverage != null && coverage.Status == CoverageStatus.Terminated) return "inactive";
        if (members.Count == 0) return "proposed";
        // If every participant has been ended, the team is inactive.
        if (members.All(m => m.EndDate.HasValue && m.EndDate.Value <= DateTime.UtcNow)) return "inactive";
        return "active";
    }

    private static (string code, string display) MapRole(CareTeamRole role) => role switch
    {
        CareTeamRole.PrimaryCareProvider => ("doctor", "Primary Care Provider"),
        CareTeamRole.Specialist => ("doctor", "Specialist"),
        CareTeamRole.CareManager => ("ict", "Care Manager"),
        CareTeamRole.BehavioralHealth => ("doctor", "Behavioral Health"),
        _ => ("doctor", role.ToString())
    };
}
