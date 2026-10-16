using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace ClaimsService.Models;

/// <summary>A reviewer's decision on one edit.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EditDecision
{
    /// <summary>The edit stands: the affected line (or the units over the limit) is denied with the edit's CARC.</summary>
    Uphold,

    /// <summary>The edit does not apply to this claim: re-adjudication suppresses it.</summary>
    Override,
}

/// <summary>
/// A reviewer's decision on one edit, keyed by the edit's identity
/// (<see cref="Services.Adjudication.EditIdentity"/>). Recorded for every edit
/// on the claim when the reviewer finishes — untouched edits as
/// <see cref="EditDecision.Uphold"/> — so the audit shows a person looked at
/// each one.
/// </summary>
[BsonIgnoreExtraElements]
public class EditDisposition
{
    [StringLength(200)]
    public string EditKey { get; set; } = string.Empty;

    /// <summary>NcciPair, Mue or Duplicate.</summary>
    [StringLength(20)]
    public string EditType { get; set; } = string.Empty;

    [StringLength(20)]
    public string RuleId { get; set; } = string.Empty;

    public List<int> LineNumbers { get; set; } = new();

    public EditDecision Decision { get; set; }

    /// <summary>Required for an override; written to the claim as a Decision note.</summary>
    [StringLength(1000)]
    public string? Reason { get; set; }

    /// <summary>True when the reviewer didn't touch the edit and Finish upheld it.</summary>
    public bool UpheldByDefault { get; set; }

    /// <summary>The policy level the override needed (Examiner, Supervisor), for the audit.</summary>
    [StringLength(20)]
    public string? PolicyLevel { get; set; }

    [StringLength(128)]
    public string? DecidedBy { get; set; }

    public DateTime DecidedAt { get; set; }
}

/// <summary>
/// A line a reviewer denies on their own judgment (claim-review-workbench
/// §2.5), e.g. the records don't support the service. Applied on the approval
/// re-run exactly like an upheld edit, with the reviewer's CARC.
/// </summary>
[BsonIgnoreExtraElements]
public class ManualLineDenial
{
    public int LineNumber { get; set; }

    /// <summary>Units denied; null denies the whole line.</summary>
    public decimal? Units { get; set; }

    /// <summary>CO (provider liability) or PR (member liability).</summary>
    [StringLength(2)]
    public string GroupCode { get; set; } = "CO";

    [StringLength(5)]
    public string Carc { get; set; } = string.Empty;

    [StringLength(10)]
    public string? Rarc { get; set; }

    [StringLength(1000)]
    public string Reason { get; set; } = string.Empty;

    [StringLength(128)]
    public string? DecidedBy { get; set; }

    public DateTime DecidedAt { get; set; }
}

/// <summary>The denial reasons a reviewer may pick for a manual line denial.</summary>
public static class ReviewerDenialReasons
{
    public sealed record Reason(string GroupCode, string Carc, string Description);

    public static readonly IReadOnlyList<Reason> All =
    [
        new("CO", "50", "Not medically necessary"),
        new("CO", "16", "Claim/service lacks information needed for adjudication"),
        new("CO", "11", "Diagnosis inconsistent with the procedure"),
        new("CO", "4", "Procedure code inconsistent with the modifier"),
        new("CO", "151", "Information submitted doesn't support this many services"),
        new("CO", "96", "Non-covered charge (provider liability)"),
        // Member-liability (PR) denials need the member's share re-derived
        // with the line; they come with the member-liability work, not here.
    ];

    public static Reason? Find(string? group, string? carc) => All.FirstOrDefault(r =>
        string.Equals(r.GroupCode, group?.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(r.Carc, carc?.Trim(), StringComparison.OrdinalIgnoreCase));
}
