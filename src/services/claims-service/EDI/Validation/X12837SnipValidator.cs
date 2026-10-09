using System.Globalization;
using ClaimsService.EDI.Inbound;
using Microsoft.Extensions.Options;

namespace ClaimsService.EDI.Validation;

/// <summary>Validates an inbound 837 file against WEDI SNIP levels 1–5.</summary>
public interface ISnip837Validator
{
    /// <summary>Never throws for bad input; every problem becomes a <see cref="SnipIssue"/>.</summary>
    SnipValidationResult Validate(string ediContent);
}

/// <summary>
/// WEDI SNIP 1–5 validation for 837P (005010X222A1) and 837I
/// (005010X223A2/A3), run before the 837 is parsed and mapped.
///
/// <para>What each level checks:</para>
/// <list type="number">
///   <item><b>Syntax.</b> ISA fixed widths, ISA/IEA, GS/GE and ST/SE control
///   numbers and counts, duplicate ST02, unrecognized segment ids, X12
///   mandatory elements, numeric and date/time element formats.</item>
///   <item><b>Implementation guide.</b> BHT values, 1000A/1000B, the HL
///   hierarchy (sequence, parents, child codes), 2010AA/2010AB/2010AC/2000B/
///   2010BA/2010BB/2000C required segments (2010AB is an address only: NM103
///   onward not used), CLM05 composite and CLM06–09, principal diagnosis,
///   diagnosis count, LX numbering and SV1/SV2 presence and qualifiers.</item>
///   <item><b>Balancing.</b> CLM02 = ΣSV102 (837P) / ΣSV203 (837I).</item>
///   <item><b>Situational.</b> DTP*472 on every 837P line and on 837I
///   outpatient lines when the statement covers more than one day; 837I
///   inpatient admission date and CL1; line dates inside the statement
///   period and not after BHT04; NPI check digit; PO Box billing address;
///   2010AC only on a subrogation demand (BHT06 = 31);
///   subscriber-is-patient rules; REF*F8 for frequency 7/8; SV107 pointers.</item>
///   <item><b>External code sets.</b> ICD-10-CM / ICD-10-PCS formats (and no
///   ICD-9 qualifiers after 2015-10-01), CPT/HCPCS and modifier formats,
///   the CMS place-of-service list, revenue code and type-of-bill formats, plus
///   membership checks through an optional <see cref="ISnipCodeSetReference"/>.</item>
/// </list>
/// </summary>
public sealed class X12837SnipValidator : ISnip837Validator
{
    private const string Professional = "005010X222A1";
    private static readonly HashSet<string> Institutional = ["005010X223A2", "005010X223A3"];

    private static readonly HashSet<string> Known837Segments =
    [
        "ST", "BHT", "REF", "NM1", "PER", "N3", "N4", "HL", "PRV", "CUR", "DMG", "SBR", "PAT",
        "CLM", "DTP", "CL1", "PWK", "CN1", "AMT", "K3", "NTE", "CR1", "CR2", "CRC", "HI", "HCP",
        "CAS", "MOA", "MIA", "OI", "LX", "SV1", "SV2", "SV5", "CR3", "QTY", "MEA", "PS1", "HSD",
        "LIN", "CTP", "SVD", "LQ", "FRM", "TOO", "SE",
    ];

    // X12 base-standard mandatory elements (1-based), independent of the IG.
    private static readonly Dictionary<string, int[]> MandatoryElements = new()
    {
        ["BHT"] = [1, 2],
        ["NM1"] = [1, 2],
        ["N3"] = [1],
        ["HL"] = [1, 3],
        ["SBR"] = [1],
        ["CLM"] = [1, 2, 5],
        ["HI"] = [1],
        ["LX"] = [1],
        ["SV1"] = [1, 2],
        ["SV2"] = [1],
        ["DTP"] = [1, 2, 3],
        ["REF"] = [1],
        ["PAT"] = [],
        ["AMT"] = [1, 2],
    };

    // Numeric (R / N) elements checked for syntax: (segment, element).
    private static readonly (string Seg, int Element, string Ref)[] NumericElements =
    [
        ("CLM", 2, "782"), ("SV1", 2, "782"), ("SV1", 4, "380"),
        ("SV2", 3, "782"), ("SV2", 5, "380"), ("AMT", 2, "782"),
        ("LX", 1, "554"), ("HL", 1, "628"),
    ];

    private static readonly HashSet<string> Icd10CmQualifiers = ["ABK", "ABF", "ABJ", "APR", "ABN"];
    private static readonly HashSet<string> Icd9CmQualifiers = ["BK", "BF", "BJ", "PR", "BN"];
    private static readonly DateOnly Icd10Cutover = new(2015, 10, 1);

    private readonly Snip837ValidationOptions _options;
    private readonly ISnipCodeSetReference? _codeSets;

    public X12837SnipValidator(IOptions<Snip837ValidationOptions> options, ISnipCodeSetReference? codeSets = null)
        : this(options.Value, codeSets)
    {
    }

    public X12837SnipValidator(Snip837ValidationOptions? options = null, ISnipCodeSetReference? codeSets = null)
    {
        _options = options ?? new Snip837ValidationOptions();
        _codeSets = codeSets;
    }
    public SnipValidationResult Validate(string ediContent)
    {
        X12Document doc;
        try
        {
            doc = X12Tokenizer.Tokenize(ediContent);
        }
        catch (X12FormatException ex)
        {
            return Unreadable($"The file is not a readable X12 interchange: {ex.Message}", "L1-ISA-UNREADABLE");
        }

        try
        {
            var result = new SnipValidationResult { Document = doc };
            new EnvelopeWalker(this, doc, result, new FindingBudget(_options.MaxFindingsPerFile)).Run();
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Validation must never throw: an unexpected failure is itself a
            // syntax finding that rejects the file (no transaction set is
            // accepted, no 999 is built).
            var failed = new SnipValidationResult { Document = doc };
            failed.EnvelopeIssues.Add(new SnipIssue
            {
                Level = SnipLevel.Syntax,
                RuleId = "L1-VALIDATION-FAILED",
                Message = $"The file could not be validated ({ex.GetType().Name}); it is rejected.",
            });
            return failed;
        }
    }

    private static SnipValidationResult Unreadable(string message, string rule)
    {
        var unreadable = new SnipValidationResult();
        unreadable.EnvelopeIssues.Add(new SnipIssue
        {
            Level = SnipLevel.Syntax,
            RuleId = rule,
            Message = message,
            SegmentId = "ISA",
        });
        return unreadable;
    }

