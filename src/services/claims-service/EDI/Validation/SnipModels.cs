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

    /// <summary>999 IK5 acknowledgment code: A accepted, E accepted with errors, R rejected.</summary>
    public string AcknowledgmentCode =>
        Issues.Any(i => i.Severity == SnipSeverity.Error) ? "R" : Issues.Count > 0 ? "E" : "A";

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

    /// <summary>GE01 as sent, when parseable.</summary>
    public int? DeclaredTransactionSetCount { get; set; }

    public List<SnipTransactionSetOutcome> TransactionSets { get; } = [];

    /// <summary>999 AK905–AK909 functional group syntax error codes.</summary>
    public List<string> GroupErrorCodes { get; } = [];

    /// <summary>999 AK901: A, E, P (partially accepted) or R.</summary>
    public string AcknowledgmentCode
    {
        get
        {
            if (GroupErrorCodes.Count > 0 || TransactionSets.Count == 0) return "R";
            var accepted = TransactionSets.Count(t => t.Accepted);
            if (accepted == 0) return "R";
            if (accepted < TransactionSets.Count) return "P";
            return TransactionSets.Any(t => t.AcknowledgmentCode == "E") ? "E" : "A";
        }
    }
}

/// <summary>Result of SNIP-validating one 837 file.</summary>
public sealed class SnipValidationResult
{
    /// <summary>The tokenized file, or null when it could not be tokenized at all.</summary>
    public X12Document? Document { get; init; }

    /// <summary>ISA06 / ISA08 / ISA13 of the (first) inbound interchange, for the 999 envelope.</summary>
    public string? InterchangeSenderQualifier { get; set; }
    public string? InterchangeSenderId { get; set; }
    public string? InterchangeReceiverQualifier { get; set; }
    public string? InterchangeReceiverId { get; set; }
    public string? InterchangeControlNumber { get; set; }

    public List<SnipFunctionalGroupOutcome> FunctionalGroups { get; } = [];

    /// <summary>Issues outside any transaction set (ISA/IEA, GS/GE, unreadable file).</summary>
    public List<SnipIssue> EnvelopeIssues { get; } = [];

    public IEnumerable<SnipTransactionSetOutcome> TransactionSets =>
        FunctionalGroups.SelectMany(g => g.TransactionSets);

    public IEnumerable<SnipIssue> AllIssues =>
        EnvelopeIssues.Concat(TransactionSets.SelectMany(t => t.Issues));

    /// <summary>
    /// Transaction sets whose claims may be submitted: none when an
    /// envelope-level issue rejects the file; otherwise the accepted sets of
    /// groups without group-level errors.
    /// </summary>
    public IEnumerable<SnipTransactionSetOutcome> AcceptedTransactionSets =>
        Document is null || EnvelopeIssues.Any(i => i.Severity == SnipSeverity.Error)
            ? []
            : FunctionalGroups.Where(g => g.GroupErrorCodes.Count == 0).SelectMany(g => g.TransactionSets).Where(t => t.Accepted);

    /// <summary>
    /// Overall code: R when the file is unreadable or every group is
    /// rejected, P when some transaction sets were rejected, E when
    /// everything was accepted with warnings, otherwise A.
    /// </summary>
    public string AcknowledgmentCode
    {
        get
        {
            if (Document is null || FunctionalGroups.Count == 0) return "R";
            if (EnvelopeIssues.Any(i => i.Severity == SnipSeverity.Error)) return "R";
            var codes = FunctionalGroups.Select(g => g.AcknowledgmentCode).ToList();
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
