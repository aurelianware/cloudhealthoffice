using System.Globalization;

namespace CloudHealthOffice.Infrastructure.Edi.Interchange;

/// <summary>
/// Reads and validates the ISA/IEA envelopes of a raw X12 file. Pure: no I/O
/// and no duplicate check (that needs a store; see
/// <see cref="X12InterchangeIntake"/>). Every finding carries the TA105 note
/// code it maps to, so the same pass decides the TA1.
///
/// <para>
/// Delimiters are taken from the ISA as X12 defines them: the element
/// separator is the 4th character, ISA16 is the component separator and the
/// character after it is the segment terminator. The ISA is split on the
/// element separator rather than read at fixed offsets, so a field of the
/// wrong width is reported as that field's note code (e.g. a 14-character
/// ISA06 is 006) instead of making every later field unreadable.
/// </para>
/// </summary>
public static class X12EnvelopeReader
{
    /// <summary>ISA01–ISA16 fixed widths.</summary>
    internal static readonly int[] IsaWidths = [2, 10, 2, 10, 2, 15, 2, 15, 6, 4, 1, 5, 9, 1, 1, 1];

    private static readonly HashSet<string> AuthorizationQualifiers = ["00", "01", "02", "03", "04", "05", "06"];
    private static readonly HashSet<string> SecurityQualifiers = ["00", "01"];

    /// <summary>Interchange ID qualifiers (X12 I05).</summary>
    private static readonly HashSet<string> IdQualifiers =
    [
        "01", "02", "03", "04", "07", "08", "09", "10", "11", "12", "13", "14", "15", "16", "17", "18", "19", "20",
        "21", "22", "23", "24", "25", "26", "27", "28", "29", "30", "31", "32", "33", "34", "35", "36", "37", "38",
        "AM", "NR", "SA", "SN", "ZZ",
    ];

    /// <summary>How far past the ISA tag the 16 element separators are looked for (a valid ISA is 106 characters).</summary>
    private const int IsaSearchWindow = 300;

    public static X12EnvelopeReadResult Read(string? content, X12EnvelopeValidationOptions? options = null)
    {
        options ??= new X12EnvelopeValidationOptions();
        if (string.IsNullOrWhiteSpace(content))
            return new X12EnvelopeReadResult { Interchanges = [], UnreadableReason = "The file is empty." };

        var pos = SkipWhitespace(content, 0);
        // At the start of the file anything after "ISA" is taken as the element
        // separator, so a bad one is reported (026) instead of "not X12".
        if (!(pos + 3 < content.Length && string.CompareOrdinal(content, pos, "ISA", 0, 3) == 0))
            return new X12EnvelopeReadResult { Interchanges = [], UnreadableReason = "The file does not start with an ISA segment." };

        var interchanges = new List<X12InterchangeEnvelope>();
        while (pos < content.Length)
        {
            var env = ReadOne(content, ref pos, options);
            interchanges.Add(env);
            pos = SkipWhitespace(content, pos);
            if (pos >= content.Length) break;
            if (!IsIsaAt(content, pos))
            {
                env.Add(Ta1NoteCodes.InvalidControlStructure, "Content follows the IEA that is not another interchange.");
                break;
            }
        }

        return new X12EnvelopeReadResult { Interchanges = interchanges };
    }

