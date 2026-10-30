using System.Globalization;
using System.Text;

namespace CloudHealthOffice.Infrastructure.Edi.Core;

/// <summary>
/// A 999 (005010X231A1) that rejects every transaction set of a real-time
/// request whose envelope was accepted but whose content could not be
/// processed. CORE real-time rules return a 999 only for a rejection; an
/// accepted 270/276 is answered by its 271/277 alone.
///
/// <para>The 999 echoes GS01/GS06/GS08 (AK1) and ST01/ST02/ST03 (AK2) and
/// rejects each set with IK5 R. It carries no IK3/IK4 detail: the parsers
/// CHO uses for real-time requests report a reason, not a segment
/// position.</para>
/// </summary>
public static class X12999Rejection
{
    public const string ImplementationReference = "005010X231A1";

    /// <summary>IK502 / AK905 code 5: one or more segments in error.</summary>
    public const string SegmentsInError = "5";

    /// <summary>
    /// The 999 interchange, or null when the request has no readable ISA and
    /// GS to acknowledge (then the TA1 or envelope error answers it).
    /// </summary>
    public static string? Build(string x12, long controlNumber, DateTime now, string syntaxErrorCode = SegmentsInError)
    {
        if (string.IsNullOrEmpty(x12) || x12.Length < 106 || !x12.StartsWith("ISA", StringComparison.Ordinal))
            return null;

        var el = x12[3];
        var comp = x12[104];
        var term = x12[105];
        var segments = x12.Split(term)
            .Select(s => s.Trim('\r', '\n', ' '))
            .Where(s => s.Length > 0)
            .Select(s => s.Split(el))
            .ToList();

        var isa = segments.FirstOrDefault(s => s[0] == "ISA");
        var gs = segments.FirstOrDefault(s => s[0] == "GS");
        if (isa is null || isa.Length < 17 || gs is null || gs.Length < 9)
            return null;

        var sets = segments.Where(s => s[0] == "ST" && s.Length >= 3).ToList();
        var control = (controlNumber % 1_000_000_000).ToString("D9", CultureInfo.InvariantCulture);
        var groupControl = (controlNumber % 1_000_000_000).ToString(CultureInfo.InvariantCulture);

        var body = new List<string>
        {
            Join(el, "ST", "999", "0001", ImplementationReference),
            Join(el, "AK1", Clean(gs[1], el, comp, term), Clean(gs[6], el, comp, term), Clean(gs[8], el, comp, term)),
        };
        foreach (var st in sets)
        {
            body.Add(Join(el, "AK2", Clean(st[1], el, comp, term), Clean(st[2], el, comp, term), st.Length > 3 ? Clean(st[3], el, comp, term) : ""));
            body.Add(Join(el, "IK5", "R", syntaxErrorCode));
        }
        var count = sets.Count.ToString(CultureInfo.InvariantCulture);
        body.Add(Join(el, "AK9", "R", count, count, "0"));
        body.Add(Join(el, "SE", (body.Count + 1).ToString(CultureInfo.InvariantCulture), "0001"));

        var usage = isa[15] is "P" or "T" ? isa[15] : "P";
        var sb = new StringBuilder();
        // Back to the sender: ISA07/08 of the request become ISA05/06 of the 999.
        sb.Append(Join(el, "ISA", "00", new string(' ', 10), "00", new string(' ', 10),
            Pad(isa[7], 2), Pad(isa[8], 15), Pad(isa[5], 2), Pad(isa[6], 15),
            now.ToString("yyMMdd", CultureInfo.InvariantCulture), now.ToString("HHmm", CultureInfo.InvariantCulture),
            "^", "00501", control, "0", usage, comp.ToString())).Append(term);
        sb.Append(Join(el, "GS", "FA", Clean(gs[3], el, comp, term), Clean(gs[2], el, comp, term),
            now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), now.ToString("HHmm", CultureInfo.InvariantCulture),
            groupControl, "X", ImplementationReference)).Append(term);
        foreach (var segment in body)
            sb.Append(segment).Append(term);
        sb.Append(Join(el, "GE", "1", groupControl)).Append(term);
        sb.Append(Join(el, "IEA", "1", control)).Append(term);
        return sb.ToString();
    }

    private static string Join(char el, params string[] elements)
    {
        var last = elements.Length - 1;
        while (last > 0 && elements[last].Length == 0) last--;
        return string.Join(el, elements, 0, last + 1);
    }

    private static string Clean(string value, char el, char comp, char term) =>
        new string(value.Trim().Where(c => c != el && c != comp && c != term).ToArray());

    private static string Pad(string value, int width)
    {
        var v = value.Trim();
        return v.Length >= width ? v[..width] : v.PadRight(width);
    }
}
