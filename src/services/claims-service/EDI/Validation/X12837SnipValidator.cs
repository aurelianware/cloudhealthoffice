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
///   hierarchy (sequence, parents, child codes), 2010AA/2000B/2010BA/2010BB/2000C
///   required segments, CLM05 composite and CLM06–09, principal diagnosis,
///   diagnosis count, LX numbering and SV1/SV2 presence and qualifiers.</item>
///   <item><b>Balancing.</b> CLM02 = ΣSV102 (837P) / ΣSV203 (837I).</item>
///   <item><b>Situational.</b> DTP*472 on every 837P line and on 837I
///   outpatient lines when the statement covers more than one day; 837I
///   inpatient admission date and CL1; line dates inside the statement
///   period and not after BHT04; NPI check digit; PO Box billing address;
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
            var unreadable = new SnipValidationResult();
            unreadable.EnvelopeIssues.Add(new SnipIssue
            {
                Level = SnipLevel.Syntax,
                RuleId = "L1-ISA-UNREADABLE",
                Message = $"The file is not a readable X12 interchange: {ex.Message}",
                SegmentId = "ISA",
            });
            return unreadable;
        }

        var result = new SnipValidationResult { Document = doc };
        new EnvelopeWalker(this, doc, result).Run();
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Envelope: ISA/IEA, GS/GE, ST/SE
    // ═══════════════════════════════════════════════════════════════════

    private sealed class EnvelopeWalker(X12837SnipValidator owner, X12Document doc, SnipValidationResult result)
    {
        private readonly IReadOnlyList<X12Segment> _segs = doc.Segments;

        public void Run()
        {
            int isaIndex = -1;
            string? isaControl = null;
            var groupsInInterchange = 0;
            SnipFunctionalGroupOutcome? group = null;
            var tsInGroup = 0;
            var stControlNumbers = new HashSet<string>(StringComparer.Ordinal);

            void CloseGroupWithoutTrailer()
            {
                if (group is null) return;
                group.GroupErrorCodes.Add("3"); // functional group trailer missing
                Envelope(SnipLevel.Syntax, "L1-GE-MISSING", $"Functional group {group.ControlNumber} has no GE trailer.", "GE");
                group = null;
            }

            for (var i = 0; i < _segs.Count; i++)
            {
                var seg = _segs[i];
                switch (seg.Id)
                {
                    case "ISA":
                        if (isaIndex >= 0)
                        {
                            CloseGroupWithoutTrailer();
                            Envelope(SnipLevel.Syntax, "L1-IEA-MISSING", $"Interchange {isaControl} has no IEA trailer.", "IEA");
                        }
                        isaIndex = i;
                        isaControl = seg.Element(12);
                        groupsInInterchange = 0;
                        ValidateIsa(seg);
                        if (result.InterchangeControlNumber is null)
                        {
                            result.InterchangeSenderQualifier = seg.Element(4)?.Trim();
                            result.InterchangeSenderId = seg.Element(5)?.Trim();
                            result.InterchangeReceiverQualifier = seg.Element(6)?.Trim();
                            result.InterchangeReceiverId = seg.Element(7)?.Trim();
                            result.InterchangeControlNumber = isaControl;
                        }
                        break;

                    case "IEA":
                        CloseGroupWithoutTrailer();
                        if (seg.Element(1) != isaControl)
                            Envelope(SnipLevel.Syntax, "L1-IEA-CONTROL", $"IEA02 '{seg.Element(1)}' does not match ISA13 '{isaControl}'.", "IEA");
                        if (!int.TryParse(seg.Element(0), NumberStyles.None, CultureInfo.InvariantCulture, out var declaredGroups) || declaredGroups != groupsInInterchange)
                            Envelope(SnipLevel.Syntax, "L1-IEA-COUNT", $"IEA01 '{seg.Element(0)}' does not match the {groupsInInterchange} functional group(s) in the interchange.", "IEA");
                        isaIndex = -1;
                        break;

                    case "GS":
                        if (isaIndex < 0)
                            Envelope(SnipLevel.Syntax, "L1-GS-OUTSIDE-ISA", "GS segment appears outside an ISA/IEA interchange.", "GS");
                        CloseGroupWithoutTrailer();
                        groupsInInterchange++;
                        tsInGroup = 0;
                        stControlNumbers.Clear();
                        group = new SnipFunctionalGroupOutcome
                        {
                            FunctionalIdentifier = seg.Element(0),
                            ApplicationSenderCode = seg.Element(1),
                            ApplicationReceiverCode = seg.Element(2),
                            ControlNumber = seg.Element(5),
                            VersionCode = seg.Element(7),
                        };
                        result.FunctionalGroups.Add(group);
                        if (seg.Element(0) != "HC")
                        {
                            group.GroupErrorCodes.Add("1"); // functional group not supported
                            Envelope(SnipLevel.Syntax, "L1-GS01", $"GS01 must be HC for an 837, found '{seg.Element(0)}'.", "GS");
                        }
                        if (seg.Element(7) is not { } gs08 || (gs08 != Professional && !Institutional.Contains(gs08)))
                        {
                            group.GroupErrorCodes.Add("2"); // functional group version not supported
                            Envelope(SnipLevel.Syntax, "L1-GS08", $"GS08 '{seg.Element(7)}' is not a supported 837 version (005010X222A1, 005010X223A2).", "GS");
                        }
                        if (seg.Element(5) is not { } gs06 || !gs06.All(char.IsAsciiDigit) || gs06.Length > 9)
                        {
                            group.GroupErrorCodes.Add("6"); // group control number violates syntax
                            Envelope(SnipLevel.Syntax, "L1-GS06", "GS06 group control number must be 1-9 digits.", "GS");
                        }
                        if (!IsDate(seg.Element(3), "yyyyMMdd"))
                            Envelope(SnipLevel.Syntax, "L1-GS04", "GS04 is not a valid CCYYMMDD date.", "GS");
                        break;

                    case "GE":
                        if (group is null)
                        {
                            Envelope(SnipLevel.Syntax, "L1-GE-UNMATCHED", "GE segment has no matching GS.", "GE");
                            break;
                        }
                        if (seg.Element(1) != group.ControlNumber)
                        {
                            group.GroupErrorCodes.Add("4");
                            Envelope(SnipLevel.Syntax, "L1-GE-CONTROL", $"GE02 '{seg.Element(1)}' does not match GS06 '{group.ControlNumber}'.", "GE");
                        }
                        if (int.TryParse(seg.Element(0), NumberStyles.None, CultureInfo.InvariantCulture, out var declaredSets))
                            group.DeclaredTransactionSetCount = declaredSets;
                        if (group.DeclaredTransactionSetCount != tsInGroup)
                        {
                            group.GroupErrorCodes.Add("5");
                            Envelope(SnipLevel.Syntax, "L1-GE-COUNT", $"GE01 '{seg.Element(0)}' does not match the {tsInGroup} transaction set(s) in the group.", "GE");
                        }
                        group = null;
                        break;

                    case "ST":
                        if (group is null)
                        {
                            Envelope(SnipLevel.Syntax, "L1-ST-OUTSIDE-GS", "ST segment appears outside a GS/GE functional group.", "ST");
                            group = new SnipFunctionalGroupOutcome();
                            group.GroupErrorCodes.Add("3");
                            result.FunctionalGroups.Add(group);
                        }
                        tsInGroup++;
                        var end = FindTransactionSetEnd(i);
                        var outcome = new SnipTransactionSetOutcome
                        {
                            ControlNumber = seg.Element(1) ?? string.Empty,
                            ImplementationReference = seg.Element(2),
                            InterchangeSegmentIndex = Math.Max(isaIndex, 0),
                            StartSegmentIndex = i,
                            EndSegmentIndex = end,
                        };
                        group.TransactionSets.Add(outcome);
                        if (!stControlNumbers.Add(outcome.ControlNumber))
                            outcome.TransactionSetErrorCodes.Add("23");
                        new TransactionSetValidator(owner, doc, outcome, group, _segs, i, end).Run();
                        i = _segs[end].Id == "SE" ? end : end - 1;
                        break;

                    default:
                        Envelope(SnipLevel.Syntax, "L1-SEGMENT-OUTSIDE-ST", $"Segment {seg.Id} appears outside a transaction set.", seg.Id);
                        break;
                }
            }

            CloseGroupWithoutTrailer();
            if (isaIndex >= 0)
                Envelope(SnipLevel.Syntax, "L1-IEA-MISSING", $"Interchange {isaControl} has no IEA trailer.", "IEA");
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
                Envelope(SnipLevel.Syntax, "L1-ISA-ELEMENTS", $"ISA must have 16 elements, found {isa.Elements.Count}.", "ISA");
                return;
            }
            for (var e = 0; e < widths.Length; e++)
            {
                if (isa.Elements[e].Length != widths[e])
                    Envelope(SnipLevel.Syntax, "L1-ISA-WIDTH", $"ISA{e + 1:00} must be exactly {widths[e]} characters.", "ISA");
            }
            if (!isa.Elements[12].All(char.IsAsciiDigit))
                Envelope(SnipLevel.Syntax, "L1-ISA13", "ISA13 interchange control number must be 9 digits.", "ISA");
            if (isa.Elements[11] != "00501")
                Envelope(SnipLevel.Syntax, "L1-ISA12", $"ISA12 must be 00501 for a 5010 837, found '{isa.Elements[11]}'.", "ISA");
            if (!IsDate(isa.Elements[8], "yyMMdd"))
                Envelope(SnipLevel.Syntax, "L1-ISA09", "ISA09 is not a valid YYMMDD date.", "ISA");
            if (isa.Elements[14] is not ("P" or "T"))
                Envelope(SnipLevel.Syntax, "L1-ISA15", "ISA15 usage indicator must be P or T.", "ISA");
        }

        private void Envelope(SnipLevel level, string rule, string message, string segmentId)
        {
            var action = owner._options.ActionFor(level);
            if (action == SnipAction.Off) return;
            result.EnvelopeIssues.Add(new SnipIssue
            {
                Level = level,
                Severity = action == SnipAction.Reject ? SnipSeverity.Error : SnipSeverity.Warning,
                RuleId = rule,
                Message = message,
                SegmentId = segmentId,
            });
        }
    }

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
        public List<Seg> Segs { get; } = [];
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

        public TransactionSetValidator(
            X12837SnipValidator owner, X12Document doc, SnipTransactionSetOutcome outcome,
            SnipFunctionalGroupOutcome group, IReadOnlyList<X12Segment> segs, int start, int end)
        {
            _owner = owner;
            _componentSeparator = doc.ComponentSeparator;
            _outcome = outcome;
            _segs = segs;
            _start = start;
            _end = end;
            _groupVersion = group.VersionCode;
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
                Report(SnipLevel.Syntax, "L1-SEGMENT-ID", $"Segment id '{Truncate(seg.Id)}' is not used in an 837.", seg, segCode: "1");
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

            var hiSegs = claim.Segs.Where(s => s.Id == "HI").ToList();
            var principal = hiSegs.FirstOrDefault() is { } firstHi ? Components(firstHi.E(1)) : [];
            if (principal.Length < 2 || principal[0] is not ("ABK" or "BK"))
            {
                Report(L2, "L2-HI-PRINCIPAL", "The first HI segment must carry the principal diagnosis (ABK).",
                    hiSegs.FirstOrDefault() ?? clm, hiSegs.Count > 0 ? 1 : null, segmentId: "HI", loop: "2300",
                    segCode: hiSegs.Count > 0 ? "8" : "3", elemCode: hiSegs.Count > 0 ? "7" : null, claimId: id);
            }

            if (!_institutional)
            {
                var dxCount = DiagnosisCodes(claim).Count;
                if (dxCount > 12)
                    Report(L2, "L2-HI-MAX", $"An 837P claim carries at most 12 diagnosis codes; found {dxCount}.", hiSegs[0], segCode: "5", claimId: id);
            }
            else if (!claim.Segs.Any(s => s.Id == "DTP" && s.E(1) == "434"))
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
                }

                if (hl.LevelCode == "22" && hl.Segs.FirstOrDefault(s => s.Id == "SBR") is { } sbr && sbr.E(2) == "18")
                {
                    if (_childLevels.TryGetValue(hl.Id, out var childLevels) && childLevels.Contains("23"))
                        Report(L4, "L4-2000C-SELF", "SBR02 is 18 (subscriber is the patient), so no patient loop (2000C) may follow.", sbr, 2, elemCode: "10", dataRef: "1069");
                    if (!hl.Segs.Any(s => s.Id == "DMG" && s.Loop == "2010BA"))
                        Report(L4, "L4-2010BA-DMG", "Subscriber demographics (2010BA DMG) are required when the subscriber is the patient.",
                            hl.Segs.FirstOrDefault(s => s.Id == "NM1" && s.E(1) == "IL") ?? hl.Hl, segmentId: "DMG", loop: "2010BA", segCode: "I6");
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
                && !claim.Segs.Any(s => s.Id == "REF" && s.E(1) == "F8"))
            {
                Report(L4, "L4-REF-F8", "A replacement or void (CLM05-3 = 7 or 8) requires the payer claim control number (REF*F8).",
                    clm, segmentId: "REF", loop: "2300", segCode: "I6", claimId: id);
            }

            var statement = claim.Segs.FirstOrDefault(s => s.Id == "DTP" && s.E(1) == "434");
            var (statementFrom, statementTo) = Period(statement);

            if (_institutional && clm05.Length >= 1 && clm05[0].Length == 2)
            {
                var inpatient = clm05[0][1] == '1';
                if (inpatient)
                {
                    if (!claim.Segs.Any(s => s.Id == "DTP" && s.E(1) == "435"))
                        Report(L4, "L4-DTP435", "An inpatient claim (type of bill x1x) requires the admission date (DTP*435).", clm, segmentId: "DTP", loop: "2300", segCode: "I6", claimId: id);
                    if (!claim.Segs.Any(s => s.Id == "CL1"))
                        Report(L4, "L4-CL1", "An inpatient claim (type of bill x1x) requires institutional claim codes (CL1).", clm, segmentId: "CL1", loop: "2300", segCode: "I6", claimId: id);
                }
            }

            var dxCount = DiagnosisCodes(claim).Count;
            var bhtDate = ParseDate(_bhtDate);
            var multiDayStatement = statementFrom is not null && statementTo is not null && statementTo > statementFrom;
            var outpatient = _institutional && clm05.Length >= 1 && clm05[0].Length == 2 && clm05[0][1] != '1';

            foreach (var line in claim.Lines)
            {
                var dtp472 = line.Segs.FirstOrDefault(s => s.Id == "DTP" && s.E(1) == "472");
                if (dtp472 is null)
                {
                    if (!_institutional)
                        Report(L4, "L4-DTP472", $"837P service line {line.Lx.E(1)} requires the service date (DTP*472).", line.Lx, segmentId: "DTP", loop: "2400", segCode: "I6", claimId: id);
                    else if (outpatient && multiDayStatement)
                        Report(L4, "L4-DTP472", $"Outpatient service line {line.Lx.E(1)} requires a service date (DTP*472) because the statement covers more than one day.", line.Lx, segmentId: "DTP", loop: "2400", segCode: "I6", claimId: id);
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

                foreach (var hi in claim.Segs.Where(s => s.Id == "HI"))
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
            foreach (var hi in claim.Segs.Where(s => s.Id == "HI"))
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
            var statement = Period(claim.Segs.FirstOrDefault(s => s.Id == "DTP" && s.E(1) == "434")).From;
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
            string? badValue = null, string? claimId = null)
        {
            var action = _owner._options.ActionFor(level);
            if (action == SnipAction.Off) return;

            _outcome.Issues.Add(new SnipIssue
            {
                Level = level,
                Severity = action == SnipAction.Reject ? SnipSeverity.Error : SnipSeverity.Warning,
                RuleId = rule,
                Message = message,
                TransactionSetControlNumber = _outcome.ControlNumber,
                ClaimId = claimId,
                Loop = loop ?? seg?.Loop,
                SegmentId = segmentId ?? seg?.Id,
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

    private static string Truncate(string value) => value.Length <= 3 ? value : value[..3];
}
