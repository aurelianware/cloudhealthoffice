using System.Text.Json.Serialization;

namespace CloudHealthOffice.Tools.PaTimelinessChallenge;

/// <summary>
/// How a case reaches Cloud Health Office.
/// <see cref="History"/> cases are loaded through the authorization CSV import
/// with their source receipt and decision times, so a full reporting year (and
/// requests decided late) can be scored without waiting days on a live clock.
/// <see cref="Live"/> cases are submitted through PAS <c>$submit</c> and decided
/// by the validator acting as the reviewer, so their timing is observed, not scripted.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaCohort { History, Live }

/// <summary>Lines of business the corpus uses. Names match CHO's <c>LineOfBusiness</c> values.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaLineOfBusiness { Commercial, Medicare, Medicaid, Exchange }

/// <summary>A clinical decision. <see cref="Modified"/> is approved with changes and counts as approved.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaOutcome { Open, Approved, Modified, Denied }

/// <summary>The behavior each case is built to exercise.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaScenario
{
    /// <summary>Approved inside the timeframe.</summary>
    Approved,
    /// <summary>Denied inside the timeframe, with a specific reason on every line.</summary>
    Denied,
    /// <summary>Some lines approved, at least one denied with its own reason (request status modified).</summary>
    PartiallyApproved,
    /// <summary>Decided hours to days after the 72-hour / 7-day deadline.</summary>
    LateDecision,
    /// <summary>Decided exactly at the deadline. The deadline is inclusive, so this is on time.</summary>
    DecidedAtDeadline,
    /// <summary>Decided one minute after the deadline.</summary>
    DecidedOneMinuteLate,
    /// <summary>Medicare Advantage or Medicaid: extended 14 days before the deadline, decided inside the extension.</summary>
    ExtendedDecidedInExtension,
    /// <summary>Medicare Advantage or Medicaid: extended, then decided after the extended deadline.</summary>
    ExtendedDecidedLate,
    /// <summary>Commercial or Exchange: an extension is attempted and must be refused (live cohort only).</summary>
    ExtensionRefusedForLineOfBusiness,
    /// <summary>A drug request (NDC or HCPCS J-code). Out of CMS-0057-F scope: excluded from the metrics.</summary>
    DrugExcluded,
    /// <summary>Still undecided at the end of the period: counted as a request, not as a decision.</summary>
    Undecided,
    /// <summary>Denied on time, then approved on appeal.</summary>
    DeniedThenApprovedOnAppeal,
}

/// <summary>One requested service line and the decision scripted for it.</summary>
public sealed record PaServiceLine(
    int LineNumber,
    string ProcedureCode,
    string CodeSystem,
    string Description,
    int RequestedUnits,
    PaOutcome Decision,
    int? ApprovedUnits,
    string? DenialReasonCode,
    string? DenialReason);

/// <summary>A synthetic member. Every value is invented.</summary>
public sealed record PaMember(string MemberId, string FirstName, string LastName, DateOnly BirthDate);

/// <summary>A synthetic provider. NPIs pass the Luhn check but are generated, not looked up.</summary>
public sealed record PaProvider(string Npi, string Name);

/// <summary>The reviewer's decision, scripted. <see cref="After"/> is measured from receipt.</summary>
public sealed record PaDecisionScript(
    PaOutcome Outcome,
    TimeSpan After,
    string? DenialReasonCode,
    string? DenialReason);

/// <summary>A lawful timeframe extension, granted <see cref="GrantedAfter"/> receipt.</summary>
public sealed record PaExtensionScript(TimeSpan GrantedAfter, TimeSpan ExtendedBy, string Reason);

/// <summary>An appeal of the initial decision, decided <see cref="DecidedAfter"/> receipt.</summary>
public sealed record PaAppealScript(TimeSpan DecidedAfter, PaOutcome Outcome);

/// <summary>
/// What a correct CMS-0057-F implementation must report for this case. Every
/// value is derived from the regulation's timeframes and the case's own script.
/// </summary>
public sealed record PaExpectation
{
    /// <summary>False for drug requests, which CMS-0057-F leaves out of the PA APIs and metrics.</summary>
    public required bool InScope { get; init; }
    public required bool Expedited { get; init; }
    /// <summary>72 (expedited) or 168 (standard) hours, plus 336 when lawfully extended.</summary>
    public required double DeadlineHours { get; init; }
    /// <summary>The initial decision's outcome; <see cref="PaOutcome.Open"/> when undecided.</summary>
    public required PaOutcome InitialOutcome { get; init; }
    /// <summary>The outcome after any appeal.</summary>
    public required PaOutcome FinalOutcome { get; init; }
    /// <summary>Hours from receipt to the initial decision. Null when undecided or when the live run supplies it.</summary>
    public double? DecisionHours { get; init; }
    /// <summary>Decided by its deadline (inclusive). Null when undecided or observed live.</summary>
    public bool? DecidedWithinTimeframe { get; init; }
    public required bool Extended { get; init; }
    /// <summary>An extension attempt the service must refuse.</summary>
    public bool ExtensionMustBeRefused { get; init; }
    public required bool ApprovedAfterAppeal { get; init; }
    /// <summary>Every specific reason a correct report lists for an adverse outcome, as (line, code).</summary>
    public required IReadOnlyList<PaExpectedAdverseReason> AdverseReasons { get; init; }
}

public sealed record PaExpectedAdverseReason(int? LineNumber, string Code);

/// <summary>One prior-authorization request in the corpus, with its script and its answer.</summary>
public sealed record PaCase
{
    public required string CaseId { get; init; }
    public required string AuthorizationNumber { get; init; }
    public required PaCohort Cohort { get; init; }
    public required PaScenario Scenario { get; init; }
    public required PaLineOfBusiness LineOfBusiness { get; init; }
    /// <summary>278 UM06 level of service: <c>U</c> urgent (expedited) or <c>E</c> elective (standard).</summary>
    public required string LevelOfService { get; init; }
    /// <summary>Source receipt time. For live cases this is a placeholder; the submission time is observed.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }
    public required PaMember Member { get; init; }
    public required PaProvider RequestingProvider { get; init; }
    public required PaProvider ServicingProvider { get; init; }
    public required string ServiceTypeCode { get; init; }
    public required IReadOnlyList<string> DiagnosisCodes { get; init; }
    public required IReadOnlyList<PaServiceLine> Lines { get; init; }
    /// <summary>Null for an undecided case.</summary>
    public PaDecisionScript? Decision { get; init; }
    public PaExtensionScript? Extension { get; init; }
    public PaAppealScript? Appeal { get; init; }
    public required PaExpectation Expected { get; init; }
}
