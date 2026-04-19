using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace CoverageService.Models;

/// <summary>
/// Immutable history row for a member's Primary Care Provider assignment.
/// A new row is written on every assignment; the prior open row is closed by
/// stamping <see cref="EndDate"/>. Current PCP = the row with null <see cref="EndDate"/>.
///
/// Denormalized <c>Coverage.PcpNpi / PcpName / PcpAssignmentDate / PreviousPcpNpi</c>
/// remain for fast read paths (eligibility, capitation roster) but this collection
/// is the source of truth for history / audit / FHIR CareTeam projection.
/// </summary>
public class PcpAssignment
{
    /// <summary>Multi-tenant partition key.</summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Document id.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Member this assignment belongs to.</summary>
    [Required]
    public string MemberId { get; set; } = string.Empty;

    /// <summary>
    /// Coverage record this assignment was written against. Kept for audit — if the
    /// member has multiple active coverages (e.g., Medical + Dental), each gets
    /// its own history row so termination of one coverage doesn't orphan the other.
    /// </summary>
    [Required]
    public string CoverageId { get; set; } = string.Empty;

    /// <summary>Provider-service record id (opaque).</summary>
    public string? ProviderId { get; set; }

    /// <summary>National Provider Identifier (10 digits). The stable external id.</summary>
    [Required]
    [StringLength(10, MinimumLength = 10)]
    public string ProviderNpi { get; set; } = string.Empty;

    /// <summary>Denormalized provider display name, captured at assignment time.</summary>
    public string? ProviderName { get; set; }

    /// <summary>When this assignment becomes effective.</summary>
    [Required]
    public DateTime EffectiveDate { get; set; }

    /// <summary>
    /// When this assignment was closed (i.e. a newer assignment took over or the
    /// member terminated). Null = currently active.
    /// </summary>
    public DateTime? EndDate { get; set; }

    /// <summary>Human-readable reason for this change, if supplied.</summary>
    [StringLength(500)]
    public string? AssignmentReason { get; set; }

    /// <summary>Who / what initiated the change.</summary>
    [Required]
    public PcpAssignmentSource AssignmentSource { get; set; } = PcpAssignmentSource.MemberChoice;

    /// <summary>
    /// Network-status snapshot captured at assignment time. Never updated.
    ///
    /// If the provider later terminates their network participation, this field
    /// still reflects the status that was true when the assignment was written —
    /// that's the point of an audit trail. For live UI status, always fetch via
    /// provider-service; do NOT treat this value as current.
    /// </summary>
    [Required]
    public string NetworkStatusAtAssignment { get; set; } = "Unknown";

    /// <summary>Audit: user/system that created the assignment.</summary>
    [StringLength(200)]
    public string? AssignedBy { get; set; }

    /// <summary>Audit: creation timestamp.</summary>
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Origin of a PCP assignment. Drives downstream reporting (auto-assignment rate,
/// member-choice rate) and regulatory reporting for Medicaid/Medicare.
///
/// Serialized as a string on the wire — member-service and the portal
/// deserialize into string-typed DTOs, and the value is part of the PCP
/// history API contract. Do not remove the converter.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PcpAssignmentSource
{
    /// <summary>Member actively chose the PCP (portal, phone, paper form).</summary>
    MemberChoice = 1,

    /// <summary>System auto-assigned (geo-match, round-robin, plan default).</summary>
    AutoAssigned = 2,

    /// <summary>Admin / back-office override (CSR, network ops).</summary>
    AdminAssigned = 3
}