    /// <summary>Segment ids echoed into findings or the 999: at most 3 characters, or a placeholder when not a valid id.</summary>
    internal static string SafeSegmentId(string? id) =>
        id is { Length: >= 2 and <= 3 } && char.IsAsciiLetterUpper(id[0]) && id.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c))
            ? id
            : "???";

    /// <summary>Envelope values echoed into messages: short alphanumeric tokens only.</summary>
    private static string SafeValue(string? value) =>
        value is null ? string.Empty
        : value.Length <= 20 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '-' or '_') ? value
        : "(not shown)";

    /// <summary>Per-file finding budget shared by the envelope and every transaction set.</summary>
    private sealed class FindingBudget(int perFile)
    {
        public int Used { get; set; }
        public bool Exhausted => Used >= Math.Max(perFile, 0);
        public bool FileCapReported { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Envelope: ISA/IEA, GS/GE, ST/SE
    // ═══════════════════════════════════════════════════════════════════

    private sealed class EnvelopeWalker(X12837SnipValidator owner, X12Document doc, SnipValidationResult result, FindingBudget budget)
    {
        private readonly IReadOnlyList<X12Segment> _segs = doc.Segments;
        private SnipInterchangeOutcome? _interchange;

        public void Run()
        {
            var inInterchange = false;
            string? isaControl = null;
            var groupsInInterchange = 0;
            SnipFunctionalGroupOutcome? group = null;
            var tsInGroup = 0;
            var stControlNumbers = new HashSet<string>(StringComparer.Ordinal);
            var groupControlNumbers = new HashSet<string>(StringComparer.Ordinal);

            void CloseGroupWithoutTrailer()
            {
                if (group is null) return;
                GroupError(group, "3", "L1-GE-MISSING", $"Functional group {SafeValue(group.ControlNumber)} has no GE trailer.", "GE");
                group = null;
            }

            for (var i = 0; i < _segs.Count; i++)
            {
                var seg = _segs[i];
                switch (seg.Id)
                {
                    case "ISA":
                        if (inInterchange)
                        {
                            CloseGroupWithoutTrailer();
                            InterchangeError("L1-IEA-MISSING", $"Interchange {SafeValue(isaControl)} has no IEA trailer.", "IEA");
                        }
                        inInterchange = true;
                        isaControl = seg.Element(12);
                        groupsInInterchange = 0;
                        groupControlNumbers.Clear();
                        _interchange = new SnipInterchangeOutcome
                        {
                            SenderQualifier = seg.Element(4)?.Trim(),
                            SenderId = seg.Element(5)?.Trim(),
                            ReceiverQualifier = seg.Element(6)?.Trim(),
                            ReceiverId = seg.Element(7)?.Trim(),
                            ControlNumber = isaControl,
                            UsageIndicator = seg.Element(14) is "P" or "T" ? seg.Element(14) : null,
                        };
                        result.Interchanges.Add(_interchange);
                        ValidateIsa(seg);
                        break;

                    case "IEA":
                        CloseGroupWithoutTrailer();
                        if (!inInterchange)
                        {
                            InterchangeError("L1-IEA-UNMATCHED", "IEA segment has no matching ISA.", "IEA");
                            break;
                        }
                        if (seg.Element(1) != isaControl)
                            InterchangeError("L1-IEA-CONTROL", $"IEA02 '{SafeValue(seg.Element(1))}' does not match ISA13 '{SafeValue(isaControl)}'.", "IEA");
                        if (!int.TryParse(seg.Element(0), NumberStyles.None, CultureInfo.InvariantCulture, out var declaredGroups) || declaredGroups != groupsInInterchange)
                            InterchangeError("L1-IEA-COUNT", $"IEA01 '{SafeValue(seg.Element(0))}' does not match the {groupsInInterchange} functional group(s) in the interchange.", "IEA");
                        inInterchange = false;
                        break;

                    case "GS":
                        CloseGroupWithoutTrailer();
                        if (!inInterchange)
                            InterchangeError("L1-GS-OUTSIDE-ISA", "GS segment appears outside an ISA/IEA interchange.", "GS");
                        groupsInInterchange++;
                        tsInGroup = 0;
                        stControlNumbers.Clear();
                        group = new SnipFunctionalGroupOutcome
                        {
                            Interchange = CurrentInterchange(),
                            FunctionalIdentifier = seg.Element(0),
                            ApplicationSenderCode = seg.Element(1),
                            ApplicationReceiverCode = seg.Element(2),
                            ControlNumber = seg.Element(5),
                            VersionCode = seg.Element(7),
                        };
                        group.Interchange.FunctionalGroups.Add(group);
                        if (seg.Element(0) != "HC")
                            GroupError(group, "1", "L1-GS01", $"GS01 must be HC for an 837, found '{SafeValue(seg.Element(0))}'.", "GS");
                        if (seg.Element(7) is not { } gs08 || (gs08 != Professional && !Institutional.Contains(gs08)))
                            GroupError(group, "2", "L1-GS08", $"GS08 '{SafeValue(seg.Element(7))}' is not a supported 837 version (005010X222A1, 005010X223A2).", "GS");
                        if (seg.Element(5) is not { } gs06 || !gs06.All(char.IsAsciiDigit) || gs06.Length > 9)
                            GroupError(group, "6", "L1-GS06", "GS06 group control number must be 1-9 digits.", "GS");
                        else if (!groupControlNumbers.Add(gs06))
                            GroupError(group, "19", "L1-GS06-DUPLICATE", $"GS06 '{gs06}' is used by another functional group in this interchange.", "GS");
                        // X12 716 has no date-specific AK905 code; 1 (group not supported) is the closest.
                        if (!IsDate(seg.Element(3), "yyyyMMdd"))
                            GroupError(group, "1", "L1-GS04", "GS04 is not a valid CCYYMMDD date.", "GS");
                        break;

                    case "GE":
                        if (group is null)
                        {
                            InterchangeError("L1-GE-UNMATCHED", "GE segment has no matching GS.", "GE");
                            break;
                        }
                        if (seg.Element(1) != group.ControlNumber)
                            GroupError(group, "4", "L1-GE-CONTROL", $"GE02 '{SafeValue(seg.Element(1))}' does not match GS06 '{SafeValue(group.ControlNumber)}'.", "GE");
                        if (int.TryParse(seg.Element(0), NumberStyles.None, CultureInfo.InvariantCulture, out var declaredSets))
                            group.DeclaredTransactionSetCount = declaredSets;
                        if (group.DeclaredTransactionSetCount != tsInGroup)
                            GroupError(group, "5", "L1-GE-COUNT", $"GE01 '{SafeValue(seg.Element(0))}' does not match the {tsInGroup} transaction set(s) in the group.", "GE");
                        group = null;
                        break;

                    case "ST":
                        if (group is null)
                        {
                            group = new SnipFunctionalGroupOutcome { Interchange = CurrentInterchange() };
                            group.Interchange.FunctionalGroups.Add(group);
                            GroupError(group, "3", "L1-ST-OUTSIDE-GS", "ST segment appears outside a GS/GE functional group.", "ST");
                        }
                        tsInGroup++;
                        var end = FindTransactionSetEnd(i);
                        var outcome = new SnipTransactionSetOutcome
                        {
                            ControlNumber = seg.Element(1) ?? string.Empty,
                            ImplementationReference = seg.Element(2),
                            InterchangeSegmentIndex = FindInterchangeIndex(i),
                            StartSegmentIndex = i,
                            EndSegmentIndex = end,
                        };
                        group.TransactionSets.Add(outcome);
                        if (!stControlNumbers.Add(outcome.ControlNumber))
                            outcome.TransactionSetErrorCodes.Add("23");
                        new TransactionSetValidator(owner, doc, outcome, group, _segs, i, end, budget, result).Run();
                        // A truncated file can end without SE/GE/IEA: end is then past the last segment.
                        i = end < _segs.Count && _segs[end].Id == "SE" ? end : end - 1;
                        break;

                    default:
                        InterchangeError("L1-SEGMENT-OUTSIDE-ST", $"Segment {SafeSegmentId(seg.Id)} appears outside a transaction set.", SafeSegmentId(seg.Id));
                        break;
                }
            }

            CloseGroupWithoutTrailer();
            if (inInterchange)
                InterchangeError("L1-IEA-MISSING", $"Interchange {SafeValue(isaControl)} has no IEA trailer.", "IEA");

            // Fold group- and interchange-level rejections into each set, so
            // IK5, AK9 and AcceptedTransactionSets can never disagree.
            foreach (var g in result.FunctionalGroups)
            {
                foreach (var ts in g.TransactionSets)
                    ts.RejectedByEnvelope = g.Rejected;
            }
        }

        private SnipInterchangeOutcome CurrentInterchange()
        {
            if (_interchange is not null) return _interchange;
            // Segments before any ISA cannot occur (the tokenizer requires
            // ISA first), so this is only a safety net.
            _interchange = new SnipInterchangeOutcome();
            result.Interchanges.Add(_interchange);
            return _interchange;
        }

        private int FindInterchangeIndex(int from)
        {
            for (var j = from; j >= 0; j--)
                if (_segs[j].Id == "ISA") return j;
            return 0;
        }

        /// <summary>Index of the SE closing the ST at <paramref name="st"/>, or of the segment that ends it early.</summary>
        private int FindTransactionSetEnd(int st)
        {
            for (var j = st + 1; j < _segs.Count; j++)
            {
                switch (_segs[j].Id)
                {
                    case "SE": return j;
                    case "ST" or "GE" or "IEA" or "GS" or "ISA": return j;
                }
            }
            return _segs.Count;
        }

        private void ValidateIsa(X12Segment isa)
        {
            int[] widths = [2, 10, 2, 10, 2, 15, 2, 15, 6, 4, 1, 5, 9, 1, 1, 1];
            if (isa.Elements.Count != 16)
            {
                InterchangeError("L1-ISA-ELEMENTS", $"ISA must have 16 elements, found {isa.Elements.Count}.", "ISA");
                return;
            }
            for (var e = 0; e < widths.Length; e++)
            {
                if (isa.Elements[e].Length != widths[e])
                    InterchangeError("L1-ISA-WIDTH", $"ISA{e + 1:00} must be exactly {widths[e]} characters.", "ISA");
            }
            if (!isa.Elements[12].All(char.IsAsciiDigit))
                InterchangeError("L1-ISA13", "ISA13 interchange control number must be 9 digits.", "ISA");
            if (isa.Elements[11] != "00501")
                InterchangeError("L1-ISA12", $"ISA12 must be 00501 for a 5010 837, found '{SafeValue(isa.Elements[11])}'.", "ISA");
            if (!IsDate(isa.Elements[8], "yyMMdd"))
                InterchangeError("L1-ISA09", "ISA09 is not a valid YYMMDD date.", "ISA");
            if (isa.Elements[14] is not ("P" or "T"))
                InterchangeError("L1-ISA15", "ISA15 usage indicator must be P or T.", "ISA");
        }

        /// <summary>An ISA/IEA-level error: at a rejecting Level 1, every set in the interchange is rejected.</summary>
        private void InterchangeError(string rule, string message, string segmentId)
        {
            if (Envelope(rule, message, segmentId) == SnipAction.Reject)
                CurrentInterchange().Rejected = true;
        }

        /// <summary>
        /// A GS/GE-level error with its AK905 code. Reject → the code rejects
        /// the group; Warn → reported in AK9 without rejecting; Off → nothing.
        /// </summary>
        private void GroupError(SnipFunctionalGroupOutcome group, string code, string rule, string message, string segmentId)
        {
            switch (Envelope(rule, message, segmentId))
            {
                case SnipAction.Reject: group.GroupErrorCodes.Add(code); break;
                case SnipAction.Warn: group.GroupWarningCodes.Add(code); break;
            }
        }

        private SnipAction Envelope(string rule, string message, string segmentId)
        {
            var action = owner._options.ActionFor(SnipLevel.Syntax);
            if (action == SnipAction.Off) return action;

            if (budget.Exhausted)
            {
                result.FindingsTruncated = true;
                if (!budget.FileCapReported)
                {
                    budget.FileCapReported = true;
                    result.EnvelopeIssues.Add(TooManyFindings("file"));
                }
            }
            else
            {
                budget.Used++;
                result.EnvelopeIssues.Add(new SnipIssue
                {
                    Level = SnipLevel.Syntax,
                    Severity = action == SnipAction.Reject ? SnipSeverity.Error : SnipSeverity.Warning,
                    RuleId = rule,
                    Message = message,
                    SegmentId = segmentId,
                });
            }
            return action;
        }
    }

    private static SnipIssue TooManyFindings(string scope) => new()
    {
        Level = SnipLevel.Syntax,
        Severity = SnipSeverity.Warning,
        RuleId = "L1-TOO-MANY-FINDINGS",
        Message = $"Too many errors: the {scope} finding limit was reached and further findings are not listed. " +
                  "Acceptance still accounts for every check.",
    };

    // ═══════════════════════════════════════════════════════════════════
    // One transaction set
    // ═══════════════════════════════════════════════════════════════════

    private sealed record Seg(X12Segment S, int Pos, string? Loop)
    {
        public string Id => S.Id;

        /// <summary>1-based element (CLM02 → E(2)).</summary>
        public string? E(int n) => S.Element(n - 1);
    }

    private sealed class HlNode
    {
        public required Seg Hl { get; init; }
        public string Id => Hl.E(1) ?? string.Empty;
        public string? Parent => Hl.E(2);
        public string? LevelCode => Hl.E(3);
        public string? ChildCode => Hl.E(4);
        public List<Seg> Segs { get; } = [];
        public List<ClaimNode> Claims { get; } = [];
    }

    private sealed class ClaimNode
    {
        public required Seg Clm { get; init; }
        public required HlNode Hl { get; init; }
        public string? ClaimId => Clm.E(1);

        /// <summary>Every segment after CLM up to the first LX (2300 plus 2310/2320/2330).</summary>
        public List<Seg> Segs { get; } = [];

        /// <summary>Only the 2300 loop's own segments (so e.g. a 2330B REF*F8 cannot satisfy a 2300 rule).</summary>
        public IEnumerable<Seg> Own => Segs.Where(s => s.Loop == "2300");
        public List<LineNode> Lines { get; } = [];
    }

    private sealed class LineNode
    {
        public required Seg Lx { get; init; }
        public List<Seg> Segs { get; } = [];
        public Seg? Service { get; set; }
    }

    private sealed class TransactionSetValidator
    {
        private readonly X12837SnipValidator _owner;
        private readonly char _componentSeparator;
        private readonly SnipTransactionSetOutcome _outcome;
        private readonly IReadOnlyList<X12Segment> _segs;
        private readonly int _start;
        private readonly int _end;
        private readonly string? _groupVersion;

        private readonly List<Seg> _all = [];
        private readonly List<Seg> _header = [];
        private readonly List<HlNode> _hls = [];
        private bool _institutional;
        private string? _bhtDate;
        private readonly FindingBudget _budget;
        private readonly SnipValidationResult _result;
        private bool _setCapReported;
        private int _suppressed;
        private bool _suppressedError;

        public TransactionSetValidator(
            X12837SnipValidator owner, X12Document doc, SnipTransactionSetOutcome outcome,
            SnipFunctionalGroupOutcome group, IReadOnlyList<X12Segment> segs, int start, int end,
            FindingBudget budget, SnipValidationResult result)
        {
            _owner = owner;
            _componentSeparator = doc.ComponentSeparator;
            _outcome = outcome;
            _segs = segs;
            _start = start;
            _end = end;
            _groupVersion = group.VersionCode;
            _budget = budget;
            _result = result;
        }

        public void Run()
        {
            BuildTree();
            CheckEnvelope();
            foreach (var seg in _all) CheckSegmentSyntax(seg);

            if (_owner._options.ActionFor(SnipLevel.ImplementationGuide) != SnipAction.Off) CheckImplementationGuide();
            if (_owner._options.ActionFor(SnipLevel.Balancing) != SnipAction.Off) CheckBalancing();
            if (_owner._options.ActionFor(SnipLevel.Situational) != SnipAction.Off) CheckSituational();
            if (_owner._options.ActionFor(SnipLevel.ExternalCodeSets) != SnipAction.Off) CheckCodeSets();

            // Findings dropped by a cap still decide acceptance.
            if (_suppressedError) _outcome.SuppressedError = true;
            _outcome.SuppressedFindings = _suppressed;
        }

        // ── Tree + loop ids ──────────────────────────────────────────────

        private void BuildTree()
        {
            var st = _segs[_start];
            _institutional = st.Element(2) is { } st03 && Institutional.Contains(st03);

            var hasSe = _end < _segs.Count && _segs[_end].Id == "SE";
            var last = hasSe ? _end : _end - 1;

            HlNode? hl = null;
            ClaimNode? claim = null;
            LineNode? line = null;
            string? loop = null;
            var inClaimSubscriber = false;

            for (var i = _start; i <= last && i < _segs.Count; i++)
            {
                var s = _segs[i];
                var pos = i - _start + 1;

                switch (s.Id)
                {
                    case "ST" or "BHT" or "SE":
                        loop = null;
                        break;
                    case "HL":
                        loop = s.Element(2) switch { "20" => "2000A", "22" => "2000B", "23" => "2000C", _ => "2000" };
                        claim = null;
                        line = null;
                        inClaimSubscriber = false;
                        break;
                    case "CLM":
                        loop = "2300";
                        line = null;
                        inClaimSubscriber = false;
                        break;
                    case "SBR" when claim is not null:
                        loop = "2320";
                        inClaimSubscriber = true;
                        line = null;
                        break;
                    case "LX":
                        loop = "2400";
                        inClaimSubscriber = false;
                        break;
                    case "LIN" when line is not null:
                        loop = "2410";
                        break;
                    case "SVD" when line is not null:
                        loop = "2430";
                        break;
                    case "NM1":
                        loop = NameLoop(s.Element(0), hl, claim, line, inClaimSubscriber) ?? loop;
                        break;
                }

                var seg = new Seg(s, pos, loop);
                _all.Add(seg);

                switch (s.Id)
                {
                    case "ST" or "SE":
                        break;
                    case "HL":
                        hl = new HlNode { Hl = seg };
                        _hls.Add(hl);
                        claim = null;
                        line = null;
                        break;
                    case "CLM" when hl is not null:
                        claim = new ClaimNode { Clm = seg, Hl = hl };
                        hl.Claims.Add(claim);
                        line = null;
                        break;
                    case "CLM":
                        Report(SnipLevel.ImplementationGuide, "L2-CLM-OUTSIDE-HL", "CLM appears before any HL loop.", seg, segCode: "2");
                        break;
                    case "LX" when claim is not null:
                        line = new LineNode { Lx = seg };
                        claim.Lines.Add(line);
                        break;
                    default:
                        if (line is not null)
                        {
                            line.Segs.Add(seg);
                            if (s.Id is "SV1" or "SV2" && line.Service is null) line.Service = seg;
                        }
                        else if (claim is not null) claim.Segs.Add(seg);
                        else if (hl is not null) hl.Segs.Add(seg);
                        else _header.Add(seg);
                        break;
                }
            }

            _bhtDate = _header.FirstOrDefault(s => s.Id == "BHT")?.E(4);

            // Parent HL id → level codes of its children, built once so
            // hierarchy checks stay linear on files with many HL loops.
            foreach (var node in _hls)
            {
                if (node.Parent is null) continue;
                if (!_childLevels.TryGetValue(node.Parent, out var levels))
                    _childLevels[node.Parent] = levels = new HashSet<string>(StringComparer.Ordinal);
                levels.Add(node.LevelCode ?? string.Empty);
            }
        }

        private readonly Dictionary<string, HashSet<string>> _childLevels = new(StringComparer.Ordinal);

        private string? NameLoop(string? entity, HlNode? hl, ClaimNode? claim, LineNode? line, bool inClaimSubscriber)
        {
            if (line is not null) return "2420";
            if (inClaimSubscriber) return "2330";
            if (claim is not null)
            {
                return _institutional
                    ? entity switch { "71" => "2310A", "72" => "2310B", "ZZ" => "2310C", "82" => "2310D", "77" => "2310E", "DN" => "2310F", _ => "2310" }
                    : entity switch { "DN" => "2310A", "82" => "2310B", "77" => "2310C", "DQ" => "2310D", "PW" => "2310E", "45" => "2310F", _ => "2310" };
            }
            if (hl is null)
                return entity switch { "41" => "1000A", "40" => "1000B", _ => null };
            return (hl.LevelCode, entity) switch
            {
                ("20", "85") => "2010AA",
                ("20", "87") => "2010AB",
                ("20", "PE") => "2010AC",
                ("22", "IL") => "2010BA",
                ("22", "PR") => "2010BB",
                ("23", "QC") => "2010CA",
                _ => null,
            };
        }

        // ── Level 1: ST/SE and segment syntax ────────────────────────────

        private void CheckEnvelope()
        {
            var st = _all[0];
            if (st.E(1) != "837")
            {
                _outcome.TransactionSetErrorCodes.Add("1");
                Report(SnipLevel.Syntax, "L1-ST01", $"ST01 must be 837, found '{st.E(1)}'.", st, 1, elemCode: "7", dataRef: "143", badValue: st.E(1));
            }

            var st03 = st.E(3);
            if (st03 != Professional && (st03 is null || !Institutional.Contains(st03)))
            {
                _outcome.TransactionSetErrorCodes.Add("I6");
                Report(SnipLevel.Syntax, "L1-ST03", $"ST03 '{st03}' is not a supported 837 implementation (005010X222A1, 005010X223A2).", st, 3, elemCode: "7", dataRef: "1705", badValue: st03);
            }
            else if (_groupVersion is not null && st03 != _groupVersion)
            {
                Report(SnipLevel.Syntax, "L1-ST03-GS08", $"ST03 '{st03}' does not match GS08 '{_groupVersion}'.", st, 3, elemCode: "7", dataRef: "1705", badValue: st03);
            }

            if (_outcome.TransactionSetErrorCodes.Contains("23"))
                Report(SnipLevel.Syntax, "L1-ST02-DUPLICATE", $"ST02 '{st.E(2)}' is used by another transaction set in this functional group.", st, 2, elemCode: "7", dataRef: "329");

            var se = _all[^1];
            if (se.Id != "SE")
            {
                _outcome.TransactionSetErrorCodes.Add("2");
                Report(SnipLevel.Syntax, "L1-SE-MISSING", "Transaction set has no SE trailer.", null, segmentId: "SE", position: se.Pos + 1, segCode: "3");
                return;
            }

            if (se.E(2) != st.E(2))
            {
                _outcome.TransactionSetErrorCodes.Add("3");
                Report(SnipLevel.Syntax, "L1-SE02", $"SE02 '{se.E(2)}' does not match ST02 '{st.E(2)}'.", se, 2, elemCode: "7", dataRef: "329");
            }

            if (!int.TryParse(se.E(1), NumberStyles.None, CultureInfo.InvariantCulture, out var declared) || declared != _all.Count)
            {
                _outcome.TransactionSetErrorCodes.Add("4");
                Report(SnipLevel.Syntax, "L1-SE01", $"SE01 '{se.E(1)}' does not match the {_all.Count} segments from ST to SE.", se, 1, elemCode: "7", dataRef: "96");
            }
        }

        private void CheckSegmentSyntax(Seg seg)
        {
            if (seg.Id is "ST" or "SE") return;

            if (!Known837Segments.Contains(seg.Id))
            {
                Report(SnipLevel.Syntax, "L1-SEGMENT-ID", $"Segment id '{SafeSegmentId(seg.Id)}' is not used in an 837.", seg, segCode: "1");
                return;
            }

            if (MandatoryElements.TryGetValue(seg.Id, out var mandatory))
            {
                foreach (var n in mandatory)
                {
                    if (seg.E(n) is null)
                        Report(SnipLevel.Syntax, "L1-MANDATORY-ELEMENT", $"{seg.Id}{n:00} is a mandatory element and is missing.", seg, n, elemCode: "1");
                }
            }

            foreach (var (segId, element, dataRef) in NumericElements)
            {
                if (seg.Id != segId || seg.E(element) is not { } value) continue;
                if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                    Report(SnipLevel.Syntax, "L1-NUMERIC", $"{seg.Id}{element:00} must be numeric.", seg, element, elemCode: "6", dataRef: dataRef);
            }

            if (seg.Id == "CLM" && seg.E(1) is { Length: > 38 })
                Report(SnipLevel.Syntax, "L1-CLM01-LENGTH", "CLM01 exceeds 38 characters.", seg, 1, elemCode: "5", dataRef: "1028");

            if (seg.Id == "DTP" && seg.E(3) is { } dtpValue)
            {
                var valid = seg.E(2) switch
                {
                    "D8" => IsDate(dtpValue, "yyyyMMdd"),
                    "RD8" => IsPeriod(dtpValue),
                    "DT" => dtpValue.Length == 12 && IsDate(dtpValue[..8], "yyyyMMdd") && IsTime(dtpValue[8..]),
                    "TM" => IsTime(dtpValue),
                    _ => true,
                };
                if (!valid)
                    Report(SnipLevel.Syntax, "L1-DATE", $"DTP{seg.E(1)} value is not a valid {seg.E(2)} date.", seg, 3, elemCode: "8", dataRef: "1251");
            }

            if (seg.Id == "DMG" && seg.E(1) == "D8" && seg.E(2) is { } dob && !IsDate(dob, "yyyyMMdd"))
                Report(SnipLevel.Syntax, "L1-DATE", "DMG02 is not a valid CCYYMMDD date.", seg, 2, elemCode: "8", dataRef: "1251");

            if (seg.Id == "BHT")
            {
                if (seg.E(4) is { } bht04 && !IsDate(bht04, "yyyyMMdd"))
                    Report(SnipLevel.Syntax, "L1-DATE", "BHT04 is not a valid CCYYMMDD date.", seg, 4, elemCode: "8", dataRef: "373");
                if (seg.E(5) is { } bht05 && !IsTime(bht05))
                    Report(SnipLevel.Syntax, "L1-TIME", "BHT05 is not a valid time.", seg, 5, elemCode: "9", dataRef: "337");
            }
        }

        // ── Level 2: implementation guide ────────────────────────────────

        private void CheckImplementationGuide()
        {
            const SnipLevel L2 = SnipLevel.ImplementationGuide;
            var st = _all[0];

            var bht = _all.Count > 1 && _all[1].Id == "BHT" ? _all[1] : null;
            if (bht is null)
            {
                Report(L2, "L2-BHT-MISSING", "BHT must follow ST.", null, segmentId: "BHT", position: 2, segCode: "3");
            }
            else
            {
                if (bht.E(1) != "0019") Report(L2, "L2-BHT01", "BHT01 must be 0019.", bht, 1, elemCode: "7", dataRef: "1005", badValue: bht.E(1));
                if (bht.E(2) is not ("00" or "18")) Report(L2, "L2-BHT02", "BHT02 must be 00 (original) or 18 (reissue).", bht, 2, elemCode: "7", dataRef: "353", badValue: bht.E(2));
                if (bht.E(3) is null) Report(L2, "L2-BHT03", "BHT03 originator application transaction id is required.", bht, 3, elemCode: "1", dataRef: "127");
                if (bht.E(4) is null) Report(L2, "L2-BHT04", "BHT04 creation date is required.", bht, 4, elemCode: "1", dataRef: "373");
                if (bht.E(6) is not ("CH" or "RP" or "31")) Report(L2, "L2-BHT06", "BHT06 must be CH, RP or 31.", bht, 6, elemCode: "7", dataRef: "640", badValue: bht.E(6));
            }

            var firstHlPos = _hls.Count > 0 ? _hls[0].Hl.Pos : _all[^1].Pos;
            var submitter = _header.FirstOrDefault(s => s.Id == "NM1" && s.E(1) == "41");
            if (submitter is null)
            {
                Report(L2, "L2-1000A-MISSING", "Loop 1000A submitter name (NM1*41) is required.", null, segmentId: "NM1", loop: "1000A", position: firstHlPos, segCode: "I7");
            }
            else
            {
                if (submitter.E(8) != "46") Report(L2, "L2-NM108", "1000A NM108 must be 46.", submitter, 8, elemCode: "7", dataRef: "66", badValue: submitter.E(8));
                if (submitter.E(9) is null) Report(L2, "L2-NM109", "1000A NM109 submitter identifier is required.", submitter, 9, elemCode: "1", dataRef: "67");
                if (!_header.Any(s => s.Id == "PER" && s.Loop == "1000A"))
                    Report(L2, "L2-1000A-PER", "Loop 1000A submitter contact (PER) is required.", null, segmentId: "PER", loop: "1000A", position: submitter.Pos + 1, segCode: "3");
            }

            if (!_header.Any(s => s.Id == "NM1" && s.E(1) == "40"))
                Report(L2, "L2-1000B-MISSING", "Loop 1000B receiver name (NM1*40) is required.", null, segmentId: "NM1", loop: "1000B", position: firstHlPos, segCode: "I7");

            CheckHierarchy();

            foreach (var hl in _hls)
            {
                switch (hl.LevelCode)
                {
                    case "20": CheckBillingProvider(hl); break;
                    case "22": CheckSubscriber(hl); break;
                    case "23": CheckPatient(hl); break;
                }
                foreach (var claim in hl.Claims) CheckClaimIg(claim);
            }
        }

        private void CheckHierarchy()
        {
            const SnipLevel L2 = SnipLevel.ImplementationGuide;
            if (!_hls.Any(h => h.LevelCode == "20"))
                Report(L2, "L2-2000A-MISSING", "Loop 2000A billing provider (HL*20) is required.", null, segmentId: "HL", loop: "2000A", position: _all[^1].Pos, segCode: "I7");
            if (!_hls.Any(h => h.LevelCode == "22"))
                Report(L2, "L2-2000B-MISSING", "Loop 2000B subscriber (HL*22) is required.", null, segmentId: "HL", loop: "2000B", position: _all[^1].Pos, segCode: "I7");

            var byId = new Dictionary<string, HlNode>(StringComparer.Ordinal);
            for (var i = 0; i < _hls.Count; i++)
            {
                var hl = _hls[i];
                if (hl.Id != (i + 1).ToString(CultureInfo.InvariantCulture))
                    Report(L2, "L2-HL01-SEQUENCE", $"HL01 must number HL segments 1, 2, 3…; expected {i + 1}.", hl.Hl, 1, elemCode: "7", dataRef: "628");
                byId.TryAdd(hl.Id, hl);

                if (hl.LevelCode is not ("20" or "22" or "23"))
                {
                    Report(L2, "L2-HL03", "HL03 must be 20, 22 or 23.", hl.Hl, 3, elemCode: "7", dataRef: "735", badValue: hl.LevelCode);
                    continue;
                }

                var expectedParent = hl.LevelCode switch { "22" => "20", "23" => "22", _ => null };
                if (expectedParent is null)
                {
                    if (hl.Parent is not null)
                        Report(L2, "L2-HL02", "HL02 must be empty on a billing provider (HL*20) loop.", hl.Hl, 2, elemCode: "I10", dataRef: "734");
                }
                else if (hl.Parent is null || !byId.TryGetValue(hl.Parent, out var parent) || parent.LevelCode != expectedParent)
                {
                    Report(L2, "L2-HL02", $"HL02 must point to an earlier HL*{expectedParent} loop.", hl.Hl, 2, elemCode: "7", dataRef: "734");
                }

                var hasChildren = _childLevels.ContainsKey(hl.Id);
                if (hl.ChildCode is not ("0" or "1"))
                    Report(L2, "L2-HL04", "HL04 must be 0 or 1.", hl.Hl, 4, elemCode: "7", dataRef: "736", badValue: hl.ChildCode);
                else if ((hl.ChildCode == "1") != hasChildren)
                    Report(L2, "L2-HL04", hasChildren ? "HL04 is 0 but child HL loops follow." : "HL04 is 1 but no child HL loop follows.", hl.Hl, 4, elemCode: "7", dataRef: "736", badValue: hl.ChildCode);

                if (hl.LevelCode is "22" or "23" && !hasChildren && hl.Claims.Count == 0)
                    Report(L2, "L2-HL-NO-CLAIM", $"HL*{hl.LevelCode} loop {hl.Id} has no claim (CLM).", hl.Hl, segCode: "I7");
                if (hl.LevelCode == "20" && hl.Claims.Count > 0)
                    Report(L2, "L2-CLM-UNDER-2000A", "CLM cannot appear directly under the billing provider loop.", hl.Claims[0].Clm, segCode: "2");
            }
        }

        private void CheckBillingProvider(HlNode hl)
        {
            const SnipLevel L2 = SnipLevel.ImplementationGuide;
            var nm1 = hl.Segs.FirstOrDefault(s => s.Id == "NM1" && s.E(1) == "85");
            if (nm1 is null)
            {
                Report(L2, "L2-2010AA-MISSING", "Loop 2010AA billing provider name (NM1*85) is required.", hl.Hl, segmentId: "NM1", loop: "2010AA", segCode: "I7");
                return;
            }
            if (nm1.E(3) is null) Report(L2, "L2-NM103", "2010AA NM103 billing provider name is required.", nm1, 3, elemCode: "1", dataRef: "1035");
            if (nm1.E(8) != "XX") Report(L2, "L2-NM108", "2010AA NM108 must be XX (NPI).", nm1, 8, elemCode: "7", dataRef: "66", badValue: nm1.E(8));
            if (nm1.E(9) is null) Report(L2, "L2-NM109", "2010AA NM109 billing provider NPI is required.", nm1, 9, elemCode: "1", dataRef: "67");

            var loopSegs = hl.Segs.Where(s => s.Loop == "2010AA").ToList();
            if (!loopSegs.Any(s => s.Id == "N3"))
                Report(L2, "L2-2010AA-N3", "2010AA billing provider address (N3) is required.", nm1, segmentId: "N3", loop: "2010AA", position: nm1.Pos + 1, segCode: "3");
            if (!loopSegs.Any(s => s.Id == "N4"))
                Report(L2, "L2-2010AA-N4", "2010AA billing provider city/state/ZIP (N4) is required.", nm1, segmentId: "N4", loop: "2010AA", position: nm1.Pos + 1, segCode: "3");
            if (!loopSegs.Any(s => s.Id == "REF" && s.E(1) is "EI" or "SY"))
                Report(L2, "L2-2010AA-TAXID", "2010AA billing provider tax id (REF*EI or REF*SY) is required.", nm1, segmentId: "REF", loop: "2010AA", position: nm1.Pos + 1, segCode: "3");

            CheckPayToAddress(hl);
            CheckPayToPlan(hl);
        }

        /// <summary>
        /// 2010AB pay-to address. In 5010 only NM101 (87) and NM102 (1 or 2)
        /// are used; the loop carries the address (N3 and N4, both required)
        /// and no name or identifier (X12 RFI 1522).
        /// </summary>
        private void CheckPayToAddress(HlNode hl)
        {
            const SnipLevel L2 = SnipLevel.ImplementationGuide;
            var nm1 = hl.Segs.FirstOrDefault(s => s.Id == "NM1" && s.Loop == "2010AB");
            if (nm1 is null) return;

            if (nm1.E(2) is not ("1" or "2"))
                Report(L2, "L2-NM102", "2010AB NM102 entity type must be 1 or 2.", nm1, 2, elemCode: "7", dataRef: "1065", badValue: nm1.E(2));
            // Warn only: many submitters still send the 4010-style
            // NM1*87*2*NAME*****XX*NPI. The name/id are ignored (the payee
            // is the 2010AA billing provider), so they are no reason to
            // reject the whole transaction set.
            for (var n = 3; n <= 12; n++)
            {
                if (nm1.E(n) is null) continue;
                Report(L2, "L2-2010AB-NOT-USED", $"2010AB NM1{n:00} is not used: the pay-to address loop carries no name or identifier in 5010 (ignored).", nm1, n, elemCode: "I10", warnOnly: true);
            }

            // 2010AB repeats at most once per 2000A. The parser keeps the
            // last one, so a second NM1*87 is reported, but only as a warning.
            foreach (var extra in hl.Segs.Where(s => s.Id == "NM1" && s.Loop == "2010AB").Skip(1))
                Report(L2, "L2-2010AB-REPEAT", "2010AB pay-to address appears more than once in this billing provider loop; only the last one is used.", extra, segCode: "5", warnOnly: true);

            var loopSegs = hl.Segs.Where(s => s.Loop == "2010AB").ToList();
            if (!loopSegs.Any(s => s.Id == "N3"))
                Report(L2, "L2-2010AB-N3", "2010AB pay-to address (N3) is required.", nm1, segmentId: "N3", loop: "2010AB", position: nm1.Pos + 1, segCode: "3");
            if (!loopSegs.Any(s => s.Id == "N4"))
                Report(L2, "L2-2010AB-N4", "2010AB pay-to address city/state/ZIP (N4) is required.", nm1, segmentId: "N4", loop: "2010AB", position: nm1.Pos + 1, segCode: "3");
        }

        /// <summary>
        /// 2010AC pay-to plan: NM102 = 2, NM103 plan name, NM108 PI or XV and
        /// NM109 plan id, N3, N4 and the plan's tax id (REF*EI) are required.
        /// </summary>
        private void CheckPayToPlan(HlNode hl)
        {
            const SnipLevel L2 = SnipLevel.ImplementationGuide;
            var nm1 = hl.Segs.FirstOrDefault(s => s.Id == "NM1" && s.Loop == "2010AC");
            if (nm1 is null) return;

            if (nm1.E(2) != "2") Report(L2, "L2-NM102", "2010AC NM102 entity type must be 2 (non-person).", nm1, 2, elemCode: "7", dataRef: "1065", badValue: nm1.E(2));
            if (nm1.E(3) is null) Report(L2, "L2-NM103", "2010AC NM103 pay-to plan name is required.", nm1, 3, elemCode: "1", dataRef: "1035");
            if (nm1.E(8) is not ("PI" or "XV")) Report(L2, "L2-NM108", "2010AC NM108 must be PI or XV.", nm1, 8, elemCode: "7", dataRef: "66", badValue: nm1.E(8));
            if (nm1.E(9) is null) Report(L2, "L2-NM109", "2010AC NM109 pay-to plan identifier is required.", nm1, 9, elemCode: "1", dataRef: "67");

            var loopSegs = hl.Segs.Where(s => s.Loop == "2010AC").ToList();
            if (!loopSegs.Any(s => s.Id == "N3"))
                Report(L2, "L2-2010AC-N3", "2010AC pay-to plan address (N3) is required.", nm1, segmentId: "N3", loop: "2010AC", position: nm1.Pos + 1, segCode: "3");
            if (!loopSegs.Any(s => s.Id == "N4"))
                Report(L2, "L2-2010AC-N4", "2010AC pay-to plan city/state/ZIP (N4) is required.", nm1, segmentId: "N4", loop: "2010AC", position: nm1.Pos + 1, segCode: "3");
            if (!loopSegs.Any(s => s.Id == "REF" && s.E(1) == "EI"))
                Report(L2, "L2-2010AC-TAXID", "2010AC pay-to plan tax id (REF*EI) is required.", nm1, segmentId: "REF", loop: "2010AC", position: nm1.Pos + 1, segCode: "3");
        }

        private void CheckSubscriber(HlNode hl)
        {
            const SnipLevel L2 = SnipLevel.ImplementationGuide;
            var sbr = hl.Segs.FirstOrDefault(s => s.Id == "SBR");
            if (sbr is null)
            {
                Report(L2, "L2-2000B-SBR", "Loop 2000B subscriber information (SBR) is required.", hl.Hl, segmentId: "SBR", loop: "2000B", position: hl.Hl.Pos + 1, segCode: "3");
            }
            else if (sbr.E(1) is not { Length: 1 } sbr01 || !"ABCDEFGHPSTU".Contains(sbr01[0]))
            {
                Report(L2, "L2-SBR01", "SBR01 payer responsibility must be one of A–H, P, S, T, U.", sbr, 1, elemCode: "7", dataRef: "1138", badValue: sbr.E(1));
            }

            var subscriber = hl.Segs.FirstOrDefault(s => s.Id == "NM1" && s.E(1) == "IL");
            if (subscriber is null)
                Report(L2, "L2-2010BA-MISSING", "Loop 2010BA subscriber name (NM1*IL) is required.", hl.Hl, segmentId: "NM1", loop: "2010BA", segCode: "I7");
            else if (subscriber.E(9) is null)
                Report(L2, "L2-NM109", "2010BA NM109 subscriber member id is required.", subscriber, 9, elemCode: "1", dataRef: "67");

            if (!hl.Segs.Any(s => s.Id == "NM1" && s.E(1) == "PR"))
                Report(L2, "L2-2010BB-MISSING", "Loop 2010BB payer name (NM1*PR) is required.", hl.Hl, segmentId: "NM1", loop: "2010BB", segCode: "I7");

            foreach (var dmg in hl.Segs.Where(s => s.Id == "DMG")) CheckDmg(dmg);
        }

        private void CheckPatient(HlNode hl)
        {
            const SnipLevel L2 = SnipLevel.ImplementationGuide;
            if (!hl.Segs.Any(s => s.Id == "PAT"))
                Report(L2, "L2-2000C-PAT", "Loop 2000C patient information (PAT) is required.", hl.Hl, segmentId: "PAT", loop: "2000C", position: hl.Hl.Pos + 1, segCode: "3");
            if (!hl.Segs.Any(s => s.Id == "NM1" && s.E(1) == "QC"))
                Report(L2, "L2-2010CA-MISSING", "Loop 2010CA patient name (NM1*QC) is required.", hl.Hl, segmentId: "NM1", loop: "2010CA", segCode: "I7");
            foreach (var dmg in hl.Segs.Where(s => s.Id == "DMG")) CheckDmg(dmg);
        }

        private void CheckDmg(Seg dmg)
        {
            const SnipLevel L2 = SnipLevel.ImplementationGuide;
            if (dmg.E(1) != "D8") Report(L2, "L2-DMG01", "DMG01 must be D8.", dmg, 1, elemCode: "7", dataRef: "1250", badValue: dmg.E(1));
            if (dmg.E(2) is null) Report(L2, "L2-DMG02", "DMG02 birth date is required.", dmg, 2, elemCode: "1", dataRef: "1251");
            if (dmg.E(3) is not ("F" or "M" or "U")) Report(L2, "L2-DMG03", "DMG03 gender must be F, M or U.", dmg, 3, elemCode: "7", dataRef: "1068", badValue: dmg.E(3));
        }

        private void CheckClaimIg(ClaimNode claim)
        {
            const SnipLevel L2 = SnipLevel.ImplementationGuide;
            var clm = claim.Clm;
            var id = claim.ClaimId;

            var clm05 = Components(clm.E(5));
            if (clm05.Length < 3 || clm05[0].Length == 0 || clm05[1].Length == 0 || clm05[2].Length == 0)
            {
                Report(L2, "L2-CLM05", "CLM05 must carry facility/place of service code, qualifier and frequency code.", clm, 5, elemCode: "1", dataRef: "C023", claimId: id);
            }
            else
            {
                var expectedQualifier = _institutional ? "A" : "B";
                if (clm05[1] != expectedQualifier)
                    Report(L2, "L2-CLM05-2", $"CLM05-2 must be {expectedQualifier}.", clm, 5, 2, elemCode: "7", dataRef: "1332", badValue: clm05[1], claimId: id);
                var frequencyOk = _institutional
                    ? clm05[2].Length == 1 && char.IsAsciiLetterOrDigit(clm05[2][0])
                    : clm05[2] is "1" or "6" or "7" or "8";
                if (!frequencyOk)
                    Report(L2, "L2-CLM05-3", "CLM05-3 claim frequency code is not valid.", clm, 5, 3, elemCode: "7", dataRef: "1325", badValue: clm05[2], claimId: id);
            }

            if (!_institutional && clm.E(6) is not ("Y" or "N"))
                Report(L2, "L2-CLM06", "CLM06 provider signature indicator must be Y or N.", clm, 6, elemCode: clm.E(6) is null ? "1" : "7", dataRef: "1073", claimId: id);
            if (clm.E(7) is not ("A" or "B" or "C"))
                Report(L2, "L2-CLM07", "CLM07 assignment/plan participation code must be A, B or C.", clm, 7, elemCode: clm.E(7) is null ? "1" : "7", dataRef: "1359", claimId: id);
            if (clm.E(8) is not ("N" or "W" or "Y"))
                Report(L2, "L2-CLM08", "CLM08 benefits assignment indicator must be N, W or Y.", clm, 8, elemCode: clm.E(8) is null ? "1" : "7", dataRef: "1073", claimId: id);
            if (clm.E(9) is not ("I" or "Y"))
                Report(L2, "L2-CLM09", "CLM09 release of information code must be I or Y.", clm, 9, elemCode: clm.E(9) is null ? "1" : "7", dataRef: "1363", claimId: id);

            // 837P has no 2300 DTP*472: the service date belongs on each 2400 line.
            if (!_institutional && claim.Own.FirstOrDefault(s => s.Id == "DTP" && s.E(1) == "472") is { } claimDtp472)
                Report(L2, "L2-2300-DTP472", "837P loop 2300 does not use DTP*472; send the service date on each 2400 line.",
                    claimDtp472, segCode: "I4", claimId: id);

            // The principal diagnosis (ABK, or BK for ICD-9) is HI01 of one of
            // the 2300 HI segments — not necessarily the first HI on an 837I.
            var hiSegs = claim.Own.Where(s => s.Id == "HI").ToList();
            var hasPrincipal = hiSegs.Any(h => Components(h.E(1)) is [ "ABK" or "BK", { Length: > 0 }, ..]);
            if (hiSegs.Count == 0)
            {
                Report(L2, "L2-HI-PRINCIPAL", "The claim needs an HI segment carrying the principal diagnosis (ABK).",
                    null, segmentId: "HI", loop: "2300", position: PositionAfter(claim.Own, Before2300Hi), segCode: "3", claimId: id);
            }
            else if (!hasPrincipal)
            {
                Report(L2, "L2-HI-PRINCIPAL", "No 2300 HI segment carries the principal diagnosis (HI01 qualifier ABK).",
                    hiSegs[0], 1, 1, elemCode: "7", dataRef: "1270", badValue: Components(hiSegs[0].E(1)).FirstOrDefault(), claimId: id);
            }

            if (!_institutional)
            {
                var dxCount = DiagnosisCodes(claim).Count;
                if (dxCount > 12)
                    Report(L2, "L2-HI-MAX", $"An 837P claim carries at most 12 diagnosis codes; found {dxCount}.", hiSegs[0], segCode: "5", claimId: id);
            }
            else if (!claim.Own.Any(s => s.Id == "DTP" && s.E(1) == "434"))
            {
                Report(L2, "L2-DTP434", "837I statement dates (DTP*434) are required.", clm, segmentId: "DTP", loop: "2300", position: clm.Pos + 1, segCode: "3", claimId: id);
            }

            if (claim.Lines.Count == 0)
            {
                Report(L2, "L2-2400-MISSING", "A claim needs at least one service line (LX loop 2400).", clm, segmentId: "LX", loop: "2400", segCode: "I7", claimId: id);
                return;
            }

            var maxLines = _institutional ? 999 : 50;
            if (claim.Lines.Count > maxLines)
                Report(L2, "L2-2400-MAX", $"Claim has {claim.Lines.Count} service lines; the implementation guide allows {maxLines}.", claim.Lines[maxLines].Lx, segCode: "4", claimId: id);

            for (var i = 0; i < claim.Lines.Count; i++)
            {
                var line = claim.Lines[i];
                if (line.Lx.E(1) != (i + 1).ToString(CultureInfo.InvariantCulture))
                    Report(L2, "L2-LX01", $"LX01 must number lines 1, 2, 3… within the claim; expected {i + 1}.", line.Lx, 1, elemCode: "7", dataRef: "554", claimId: id);

                var expected = _institutional ? "SV2" : "SV1";
                if (line.Service is null || line.Service.Id != expected)
                {
                    Report(L2, $"L2-{expected}-MISSING", $"Line {i + 1} needs an {expected} service segment.", line.Lx, segmentId: expected, loop: "2400", position: line.Lx.Pos + 1, segCode: "3", claimId: id);
                    continue;
                }

                var sv = line.Service;
                if (_institutional)
                {
                    var proc = Components(sv.E(2));
                    if (proc.Length > 0 && proc[0].Length > 0 && proc[0] is not ("HC" or "ER" or "HP" or "IV" or "WK"))
                        Report(L2, "L2-SV202-1", "SV202-1 procedure qualifier must be HC, ER, HP, IV or WK.", sv, 2, 1, elemCode: "7", dataRef: "235", badValue: proc[0], claimId: id);
                    if (sv.E(3) is null) Report(L2, "L2-SV203", "SV203 line charge is required.", sv, 3, elemCode: "1", dataRef: "782", claimId: id);
                    if (sv.E(4) is not ("UN" or "DA")) Report(L2, "L2-SV204", "SV204 must be UN or DA.", sv, 4, elemCode: "7", dataRef: "355", badValue: sv.E(4), claimId: id);
                    if (sv.E(5) is null) Report(L2, "L2-SV205", "SV205 service unit count is required.", sv, 5, elemCode: "1", dataRef: "380", claimId: id);
                }
                else
                {
                    var proc = Components(sv.E(1));
                    if (proc.Length < 2 || proc[0] is not ("HC" or "ER" or "IV" or "WK"))
                        Report(L2, "L2-SV101", "SV101 must carry a procedure qualifier (HC, ER, IV, WK) and code.", sv, 1, 1, elemCode: "7", dataRef: "235", badValue: proc.ElementAtOrDefault(0), claimId: id);
                    if (sv.E(3) is not ("MJ" or "UN")) Report(L2, "L2-SV103", "SV103 must be MJ or UN.", sv, 3, elemCode: "7", dataRef: "355", badValue: sv.E(3), claimId: id);
                    if (sv.E(4) is null) Report(L2, "L2-SV104", "SV104 service unit count is required.", sv, 4, elemCode: "1", dataRef: "380", claimId: id);
                    if (sv.E(7) is null) Report(L2, "L2-SV107", "SV107 diagnosis code pointer is required.", sv, 7, elemCode: "1", dataRef: "C004", claimId: id);
                }
            }
        }

        // ── Level 3: balancing ───────────────────────────────────────────

        private void CheckBalancing()
        {
            foreach (var claim in _hls.SelectMany(h => h.Claims))
            {
                if (!TryAmount(claim.Clm.E(2), out var total) || claim.Lines.Count == 0) continue;

                var sum = 0m;
                var complete = true;
                foreach (var line in claim.Lines)
                {
                    var chargeElement = _institutional ? 3 : 2;
                    if (line.Service is null || !TryAmount(line.Service.E(chargeElement), out var charge))
                    {
                        complete = false;
                        break;
                    }
                    sum += charge;
                }

                if (complete && sum != total)
                {
                    var lineField = _institutional ? "SV203" : "SV102";
                    Report(SnipLevel.Balancing, "L3-CLM-BALANCE",
                        $"CLM02 total charge {total.ToString("0.00", CultureInfo.InvariantCulture)} does not equal the sum of {lineField} line charges {sum.ToString("0.00", CultureInfo.InvariantCulture)}.",
                        claim.Clm, 2, elemCode: "I12", dataRef: "782", claimId: claim.ClaimId);
                }
            }

            foreach (var claim in _hls.SelectMany(h => h.Claims))
                CheckCobBalancing(claim);
        }

        /// <summary>
        /// TR3 coordination-of-benefits balancing (warnings only — never a
        /// reject, since a payer's own remittance may not balance and the
        /// claim is still processable; COB pends what it cannot place):
        /// <list type="bullet">
        ///   <item><description>per 2320 other payer: CLM02 = AMT*D + Σ its
        ///     2320 CAS + Σ its 2430 CAS (lines it reported; with no 2430 the
        ///     2320 CAS alone);</description></item>
        ///   <item><description>per 2430: the line charge (SV102 / SV203) =
        ///     SVD02 + Σ its 2430 CAS.</description></item>
        /// </list>
        /// </summary>
        private void CheckCobBalancing(ClaimNode claim)
        {
            var chargeElement = _institutional ? 3 : 2;
            var lineField = _institutional ? "SV203" : "SV102";

            // 2430 groups: each SVD with the CAS segments that follow it.
            var svds = new List<(Seg Svd, decimal Paid, decimal Cas, decimal? Charge)>();
            foreach (var line in claim.Lines)
            {
                decimal? charge = line.Service is not null && TryAmount(line.Service.E(chargeElement), out var c) ? c : null;
                // A 2430 loop runs from its SVD to the next SVD or the end of
                // loop 2430; its CAS segments are the 2430 CAS.
                foreach (var seg in line.Segs.Where(s => s.Loop == "2430"))
                {
                    if (seg.Id == "SVD")
                        svds.Add((seg, TryAmount(seg.E(2), out var paid) ? paid : 0m, 0m, charge));
                    else if (seg.Id == "CAS" && svds.Count > 0)
                        svds[^1] = (svds[^1].Svd, svds[^1].Paid, svds[^1].Cas + CasTotal(seg), svds[^1].Charge);
                }
            }

            foreach (var (svd, paid, cas, charge) in svds)
            {
                if (charge is { } lineCharge && paid + cas != lineCharge)
                {
                    Report(SnipLevel.Balancing, "L3-COB-SVD-BALANCE",
                        $"2430 SVD for payer '{svd.E(1)}': SVD02 {paid.ToString("0.00", CultureInfo.InvariantCulture)} + 2430 CAS " +
                        $"{cas.ToString("0.00", CultureInfo.InvariantCulture)} does not equal the line charge {lineField} " +
                        $"{lineCharge.ToString("0.00", CultureInfo.InvariantCulture)}.",
                        svd, 2, elemCode: "I12", dataRef: "782", claimId: claim.ClaimId, warnOnly: true);
                }
            }

            if (!TryAmount(claim.Clm.E(2), out var total)) return;

            // 2320 groups: SBR … up to the next 2320 SBR.
            var payers = new List<(Seg Sbr, decimal? AmtD, decimal Cas, HashSet<string> Ids)>();
            foreach (var seg in claim.Segs.Where(s => s.Loop is "2320" or "2330"))
            {
                if (seg.Id == "SBR")
                {
                    payers.Add((seg, null, 0m, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
                    continue;
                }
                if (payers.Count == 0) continue;
                var p = payers[^1];
                if (seg.Id == "AMT" && seg.E(1) == "D" && TryAmount(seg.E(2), out var amtD))
                    payers[^1] = (p.Sbr, amtD, p.Cas, p.Ids);
                else if (seg.Id == "CAS" && seg.Loop == "2320")
                    payers[^1] = (p.Sbr, p.AmtD, p.Cas + CasTotal(seg), p.Ids);
                else if (seg.Id == "NM1" && seg.E(1) == "PR" && seg.E(9) is { Length: > 0 } nm109)
                    p.Ids.Add(nm109.Trim());
                else if (seg.Id == "REF" && seg.E(1) is "2U" or "FY" && seg.E(2) is { Length: > 0 } refId)
                    p.Ids.Add(refId.Trim());
            }

            foreach (var payer in payers)
            {
                if (payer.AmtD is not { } amtD) continue;
                var payerSvds = payers.Count == 1
                    ? svds
                    : svds.Where(s => s.Svd.E(1) is { } id && payer.Ids.Contains(id.Trim())).ToList();
                var lineCas = payerSvds.Sum(s => s.Cas);
                if (amtD + payer.Cas + lineCas != total)
                {
                    Report(SnipLevel.Balancing, "L3-COB-2320-BALANCE",
                        $"2320 payer (SBR01 '{payer.Sbr.E(1)}'): AMT*D {amtD.ToString("0.00", CultureInfo.InvariantCulture)} + " +
                        $"2320 CAS {payer.Cas.ToString("0.00", CultureInfo.InvariantCulture)} + its 2430 CAS " +
                        $"{lineCas.ToString("0.00", CultureInfo.InvariantCulture)} does not equal CLM02 " +
                        $"{total.ToString("0.00", CultureInfo.InvariantCulture)}.",
                        payer.Sbr, 1, elemCode: "I12", dataRef: "782", claimId: claim.ClaimId, warnOnly: true);
                }
            }
        }

        /// <summary>Sum of a CAS segment's adjustment amounts (CAS03, 06, 09, 12, 15, 18).</summary>
        private static decimal CasTotal(Seg cas)
        {
            var sum = 0m;
            foreach (var element in new[] { 3, 6, 9, 12, 15, 18 })
                if (TryAmount(cas.E(element), out var amount)) sum += amount;
            return sum;
        }

        // ── Missing-segment positions ────────────────────────────────────
        // For a missing segment, IK302 is the position where it should have
        // appeared: right after the last present segment that the IG orders
        // before it in the same loop.

        private static readonly string[] Before2300Ref = ["CLM", "DTP", "CL1", "PWK", "CN1", "AMT"];
        private static readonly string[] Before2300Hi = ["CLM", "DTP", "CL1", "PWK", "CN1", "AMT", "REF", "K3", "NTE", "CR1", "CR2", "CRC"];

        private static int? PositionAfter(IEnumerable<Seg> loopSegments, params string[] precedingIds)
        {
            Seg? last = null;
            foreach (var s in loopSegments)
            {
                if (precedingIds.Contains(s.Id) && (last is null || s.Pos > last.Pos)) last = s;
            }
            return last is null ? null : last.Pos + 1;
        }

        // ── Level 4: situational ─────────────────────────────────────────

        private void CheckSituational()
        {
            const SnipLevel L4 = SnipLevel.Situational;

            foreach (var nm1 in _all.Where(s => s.Id == "NM1" && s.E(8) == "XX"))
            {
                if (nm1.E(9) is { } npi && !SnipCodeSets.IsValidNpi(npi))
                    Report(L4, "L4-NPI-CHECKDIGIT", $"NM109 NPI {npi} fails the NPI check digit (Luhn with prefix 80840).", nm1, 9, elemCode: "I12", dataRef: "67", badValue: npi);
            }

            foreach (var hl in _hls)
            {
                if (hl.LevelCode == "20")
                {
                    var n3 = hl.Segs.FirstOrDefault(s => s.Id == "N3" && s.Loop == "2010AA");
                    if (n3?.E(1) is { } street && IsPoBox(street))
                        Report(L4, "L4-2010AA-POBOX", "The billing provider address (2010AA N3) must be a street address, not a PO Box.", n3, 1, elemCode: "I12", dataRef: "166");

                    // The pay-to plan loop is used only on a subrogation
                    // demand, BHT06 = 31 (X12 RFI 1036).
                    if (hl.Segs.FirstOrDefault(s => s.Id == "NM1" && s.Loop == "2010AC") is { } payToPlan
                        && _header.FirstOrDefault(s => s.Id == "BHT")?.E(6) != "31")
                        Report(L4, "L4-2010AC-BHT06", "The pay-to plan loop (2010AC) is used only when BHT06 is 31 (subrogation demand).", payToPlan, segCode: "I9");
                }

                if (hl.LevelCode == "22" && hl.Segs.FirstOrDefault(s => s.Id == "SBR") is { } sbr && sbr.E(2) == "18")
                {
                    if (_childLevels.TryGetValue(hl.Id, out var childLevels) && childLevels.Contains("23"))
                        Report(L4, "L4-2000C-SELF", "SBR02 is 18 (subscriber is the patient), so no patient loop (2000C) may follow.", sbr, 2, elemCode: "10", dataRef: "1069");
                    if (!hl.Segs.Any(s => s.Id == "DMG" && s.Loop == "2010BA"))
                        Report(L4, "L4-2010BA-DMG", "Subscriber demographics (2010BA DMG) are required when the subscriber is the patient.",
                            null, segmentId: "DMG", loop: "2010BA", segCode: "I6",
                            position: PositionAfter(hl.Segs.Where(s => s.Loop == "2010BA"), "NM1", "N3", "N4") ?? hl.Hl.Pos + 1);
                }

                foreach (var claim in hl.Claims) CheckClaimSituational(claim);
            }
        }

        private void CheckClaimSituational(ClaimNode claim)
        {
            const SnipLevel L4 = SnipLevel.Situational;
            var clm = claim.Clm;
            var id = claim.ClaimId;
            var clm05 = Components(clm.E(5));

            if (clm05.Length >= 3 && clm05[2] is "7" or "8"
                && !claim.Own.Any(s => s.Id == "REF" && s.E(1) == "F8"))
            {
                Report(L4, "L4-REF-F8", "A replacement or void (CLM05-3 = 7 or 8) requires the payer claim control number (REF*F8) in loop 2300.",
                    null, segmentId: "REF", loop: "2300", segCode: "I6", claimId: id,
                    position: PositionAfter(claim.Own, Before2300Ref) ?? clm.Pos + 1);
            }

            var statement = claim.Own.FirstOrDefault(s => s.Id == "DTP" && s.E(1) == "434");
            var (statementFrom, statementTo) = Period(statement);

            // Inpatient/outpatient from the full facility + classification
            // code (CLM05-1). Codes not in either table are uncertain: the
            // rules that depend on the distinction then only warn.
            var setting = _institutional && clm05.Length >= 1 ? ClassifyTypeOfBill(clm05[0]) : BillSetting.Unknown;
            if (_institutional && setting != BillSetting.Outpatient)
            {
                var uncertain = setting == BillSetting.Unknown;
                var label = uncertain ? $"type of bill {SafeValue(clm05.ElementAtOrDefault(0))}x may be inpatient" : "an inpatient claim";
                if (!claim.Own.Any(s => s.Id == "DTP" && s.E(1) == "435"))
                    Report(L4, "L4-DTP435", $"Admission date (DTP*435) is required: {label}.",
                        null, segmentId: "DTP", loop: "2300", segCode: "I6", claimId: id, warnOnly: uncertain,
                        position: PositionAfter(claim.Own, "CLM", "DTP") ?? clm.Pos + 1);
                if (!claim.Own.Any(s => s.Id == "CL1"))
                    Report(L4, "L4-CL1", $"Institutional claim codes (CL1) are required: {label}.",
                        null, segmentId: "CL1", loop: "2300", segCode: "I6", claimId: id, warnOnly: uncertain,
                        position: PositionAfter(claim.Own, "CLM", "DTP") ?? clm.Pos + 1);
            }

            var dxCount = DiagnosisCodes(claim).Count;
            var bhtDate = ParseDate(_bhtDate);
            var multiDayStatement = statementFrom is not null && statementTo is not null && statementTo > statementFrom;

            foreach (var line in claim.Lines)
            {
                var dtp472 = line.Segs.FirstOrDefault(s => s.Id == "DTP" && s.E(1) == "472");
                if (dtp472 is null)
                {
                    var expectedAt = PositionAfter(line.Segs.Prepend(line.Lx), "LX", "SV1", "SV2", "SV5", "PWK", "CR1", "CR3", "CRC") ?? line.Lx.Pos + 1;
                    if (!_institutional)
                        Report(L4, "L4-DTP472", $"837P service line {SafeValue(line.Lx.E(1))} requires the service date (DTP*472).",
                            null, segmentId: "DTP", loop: "2400", segCode: "I6", claimId: id, position: expectedAt);
                    else if (setting != BillSetting.Inpatient && multiDayStatement)
                        Report(L4, "L4-DTP472", $"Outpatient service line {SafeValue(line.Lx.E(1))} requires a service date (DTP*472) because the statement covers more than one day.",
                            null, segmentId: "DTP", loop: "2400", segCode: "I6", claimId: id, position: expectedAt,
                            warnOnly: setting == BillSetting.Unknown);
                }
                else
                {
                    var (lineFrom, lineTo) = Period(dtp472);
                    if (_institutional && statementFrom is not null && statementTo is not null && lineFrom is not null
                        && (lineFrom < statementFrom || (lineTo ?? lineFrom) > statementTo))
                        Report(L4, "L4-DTP472-PERIOD", $"Service line {line.Lx.E(1)} date falls outside the statement period (DTP*434).", dtp472, 3, elemCode: "8", dataRef: "1251", claimId: id);
                    if (bhtDate is not null && lineFrom is not null && lineFrom > bhtDate)
                        Report(L4, "L4-DTP472-FUTURE", $"Service line {line.Lx.E(1)} date is after the transaction creation date (BHT04).", dtp472, 3, elemCode: "8", dataRef: "1251", claimId: id);
                }

                if (!_institutional && line.Service?.E(7) is { } pointerRaw)
                {
                    var pointers = Components(pointerRaw);
                    for (var p = 0; p < pointers.Length; p++)
                    {
                        if (pointers[p].Length == 0) continue;
                        if (!int.TryParse(pointers[p], NumberStyles.None, CultureInfo.InvariantCulture, out var pointer) || pointer < 1 || pointer > dxCount)
                            Report(L4, "L4-SV107-POINTER", $"SV107 diagnosis pointer {pointers[p]} on line {line.Lx.E(1)} does not point to a diagnosis on the claim ({dxCount} present).",
                                line.Service, 7, p + 1, elemCode: "7", dataRef: "1328", badValue: pointers[p], claimId: id);
                    }
                }
            }
        }

        // ── Level 5: external code sets ──────────────────────────────────

        private void CheckCodeSets()
        {
            const SnipLevel L5 = SnipLevel.ExternalCodeSets;
            foreach (var claim in _hls.SelectMany(h => h.Claims))
            {
                var id = claim.ClaimId;
                var claimDate = ClaimServiceDate(claim);
                var clm05 = Components(claim.Clm.E(5));

                if (clm05.Length > 0 && clm05[0].Length > 0)
                {
                    if (_institutional)
                    {
                        if (clm05[0].Length != 2 || !clm05[0].All(char.IsAsciiDigit))
                            Report(L5, "L5-TOB", "CLM05-1 facility type code must be two digits (the first two of the type of bill).", claim.Clm, 5, 1, elemCode: "7", dataRef: "1331", badValue: clm05[0], claimId: id);
                    }
                    else
                    {
                        CheckPos(clm05[0], claim.Clm, 5, 1, id, claimDate);
                    }
                }

                foreach (var hi in claim.Own.Where(s => s.Id == "HI"))
                {
                    for (var e = 1; e <= hi.S.Elements.Count; e++)
                    {
                        var parts = Components(hi.E(e));
                        if (parts.Length < 2 || parts[1].Length == 0) continue;
                        var (qualifier, code) = (parts[0], parts[1]);

                        if (Icd9CmQualifiers.Contains(qualifier) || qualifier is "BR" or "BQ")
                        {
                            if (claimDate is null || claimDate >= Icd10Cutover)
                                Report(L5, "L5-ICD9", $"ICD-9 qualifier {qualifier} is not valid for dates of service on or after 2015-10-01; use ICD-10.", hi, e, 1, elemCode: "7", dataRef: "1270", badValue: qualifier, claimId: id);
                            continue;
                        }

                        if (Icd10CmQualifiers.Contains(qualifier))
                            CheckCode(SnipCodeSystems.Icd10Cm, code, SnipCodeSets.IsIcd10CmFormat(code), "ICD-10-CM diagnosis", hi, e, id, claimDate,
                                code.Contains('.') ? "ICD-10-CM codes are sent without the decimal point" : null);
                        else if (qualifier is "BBR" or "BBQ")
                            CheckCode(SnipCodeSystems.Icd10Pcs, code, SnipCodeSets.IsIcd10PcsFormat(code), "ICD-10-PCS procedure", hi, e, id, claimDate, null);
                    }
                }

                foreach (var line in claim.Lines)
                {
                    if (line.Service is not { } sv) continue;
                    var lineDate = Period(line.Segs.FirstOrDefault(s => s.Id == "DTP" && s.E(1) == "472")).From ?? claimDate;
                    var procElement = _institutional ? 2 : 1;
                    var proc = Components(sv.E(procElement));

                    if (proc.Length >= 2 && proc[0] == "HC" && proc[1].Length > 0)
                    {
                        var code = proc[1];
                        var isCpt = SnipCodeSets.IsCptFormat(code);
                        var isHcpcs = SnipCodeSets.IsHcpcsFormat(code);
                        CheckCode(isHcpcs ? SnipCodeSystems.Hcpcs : SnipCodeSystems.Cpt, code, isCpt || isHcpcs, "CPT/HCPCS procedure", sv, procElement, id, lineDate, null, component: 2);
                        for (var m = 2; m < proc.Length && m < 6; m++)
                        {
                            if (proc[m].Length > 0 && !SnipCodeSets.IsModifierFormat(proc[m]))
                                Report(L5, "L5-MODIFIER", $"Procedure modifier '{proc[m]}' is not a two-character modifier.", sv, procElement, m + 1, elemCode: "7", dataRef: "1339", badValue: proc[m], claimId: id);
                        }
                    }

                    if (_institutional)
                    {
                        if (sv.E(1) is { } revenue)
                            CheckCode(SnipCodeSystems.RevenueCode, revenue, SnipCodeSets.IsRevenueCodeFormat(revenue), "revenue code", sv, 1, id, lineDate, null, component: null);
                    }
                    else if (sv.E(5) is { } linePos)
                    {
                        CheckPos(linePos, sv, 5, null, id, lineDate);
                    }
                }
            }
        }

        private void CheckPos(string pos, Seg seg, int element, int? component, string? claimId, DateOnly? date)
        {
            var known = _owner._codeSets?.IsValid(SnipCodeSystems.PlaceOfService, pos, date)
                ?? SnipCodeSets.PlaceOfServiceCodes.Contains(pos);
            if (!known)
                Report(SnipLevel.ExternalCodeSets, "L5-POS", $"Place of service '{pos}' is not in the CMS place-of-service code set.", seg, element, component,
                    elemCode: "7", dataRef: "1331", badValue: pos, claimId: claimId);
        }

        private void CheckCode(
            string system, string code, bool formatOk, string label, Seg seg, int element,
            string? claimId, DateOnly? date, string? formatHint, int? component = 2)
        {
            if (!formatOk)
            {
                Report(SnipLevel.ExternalCodeSets, $"L5-{system}-FORMAT",
                    $"'{code}' is not a valid {label} code format{(formatHint is null ? "." : $": {formatHint}.")}",
                    seg, element, component, elemCode: "7", dataRef: "1271", badValue: code, claimId: claimId);
                return;
            }

            if (_owner._codeSets?.IsValid(system, code, date) == false)
            {
                Report(SnipLevel.ExternalCodeSets, $"L5-{system}-UNKNOWN",
                    $"'{code}' is not a valid {label} code{(date is null ? "" : $" on {date:yyyy-MM-dd}")}.",
                    seg, element, component, elemCode: "7", dataRef: "1271", badValue: code, claimId: claimId);
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private List<string> DiagnosisCodes(ClaimNode claim)
        {
            var codes = new List<string>();
            foreach (var hi in claim.Own.Where(s => s.Id == "HI"))
            {
                for (var e = 1; e <= hi.S.Elements.Count; e++)
                {
                    var parts = Components(hi.E(e));
                    if (parts.Length >= 2 && (Icd10CmQualifiers.Contains(parts[0]) || Icd9CmQualifiers.Contains(parts[0]))
                        && parts[0] is not ("ABJ" or "BJ" or "APR" or "PR" or "ABN" or "BN"))
                        codes.Add(parts[1]);
                }
            }
            return codes;
        }

        private DateOnly? ClaimServiceDate(ClaimNode claim)
        {
            var lineDates = claim.Lines
                .Select(l => Period(l.Segs.FirstOrDefault(s => s.Id == "DTP" && s.E(1) == "472")).From)
                .Where(d => d is not null)
                .ToList();
            if (lineDates.Count > 0) return lineDates.Min();
            var statement = Period(claim.Own.FirstOrDefault(s => s.Id == "DTP" && s.E(1) == "434")).From;
            return statement ?? ParseDate(_bhtDate);
        }

        private static (DateOnly? From, DateOnly? To) Period(Seg? dtp)
        {
            if (dtp?.E(3) is not { } value) return (null, null);
            if (dtp.E(2) == "RD8" && value.Split('-') is [var from, var to])
                return (ParseDate(from), ParseDate(to));
            var single = ParseDate(value.Length >= 8 ? value[..8] : value);
            return (single, single);
        }

        private string[] Components(string? element) =>
            element is null ? [] : element.Split(_componentSeparator);

        private void Report(
            SnipLevel level, string rule, string message, Seg? seg,
            int? element = null, int? component = null,
            string? segmentId = null, string? loop = null, int? position = null,
            string segCode = "8", string? elemCode = null, string? dataRef = null,
            string? badValue = null, string? claimId = null, bool warnOnly = false)
        {
            var action = _owner._options.ActionFor(level);
            if (action == SnipAction.Off) return;
            // warnOnly: a rule whose applicability is uncertain (e.g. an
            // unclassified type of bill) may report but never reject.
            if (warnOnly && action == SnipAction.Reject) action = SnipAction.Warn;
            var severity = action == SnipAction.Reject ? SnipSeverity.Error : SnipSeverity.Warning;

            var perSet = _owner._options.MaxFindingsPerTransactionSet;
            if (_outcome.Issues.Count >= Math.Max(perSet, 0) || _budget.Exhausted)
            {
                _suppressed++;
                _suppressedError |= severity == SnipSeverity.Error;
                if (_budget.Exhausted)
                {
                    _result.FindingsTruncated = true;
                    if (!_budget.FileCapReported)
                    {
                        _budget.FileCapReported = true;
                        _result.EnvelopeIssues.Add(TooManyFindings("file"));
                    }
                }
                else if (!_setCapReported)
                {
                    _setCapReported = true;
                    _outcome.Issues.Add(TooManyFindings("transaction set") with { TransactionSetControlNumber = _outcome.ControlNumber });
                }
                return;
            }

            _budget.Used++;
            _outcome.Issues.Add(new SnipIssue
            {
                Level = level,
                Severity = severity,
                RuleId = rule,
                Message = message,
                TransactionSetControlNumber = _outcome.ControlNumber,
                ClaimId = claimId,
                Loop = loop ?? seg?.Loop,
                SegmentId = SafeSegmentId(segmentId ?? seg?.Id),
                SegmentPosition = position ?? seg?.Pos,
                ElementPosition = segmentId is null || segmentId == seg?.Id ? element : null,
                ComponentPosition = segmentId is null || segmentId == seg?.Id ? component : null,
                DataElementReference = dataRef,
                SegmentErrorCode = segCode,
                ElementErrorCode = segmentId is null || segmentId == seg?.Id ? elemCode : null,
                BadValue = badValue,
            });
        }
    }

    // ── Type of bill ─────────────────────────────────────────────────────

    internal enum BillSetting { Inpatient, Outpatient, Unknown }

    // NUBC type of bill, first two digits (facility type + bill
    // classification), as carried in CLM05-1. Inpatient includes hospital
    // inpatient (11, 12), swing beds (18, 28), SNF inpatient (21, 22),
    // religious nonmedical inpatient (41), ICF (65, 66) and residential (86).
    private static readonly HashSet<string> InpatientBillTypes = ["11", "12", "18", "21", "22", "28", "41", "65", "66", "86"];

    // Outpatient: hospital outpatient / other (13, 14), SNF outpatient (23),
    // religious nonmedical outpatient (43), clinics 71–77 and 79 (RHC, ESRD,
    // FQHC, ORF, CORF, CMHC), ASC (83) and CAH (85).
    private static readonly HashSet<string> OutpatientBillTypes = ["13", "14", "23", "43", "71", "72", "73", "74", "75", "76", "77", "79", "83", "85"];

    /// <summary>
    /// Classifies CLM05-1. Anything not in the two tables (home health,
    /// hospice, unusual codes) is <see cref="BillSetting.Unknown"/>, and the
    /// rules that depend on the distinction only warn for it.
    /// </summary>
    internal static BillSetting ClassifyTypeOfBill(string? facilityCode) =>
        facilityCode is null ? BillSetting.Unknown
        : InpatientBillTypes.Contains(facilityCode) ? BillSetting.Inpatient
        : OutpatientBillTypes.Contains(facilityCode) ? BillSetting.Outpatient
        : BillSetting.Unknown;

    // ── Shared helpers ───────────────────────────────────────────────────

    private static bool TryAmount(string? value, out decimal amount) =>
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount);

    private static bool IsDate(string? value, string format) =>
        value is not null && DateOnly.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool IsTime(string value) =>
        value.Length is 4 or 6 or 7 or 8
        && value.All(char.IsAsciiDigit)
        && int.Parse(value[..2], CultureInfo.InvariantCulture) < 24
        && int.Parse(value[2..4], CultureInfo.InvariantCulture) < 60;

    private static bool IsPeriod(string value) =>
        value.Split('-') is [var from, var to]
        && ParseDate(from) is { } f && ParseDate(to) is { } t && f <= t;

    private static DateOnly? ParseDate(string? value) =>
        value is not null && DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    private static bool IsPoBox(string street)
    {
        var normalized = new string(street.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return normalized.StartsWith("POBOX", StringComparison.Ordinal)
               || normalized.StartsWith("POSTOFFICEBOX", StringComparison.Ordinal)
               || normalized.StartsWith("LOCKBOX", StringComparison.Ordinal);
    }

}