    private static X12InterchangeEnvelope ReadOne(string content, ref int pos, X12EnvelopeValidationOptions options)
    {
        var start = pos;
        var elementSeparator = start + 3 < content.Length ? content[start + 3] : '\0';

        // The 16 element separators that precede ISA01..ISA16.
        var seps = new List<int>(16);
        var limit = Math.Min(content.Length, start + IsaSearchWindow);
        for (var i = start + 3; i < limit && seps.Count < 16; i++)
            if (content[i] == elementSeparator) seps.Add(i);

        if (elementSeparator == '\0' || seps.Count < 16 || seps[15] + 2 >= content.Length)
        {
            var truncated = seps.Count < 16 ? limit >= content.Length : seps[15] + 2 >= content.Length;
            var env = new X12InterchangeEnvelope { Start = start, RawText = content[start..] };
            env.Add(truncated ? Ta1NoteCodes.PrematureEndOfFile : Ta1NoteCodes.InvalidControlStructure,
                "The ISA segment does not have 16 elements.");
            pos = content.Length;
            return env;
        }

        var elements = new string[16];
        for (var i = 0; i < 15; i++)
            elements[i] = content[(seps[i] + 1)..seps[i + 1]];
        elements[15] = content[seps[15] + 1].ToString();
        var componentSeparator = content[seps[15] + 1];
        var terminator = content[seps[15] + 2];

        var header = new X12InterchangeHeader
        {
            Elements = elements,
            ElementSeparator = elementSeparator,
            ComponentSeparator = componentSeparator,
            SegmentTerminator = terminator,
        };
        var envelope = new X12InterchangeEnvelope { Start = start, Header = header };

        ValidateHeader(envelope, header, options);
        pos = ScanBody(content, seps[15] + 3, envelope, header);
        ValidateTrailer(envelope, header);

        envelope.RawText = content[start..Math.Min(pos, content.Length)];
        return envelope;
    }

    // ── ISA ────────────────────────────────────────────────────────────

