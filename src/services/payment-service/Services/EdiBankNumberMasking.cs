using System.Text;

namespace PaymentService.Services;

/// <summary>
/// Masks bank numbers in an X12 835 before it leaves the service in a
/// response. The BPR segment carries the payer's and the payee's depository
/// financial institution (routing) number and account number; a response shows
/// only the last 4 digits of each, as provider-service's bank-account reads do.
/// A number is recognised by the qualifier in the element before it (DFI id
/// qualifier 01/04 at BPR06 or later; account qualifier DA/SG), so both the
/// X12 BPR layout and the layout the generators emit are covered. Stored
/// envelopes are not changed.
/// </summary>
public static class EdiBankNumberMasking
{
    private static readonly HashSet<string> DfiQualifiers = new(StringComparer.Ordinal) { "01", "04" };
    private static readonly HashSet<string> AccountQualifiers = new(StringComparer.Ordinal) { "DA", "SG" };

    public static string MaskBpr(string? edi)
    {
        if (string.IsNullOrEmpty(edi))
            return edi ?? string.Empty;

        var (elementSeparator, segmentTerminator) = Separators(edi);
        var segments = edi.Split(segmentTerminator);
        var changed = false;

        for (var s = 0; s < segments.Length; s++)
        {
            var segment = segments[s];
            var leading = segment.Length - segment.TrimStart().Length;
            var elements = segment.TrimStart().Split(elementSeparator);
            if (elements.Length == 0 || elements[0] != "BPR")
                continue;

            for (var i = 7; i < elements.Length; i++)
            {
                var qualifier = elements[i - 1];
                if (DfiQualifiers.Contains(qualifier) || AccountQualifiers.Contains(qualifier))
                {
                    var masked = Mask(elements[i]);
                    if (masked != elements[i])
                    {
                        elements[i] = masked;
                        changed = true;
                    }
                }
            }

            segments[s] = segment[..leading] + string.Join(elementSeparator, elements);
        }

        return changed ? string.Join(segmentTerminator, segments) : edi;
    }

    /// <summary>All but the last 4 characters replaced with X; 4 or fewer are fully masked.</summary>
    internal static string Mask(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        if (value.Length <= 4)
            return new string('X', value.Length);
        var sb = new StringBuilder(value.Length);
        sb.Append('X', value.Length - 4);
        sb.Append(value, value.Length - 4, 4);
        return sb.ToString();
    }

    /// <summary>Element separator from ISA position 3 and the segment terminator after ISA16; '*' and '~' otherwise.</summary>
    private static (char Element, char Segment) Separators(string edi)
    {
        if (!edi.StartsWith("ISA", StringComparison.Ordinal) || edi.Length < 4)
            return ('*', '~');

        var element = edi[3];
        var seen = 0;
        for (var i = 3; i < edi.Length; i++)
        {
            if (edi[i] != element) continue;
            if (++seen == 16)
                return i + 2 < edi.Length ? (element, edi[i + 2]) : (element, '~');
        }
        return (element, '~');
    }
}
