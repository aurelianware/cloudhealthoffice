using CloudHealthOffice.FeeScheduleEngine.Domain;
using MongoDB.Bson.Serialization.Attributes;

namespace CloudHealthOffice.FeeScheduleEngine.Models;

/// <summary>
/// A fee schedule as it stood before a replace overwrote it. One per replaced
/// revision, so the rates a paid claim was priced on (its line's
/// <see cref="PricingResult.FeeScheduleId"/> and
/// <see cref="PricingResult.FeeScheduleRevision"/>) can still be read after
/// the schedule changes.
/// </summary>
[BsonIgnoreExtraElements]
public class FeeScheduleRevisionSnapshot
{
    /// <summary><c>{feeScheduleId}@{revision}</c>.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;
    public string FeeScheduleId { get; set; } = string.Empty;

    /// <summary>The revision the schedule had while these rates were in force.</summary>
    public long Revision { get; set; }

    /// <summary>The schedule, lines included, exactly as stored at <see cref="Revision"/>.</summary>
    public FeeSchedule Schedule { get; set; } = new();

    /// <summary>When the replace that superseded it was written.</summary>
    public DateTime SupersededAt { get; set; }

    public static string MakeId(string feeScheduleId, long revision) => $"{feeScheduleId}@{revision}";
}

/// <summary>What happened to the schedule in a <see cref="FeeScheduleChange"/>.</summary>
public enum FeeScheduleChangeKind
{
    /// <summary>A new schedule (a new id, which includes its effective date).</summary>
    Created,

    /// <summary>An existing schedule was overwritten in place.</summary>
    Replaced,
}

/// <summary>
/// One write to the fee schedule store, recorded so rate changes can be found
/// and the paid claims they affect reprocessed. Written for every create and
/// replace, whichever importer, library deploy or API made it.
///
/// <para>
/// A created schedule only changes claims whose dates of service fall on or
/// after its <see cref="EffectiveDate"/>, and only when its effective date is
/// in the past (a retroactive schedule) is any paid claim affected. A replaced
/// schedule changes the claims that were priced on it: those whose lines carry
/// its id and a revision below <see cref="NewRevision"/>.
/// </para>
/// </summary>
[BsonIgnoreExtraElements]
public class FeeScheduleChange
{
    /// <summary><c>{feeScheduleId}@{newRevision}</c>: one change per stored revision.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;
    public string FeeScheduleId { get; set; } = string.Empty;
    public string FeeScheduleName { get; set; } = string.Empty;
    public FeeScheduleType FeeScheduleType { get; set; }
    public FeeScheduleChangeKind Kind { get; set; }

    /// <summary>The revision replaced; null for <see cref="FeeScheduleChangeKind.Created"/>.</summary>
    public long? PreviousRevision { get; set; }

    /// <summary>The revision now stored.</summary>
    public long NewRevision { get; set; }

    /// <summary>Effective range of the schedule after the change.</summary>
    public DateTime EffectiveDate { get; set; }
    public DateTime? TermDate { get; set; }

    /// <summary>Effective range before the change, when it moved.</summary>
    public DateTime? PreviousEffectiveDate { get; set; }
    public DateTime? PreviousTermDate { get; set; }

    /// <summary>
    /// The plan the schedule is the default for, and the plans a supplemental
    /// schedule pays for: how the claims that may use it are found when a
    /// line does not record its schedule (claims priced before it did).
    /// </summary>
    public string? DefaultForPlanId { get; set; }
    public List<string> SupplementalForPlanIds { get; set; } = new();

    /// <summary>
    /// True when a schedule-level value changed (conversion factor, GPCI,
    /// percent of Medicare, per diem, DRG base rate, effective range), which
    /// can change every line's price, or when the change was too large to list
    /// code by code. Every line priced on the schedule is then a candidate.
    /// </summary>
    public bool AllCodes { get; set; }

    /// <summary>Which schedule-level values changed, in words, for the impact report.</summary>
    public List<string> ScheduleLevelChanges { get; set; } = new();

    /// <summary>
    /// Procedure codes (or <c>rev:{code}</c> for revenue-code-only lines) whose
    /// rate line was added, removed or changed. Empty when
    /// <see cref="AllCodes"/> is set and for a created schedule.
    /// </summary>
    public List<string> ChangedCodes { get; set; } = new();

    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public int LinesRateIncreased { get; set; }
    public int LinesRateDecreased { get; set; }

    /// <summary>A changed line whose rate did not simply go up or down (rate type, RVUs, units cap, indicators).</summary>
    public int LinesOtherwiseChanged { get; set; }

    public DateTime ChangedAt { get; set; }

    public static string MakeId(string feeScheduleId, long newRevision) => $"{feeScheduleId}@{newRevision}";
}
