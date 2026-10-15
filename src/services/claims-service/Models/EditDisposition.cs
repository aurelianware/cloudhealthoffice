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