    private static void ValidateHeader(X12InterchangeEnvelope env, X12InterchangeHeader h, X12EnvelopeValidationOptions options)
    {
        // Separators first: when they are wrong nothing after them is reliable.
        if (!IsDelimiter(h.ElementSeparator))
            env.Add(Ta1NoteCodes.InvalidElementSeparator, "The data element separator (ISA position 4) must be a non-alphanumeric character.");
        if (!IsDelimiter(h.SegmentTerminator) && h.SegmentTerminator is not ('\n' or '\r')
            || h.SegmentTerminator == h.ElementSeparator || h.SegmentTerminator == h.ComponentSeparator)
            env.Add(Ta1NoteCodes.InvalidSegmentTerminator, "The segment terminator must be a non-alphanumeric character distinct from the element and component separators.");
        if (!IsDelimiter(h.ComponentSeparator) || h.ComponentSeparator == h.ElementSeparator || h.ComponentSeparator == h.SegmentTerminator)
            env.Add(Ta1NoteCodes.InvalidComponentSeparator, "ISA16 component element separator must be a non-alphanumeric character distinct from the element separator and segment terminator.");

        bool Width(int n) => h.E(n).Length == IsaWidths[n - 1];

        if (!Width(1) || !AuthorizationQualifiers.Contains(h.E(1)))
            env.Add(Ta1NoteCodes.InvalidAuthorizationQualifier, "ISA01 authorization information qualifier is invalid.");
        if (!Width(2) || (h.E(1) == "00" && !string.IsNullOrWhiteSpace(h.E(2))))
            env.Add(Ta1NoteCodes.InvalidAuthorizationValue, "ISA02 authorization information must be 10 characters, blank when ISA01 is 00.");
        if (!Width(3) || !SecurityQualifiers.Contains(h.E(3)))
            env.Add(Ta1NoteCodes.InvalidSecurityQualifier, "ISA03 security information qualifier is invalid.");
        if (!Width(4) || (h.E(3) == "00" && !string.IsNullOrWhiteSpace(h.E(4))))
            env.Add(Ta1NoteCodes.InvalidSecurityValue, "ISA04 security information must be 10 characters, blank when ISA03 is 00.");
        if (!Width(5) || !IdQualifiers.Contains(h.E(5)))
            env.Add(Ta1NoteCodes.InvalidSenderQualifier, "ISA05 sender ID qualifier is invalid.");
        if (!Width(6) || string.IsNullOrWhiteSpace(h.E(6)))
            env.Add(Ta1NoteCodes.InvalidSenderId, "ISA06 sender ID must be 15 characters and not blank.");
        if (!Width(7) || !IdQualifiers.Contains(h.E(7)))
            env.Add(Ta1NoteCodes.InvalidReceiverQualifier, "ISA07 receiver ID qualifier is invalid.");
        if (!Width(8) || string.IsNullOrWhiteSpace(h.E(8)))
            env.Add(Ta1NoteCodes.InvalidReceiverId, "ISA08 receiver ID must be 15 characters and not blank.");
        else if (options.KnownReceiverIds.Count > 0 &&
                 !options.KnownReceiverIds.Contains(h.ReceiverId, StringComparer.OrdinalIgnoreCase))
            env.Add(Ta1NoteCodes.UnknownReceiverId, "ISA08 receiver ID is not one this system receives for.");
        if (!Width(9) || !DateTime.TryParseExact(h.E(9), "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            env.Add(Ta1NoteCodes.InvalidDate, "ISA09 interchange date must be a valid YYMMDD date.");
        if (!Width(10) || !IsHhmm(h.E(10)))
            env.Add(Ta1NoteCodes.InvalidTime, "ISA10 interchange time must be a valid HHMM time.");

        var versionDigits = Width(12) && h.E(12).All(char.IsAsciiDigit);
        if (!Width(11))
            env.Add(Ta1NoteCodes.InvalidStandardsIdentifier, "ISA11 must be exactly 1 character.");
        else if (versionDigits && string.CompareOrdinal(h.E(12), "00402") < 0)
        {
            // Through 00401, ISA11 is the Interchange Control Standards Identifier.
            if (h.E(11) != "U")
                env.Add(Ta1NoteCodes.StandardNotSupported, "ISA11 control standards identifier must be U for this version.");
        }
        else
        {
            // From 00402 on, ISA11 is the repetition separator.
            var rep = h.E(11)[0];
            if (!IsDelimiter(rep) || rep == h.ElementSeparator || rep == h.ComponentSeparator || rep == h.SegmentTerminator)
                env.Add(Ta1NoteCodes.InvalidStandardsIdentifier, "ISA11 repetition separator must be a non-alphanumeric character distinct from the other delimiters.");
        }

        if (!versionDigits)
            env.Add(Ta1NoteCodes.InvalidVersionId, "ISA12 interchange control version must be 5 digits.");
        else if (!options.SupportedVersions.Contains(h.E(12)))
            env.Add(Ta1NoteCodes.VersionNotSupported, $"ISA12 version {h.E(12)} is not supported.");

        if (!Width(13) || !h.E(13).All(char.IsAsciiDigit))
            env.Add(Ta1NoteCodes.InvalidControlNumber, "ISA13 interchange control number must be 9 digits.");
        if (h.E(14) is not ("0" or "1"))
            env.Add(Ta1NoteCodes.InvalidAckRequested, "ISA14 acknowledgment requested must be 0 or 1.");
        if (h.E(15) is not ("P" or "T"))
            env.Add(Ta1NoteCodes.InvalidTestIndicator, "ISA15 usage indicator must be P or T.");
    }

    // ── Body: GS/GE balance, contents, IEA ─────────────────────────────

    /// <returns>The offset just past where this interchange ends.</returns>
    private static int ScanBody(string content, int pos, X12InterchangeEnvelope env, X12InterchangeHeader h)
    {
        var groupOpen = false;
        var reportedOutside = false;

        while (pos < content.Length)
        {
            var t = content.IndexOf(h.SegmentTerminator, pos);
            var end = t < 0 ? content.Length : t;
            var raw = content[pos..end];
            var lead = raw.Length - raw.TrimStart().Length;
            var seg = raw.Trim();

            if (seg.Length == 0)
            {
                if (t < 0) { pos = content.Length; break; }
                pos = t + 1;
                continue;
            }

            if (IsIsaAt(seg, 0))
            {
                // A new interchange starts before this one's IEA.
                env.Add(Ta1NoteCodes.PrematureEndOfFile, "A new ISA begins before this interchange's IEA.");
                return pos + lead;
            }

            var parts = seg.Split(h.ElementSeparator);
            switch (parts[0])
            {
                case "GS":
                    if (groupOpen)
                        env.Add(Ta1NoteCodes.InvalidControlStructure, "A GS segment begins before the previous group's GE.");
                    groupOpen = true;
                    env.GroupCount++;
                    if (parts.Length < 9 || parts.Skip(1).Take(8).Any(string.IsNullOrWhiteSpace))
                        env.Add(Ta1NoteCodes.InvalidInterchangeContent, "A GS segment is missing required elements (GS01–GS08).");
                    else
                        env.FunctionalIdentifierCodes.Add(parts[1]);
                    break;
                case "GE":
                    if (!groupOpen)
                        env.Add(Ta1NoteCodes.InvalidControlStructure, "A GE segment has no matching GS.");
                    groupOpen = false;
                    break;
                case "TA1":
                    env.HasTa1Segments = true;
                    if (groupOpen)
                        env.Add(Ta1NoteCodes.InvalidControlStructure, "A TA1 segment appears inside a functional group.");
                    break;
                case "IEA":
                    if (groupOpen)
                        env.Add(Ta1NoteCodes.InvalidControlStructure, "The IEA arrives before the last group's GE.");
                    env.HasTrailer = true;
                    env.DeclaredGroupCount = parts.Length > 1 ? parts[1] : null;
                    env.TrailerControlNumber = parts.Length > 2 ? parts[2] : null;
                    return t < 0 ? content.Length : t + 1;
                default:
                    if (parts[0] == "ST" && parts.Length > 1)
                        env.TransactionSetIds.Add(parts[1]);
                    if (!groupOpen && !reportedOutside)
                    {
                        reportedOutside = true;
                        env.Add(Ta1NoteCodes.InvalidControlStructure, $"Segment {Truncate(parts[0])} appears outside a functional group.");
                    }
                    break;
            }

            if (t < 0) { pos = content.Length; break; }
            pos = t + 1;
        }

        if (groupOpen)
            env.Add(Ta1NoteCodes.PrematureEndOfFile, "The file ends inside a functional group (no GE).");
        return pos;
    }

    private static void ValidateTrailer(X12InterchangeEnvelope env, X12InterchangeHeader h)
    {
        if (!env.HasTrailer)
        {
            env.Add(Ta1NoteCodes.PrematureEndOfFile, "The interchange has no IEA trailer.");
            return;
        }

        if (!string.Equals(env.TrailerControlNumber, h.ControlNumber, StringComparison.Ordinal))
            env.Add(Ta1NoteCodes.ControlNumberMismatch, "IEA02 does not match ISA13.");

        if (env.DeclaredGroupCount is null || env.DeclaredGroupCount.Length == 0 ||
            !env.DeclaredGroupCount.All(char.IsAsciiDigit) ||
            !int.TryParse(env.DeclaredGroupCount, NumberStyles.None, CultureInfo.InvariantCulture, out var declared) ||
            declared != env.GroupCount)
            env.Add(Ta1NoteCodes.InvalidGroupCount, $"IEA01 does not equal the number of functional groups ({env.GroupCount}).");

        if (env.GroupCount == 0 && !env.HasTa1Segments)
            env.Add(Ta1NoteCodes.InvalidInterchangeContent, "The interchange contains no functional groups.");
    }

    // ── helpers ────────────────────────────────────────────────────────

    private static bool IsIsaAt(string s, int i) =>
        i + 3 < s.Length && s[i] == 'I' && s[i + 1] == 'S' && s[i + 2] == 'A' && !char.IsLetterOrDigit(s[i + 3]) && !char.IsWhiteSpace(s[i + 3]);

    private static int SkipWhitespace(string s, int i)
    {
        while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == '﻿')) i++;
        return i;
    }

    private static bool IsDelimiter(char c) => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && !char.IsControl(c);

    private static bool IsHhmm(string s) =>
        s.Length == 4 && s.All(char.IsAsciiDigit) &&
        int.Parse(s[..2], CultureInfo.InvariantCulture) < 24 && int.Parse(s[2..], CultureInfo.InvariantCulture) < 60;

    private static string Truncate(string s) => s.Length <= 3 ? s : s[..3];
}

/// <summary>Envelope rules a deployment can tune (config section <c>X12Interchange</c>).</summary>
public sealed class X12EnvelopeValidationOptions
{
    /// <summary>ISA12 versions accepted. Others get TA105 003.</summary>
    public IReadOnlyCollection<string> SupportedVersions { get; init; } = ["00401", "00501"];

    /// <summary>
    /// ISA08 receiver IDs this system receives for. Empty (default) skips the
    /// check; otherwise any other ISA08 gets TA105 009.
    /// </summary>
    public IReadOnlyCollection<string> KnownReceiverIds { get; init; } = [];
}
