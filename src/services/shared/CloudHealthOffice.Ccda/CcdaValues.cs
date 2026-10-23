using System.Globalization;
using System.Xml.Linq;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.Ccda;

/// <summary>
/// Reads the CDA datatypes that appear in every section — <c>CD</c>/<c>CE</c>
/// codes, <c>IVL_TS</c> intervals, <c>TS</c> points in time — into their FHIR
/// equivalents.
///
/// Shared rather than repeated per section because the awkward cases (a
/// nullFlavor, a translation that is better coded than the primary, a date with
/// only a year) are the same awkward cases everywhere, and a second
/// implementation of them would diverge.
/// </summary>
public static class CcdaValues
{
    private static readonly XNamespace Cda = CcdaTemplates.CdaNamespace;
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>
    /// A CDA coded element as a FHIR <see cref="CodeableConcept"/>, or null
    /// where it carries nothing usable.
    ///
    /// The primary code and every <c>translation</c> are kept as separate
    /// codings of one concept, which is what they are. Senders differ on which
    /// one they put first — some lead with a local code and translate to
    /// SNOMED, some the reverse — so dropping the translations would discard
    /// the standard coding about as often as the local one.
    /// </summary>
    public static CodeableConcept? Code(XElement? element)
    {
        if (element is null) return null;

        // nullFlavor is the sender saying "this is absent, and here is why".
        // Reading the element's other attributes anyway would turn a declared
        // absence into a code.
        var nullFlavor = element.Attribute("nullFlavor")?.Value;

        var concept = new CodeableConcept();

        if (nullFlavor is null)
        {
            var primary = CodingOf(element);
            if (primary is not null) concept.Coding.Add(primary);
        }

        foreach (var translation in element.Elements(Cda + "translation"))
        {
            var coding = CodingOf(translation);
            if (coding is not null) concept.Coding.Add(coding);
        }

        // originalText is the narrative the sender pointed at. Kept as the
        // concept's text so a code CHO cannot resolve still reads as something.
        concept.Text = Trimmed(element.Element(Cda + "originalText")?.Value)
            ?? Trimmed(element.Attribute("displayName")?.Value);

        return concept.Coding.Count == 0 && string.IsNullOrEmpty(concept.Text) ? null : concept;
    }

    private static Coding? CodingOf(XElement element)
    {
        var code = Trimmed(element.Attribute("code")?.Value);
        if (code is null) return null;

        return new Coding
        {
            System = CcdaCodeSystems.SystemFor(element.Attribute("codeSystem")?.Value),
            Code = code,
            Display = Trimmed(element.Attribute("displayName")?.Value),
        };
    }

    /// <summary>
    /// The low bound of an <c>IVL_TS</c>, or the value of a <c>TS</c>.
    /// </summary>
    public static string? Low(XElement? interval)
        => interval is null
            ? null
            : Timestamp(interval.Element(Cda + "low")?.Attribute("value")?.Value
                        ?? interval.Attribute("value")?.Value);

    /// <summary>The high bound of an <c>IVL_TS</c>; null for an open interval.</summary>
    public static string? High(XElement? interval)
        => interval is null ? null : Timestamp(interval.Element(Cda + "high")?.Attribute("value")?.Value);

    /// <summary>
    /// An HL7 <c>TS</c> as a FHIR date or dateTime, keeping the precision the
    /// sender gave.
    ///
    /// Precision is the whole point. "2024" means the year, and widening it to
    /// 2024-01-01T00:00:00 would assert a day and a second nobody recorded —
    /// which, on an onset date a reviewer uses to decide whether a condition
    /// predates coverage, is a fabricated fact rather than a formatting choice.
    /// </summary>
    public static string? Timestamp(string? value)
    {
        var digits = Trimmed(value);
        if (digits is null) return null;

        // Offsets travel with the instant; a date alone has none to apply.
        var offset = string.Empty;
        var signIndex = digits.IndexOfAny(['+', '-']);
        if (signIndex > 0)
        {
            var raw = digits[signIndex..];
            digits = digits[..signIndex];
            if (raw.Length == 5)
                offset = $"{raw[..3]}:{raw[3..]}";
        }

        if (!digits.All(char.IsAsciiDigit)) return null;

        return digits.Length switch
        {
            >= 14 => $"{Date(digits)}T{digits.Substring(8, 2)}:{digits.Substring(10, 2)}:{digits.Substring(12, 2)}"
                     + (offset.Length > 0 ? offset : "Z"),
            >= 12 => $"{Date(digits)}T{digits.Substring(8, 2)}:{digits.Substring(10, 2)}:00"
                     + (offset.Length > 0 ? offset : "Z"),
            >= 8 => Date(digits),
            >= 6 => $"{digits[..4]}-{digits.Substring(4, 2)}",
            >= 4 => digits[..4],
            // Fewer than four digits is not a year. Returning null keeps a
            // malformed date out rather than letting "20" become the year 20.
            _ => null,
        };

        static string Date(string d) => $"{d[..4]}-{d.Substring(4, 2)}-{d.Substring(6, 2)}";
    }

    /// <summary>
    /// A CDA <c>PQ</c> as a FHIR <see cref="Quantity"/>. A unit CHO cannot read
    /// is kept as the unit string without a system, rather than dropped: "2
    /// tablets" is useful to a reviewer even though UCUM does not define a
    /// tablet the way the sender meant it.
    /// </summary>
    public static Quantity? Quantity(XElement? element)
    {
        if (element is null) return null;
        var raw = Trimmed(element.Attribute("value")?.Value);
        if (raw is null || !decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;

        var unit = Trimmed(element.Attribute("unit")?.Value);
        return new Quantity
        {
            Value = value,
            Unit = unit,
            // UCUM is what CDA's PQ unit is defined against, so a present unit
            // is asserted as UCUM rather than left systemless.
            System = unit is null ? null : "http://unitsofmeasure.org",
            Code = unit,
        };
    }

    /// <summary>
    /// An element's <c>xsi:type</c>, which is how a CDA <c>value</c> says which
    /// datatype it is. A mapper that assumed one type would misread the others.
    /// </summary>
    public static string? XsiType(XElement? element)
        => Trimmed(element?.Attribute(Xsi + "type")?.Value);

    public static string? Trimmed(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
