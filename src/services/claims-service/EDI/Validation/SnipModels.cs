using System.Text.Json.Serialization;
using ClaimsService.EDI.Inbound;

namespace ClaimsService.EDI.Validation;

/// <summary>
/// WEDI Strategic National Implementation Process (SNIP) test types 1–5.
/// Types 6 (product type / line of business) and 7 (trading-partner
/// specific) are not implemented.
/// </summary>
public enum SnipLevel
{
    /// <summary>EDI syntax integrity: envelopes, control numbers, segment ids, mandatory elements, data types.</summary>
    Syntax = 1,

    /// <summary>HIPAA implementation guide requirements: required loops, segments, elements and IG code values.</summary>
    ImplementationGuide = 2,

    /// <summary>Balancing: claim charge equals the sum of its service line charges.</summary>
    Balancing = 3,

    /// <summary>Situational, inter-segment rules (e.g. DTP*472, NPI check digit, frequency 7/8 needs REF*F8).</summary>
    Situational = 4,

    /// <summary>External code sets: ICD-10-CM/PCS, CPT/HCPCS, place of service, revenue codes.</summary>
    ExternalCodeSets = 5,
}

/// <summary>What a SNIP level does when one of its checks fails.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SnipAction
{
    /// <summary>The issue rejects its transaction set (IK5 R).</summary>
    Reject,

    /// <summary>The issue is reported (IK3/IK4, IK5 E) but the transaction set is accepted.</summary>
    Warn,

    /// <summary>The level's checks do not run.</summary>
    Off,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SnipSeverity
{
    Error,
    Warning,
}

/// <summary>
/// One SNIP finding. Positions follow the 999: <see cref="SegmentPosition"/>
/// counts segments from ST (ST = 1); <see cref="ElementPosition"/> and
/// <see cref="ComponentPosition"/> are 1-based (CLM02 → 2).
/// <see cref="BadValue"/> is only filled for code values, never for names,
/// identifiers or dates, so findings can be logged and returned without PHI.
/// </summary>
public sealed record SnipIssue
{
    public required SnipLevel Level { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SnipSeverity Severity { get; init; } = SnipSeverity.Error;

    /// <summary>Stable rule id, e.g. "L3-CLM-BALANCE".</summary>
    public required string RuleId { get; init; }

    public required string Message { get; init; }

    /// <summary>ST02 of the transaction set, when the issue is inside one.</summary>
    public string? TransactionSetControlNumber { get; init; }

    /// <summary>CLM01 of the claim the issue belongs to, when known.</summary>
    public string? ClaimId { get; init; }

    /// <summary>Loop id, e.g. "2300", "2400", "2010AA".</summary>
    public string? Loop { get; init; }

    /// <summary>Segment id the issue is about (for a missing segment, the segment that is missing).</summary>
    public string? SegmentId { get; init; }

    public int? SegmentPosition { get; init; }
    public int? ElementPosition { get; init; }
    public int? ComponentPosition { get; init; }

    /// <summary>X12 data element reference number (IK402), when known.</summary>
    public string? DataElementReference { get; init; }

    /// <summary>999 IK304 segment syntax error code.</summary>
    public string SegmentErrorCode { get; init; } = "8";

    /// <summary>999 IK403 element syntax error code, for element-level issues.</summary>
    public string? ElementErrorCode { get; init; }

    public string? BadValue { get; init; }
}

/// <summary>Validation outcome of one ST/SE transaction set.</summary>
public sealed class SnipTransactionSetOutcome
{
    public required string ControlNumber { get; init; }

    /// <summary>ST03 implementation convention reference (e.g. 005010X222A1).</summary>
    public string? ImplementationReference { get; init; }

    /// <summary>Index of ISA, ST and SE in <see cref="X12Document.Segments"/>.</summary>
    public required int InterchangeSegmentIndex { get; init; }
    public required int StartSegmentIndex { get; init; }
    public required int EndSegmentIndex { get; init; }

    public List<SnipIssue> Issues { get; } = [];

    /// <summary>
    /// Set when the enclosing functional group or interchange is rejected
    /// (group error codes, ISA/IEA errors): the set is rejected whatever its
    /// own findings, so IK5, AK9 and <see cref="Accepted"/> always agree.
    /// </summary>
    public bool RejectedByEnvelope { get; set; }

    /// <summary>Findings not listed because a finding cap was reached.</summary>
    public int SuppressedFindings { get; set; }

    /// <summary>True when a finding dropped by a cap was an error: the set is still rejected.</summary>
    public bool SuppressedError { get; set; }

    /// <summary>999 IK5 acknowledgment code: A accepted, E accepted with errors, R rejected.</summary>
    public string AcknowledgmentCode =>
        RejectedByEnvelope || SuppressedError || Issues.Any(i => i.Severity == SnipSeverity.Error) ? "R"
        : Issues.Count > 0 || SuppressedFindings > 0 ? "E"
        : "A";

    public bool Accepted => AcknowledgmentCode != "R";

    /// <summary>999 IK502–IK506 transaction-set syntax error codes.</summary>
    public List<string> TransactionSetErrorCodes { get; } = [];
}

/// <summary>Validation outcome of one GS/GE functional group.</summary>
public sealed class SnipFunctionalGroupOutcome
{
    public string? ControlNumber { get; init; }
    public string? FunctionalIdentifier { get; init; }
    public string? VersionCode { get; init; }
    public string? ApplicationSenderCode { get; init; }
    public string? ApplicationReceiverCode { get; init; }

    /// <summary>The interchange the group arrived in.</summary>
    public required SnipInterchangeOutcome Interchange { get; init; }

    /// <summary>GE01 as sent, when parseable.</summary>
    public int? DeclaredTransactionSetCount { get; set; }

    public List<SnipTransactionSetOutcome> TransactionSets { get; } = [];

    /// <summary>
    /// 999 AK905–AK909 functional group error codes at a rejecting Level 1
    /// setting. Any code rejects the whole group.
    /// </summary>
    public List<string> GroupErrorCodes { get; } = [];

    /// <summary>Group error codes found while Level 1 is set to Warn: reported in AK9, the group is not rejected.</summary>
    public List<string> GroupWarningCodes { get; } = [];

    /// <summary>True when the group's own codes or its interchange reject it.</summary>
    public bool Rejected => GroupErrorCodes.Count > 0 || Interchange.Rejected;

    /// <summary>999 AK901: A, E, P (partially accepted) or R. Always consistent with the sets' <see cref="SnipTransactionSetOutcome.Accepted"/>.</summary>
    public string AcknowledgmentCode
    {
        get
        {
            if (Rejected || TransactionSets.Count == 0) return "R";
            var accepted = TransactionSets.Count(t => t.Accepted);
            if (accepted == 0) return "R";
            if (accepted < TransactionSets.Count) return "P";
            return TransactionSets.Any(t => t.AcknowledgmentCode == "E") || GroupWarningCodes.Count > 0 ? "E" : "A";
        }
    }
}

/// <summary>One inbound ISA/IEA interchange; its 999 goes back to its own sender.</summary>
public sealed class SnipInterchangeOutcome
{
    public string? SenderQualifier { get; init; }
    public string? SenderId { get; init; }
    public string? ReceiverQualifier { get; init; }
    public string? ReceiverId { get; init; }
    public string? ControlNumber { get; init; }

    /// <summary>ISA15 as sent (P or T); echoed in the 999.</summary>
    public string? UsageIndicator { get; init; }

    /// <summary>
    /// True when an ISA/IEA-level error at a rejecting Level 1 setting was
    /// found. Every group and transaction set in it is then rejected
    /// (AK9 R, IK5 R). A TA1 is not produced.
    /// </summary>
    public bool Rejected { get; set; }

    public List<SnipFunctionalGroupOutcome> FunctionalGroups { get; } = [];
}

/// <summary>Result of SNIP-validating one 837 file.</summary>
public sealed class SnipValidationResult
{
    /// <summary>The tokenized file, or null when it could not be tokenized at all.</summary>
    public X12Document? Document { get; init; }

    public List<SnipInterchangeOutcome> Interchanges { get; } = [];

    public IEnumerable<SnipFunctionalGroupOutcome> FunctionalGroups =>
        Interchanges.SelectMany(i => i.FunctionalGroups);

    /// <summary>Issues outside any transaction set (ISA/IEA, GS/GE, unreadable file).</summary>
    public List<SnipIssue> EnvelopeIssues { get; } = [];

    /// <summary>True when the per-file finding cap was reached and later findings were dropped.</summary>
    public bool FindingsTruncated { get; set; }

    public IEnumerable<SnipTransactionSetOutcome> TransactionSets =>
        FunctionalGroups.SelectMany(g => g.TransactionSets);

    public IEnumerable<SnipIssue> AllIssues =>
        EnvelopeIssues.Concat(TransactionSets.SelectMany(t => t.Issues));

    /// <summary>
    /// Transaction sets whose claims may be submitted. Exactly the sets the
    /// 999 acknowledges with IK5 A or E (envelope rejections are folded into
    /// each set's <see cref="SnipTransactionSetOutcome.RejectedByEnvelope"/>).
    /// </summary>
    public IEnumerable<SnipTransactionSetOutcome> AcceptedTransactionSets =>
        Document is null ? [] : TransactionSets.Where(t => t.Accepted);

    /// <summary>
    /// Overall code: R when the file is unreadable or every group is
    /// rejected, P when some transaction sets were rejected, E when
    /// everything was accepted with warnings, otherwise A.
    /// </summary>
    public string AcknowledgmentCode
    {
        get
        {
            var groups = FunctionalGroups.ToList();
            if (Document is null || groups.Count == 0) return "R";
            var codes = groups.Select(g => g.AcknowledgmentCode).ToList();
            if (codes.All(c => c == "R")) return "R";
            if (codes.Any(c => c is "R" or "P")) return "P";
            return codes.Any(c => c == "E") || EnvelopeIssues.Count > 0 ? "E" : "A";
        }
    }
}


/// <summary>
/// Configuration (section <c>ClaimsImport:Snip</c>). Defaults: levels 1–4
/// reject, level 5 warns — the code-set checks are format checks unless a
/// <see cref="ISnipCodeSetReference"/> is registered, so on their own they
/// are reported rather than allowed to reject.
/// </summary>
public sealed class Snip837ValidationOptions
{
    public const string SectionName = "ClaimsImport:Snip";

    /// <summary>When false, raw 837 import skips SNIP validation (no 999 is produced).</summary>
    public bool Enabled { get; set; } = true;

    public SnipAction Level1 { get; set; } = SnipAction.Reject;
    public SnipAction Level2 { get; set; } = SnipAction.Reject;
    public SnipAction Level3 { get; set; } = SnipAction.Reject;
    public SnipAction Level4 { get; set; } = SnipAction.Reject;
    public SnipAction Level5 { get; set; } = SnipAction.Warn;

    /// <summary>
    /// Findings kept per transaction set; once reached, collection stops for
    /// that set and one "too many findings" finding is added.
    /// </summary>
    public int MaxFindingsPerTransactionSet { get; set; } = 1000;

    /// <summary>Findings kept for the whole file (envelope plus all transaction sets).</summary>
    public int MaxFindingsPerFile { get; set; } = 1000;

    public SnipAction ActionFor(SnipLevel level) => level switch
    {
        SnipLevel.Syntax => Level1,
        SnipLevel.ImplementationGuide => Level2,
        SnipLevel.Balancing => Level3,
        SnipLevel.Situational => Level4,
        SnipLevel.ExternalCodeSets => Level5,
        _ => SnipAction.Reject,
    };
}
