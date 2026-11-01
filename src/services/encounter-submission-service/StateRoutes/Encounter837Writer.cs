using System.Globalization;
using System.Text;

namespace EncounterSubmissionService.StateRoutes;

/// <summary>
/// Interchange identities and control numbers for one staged 837 file.
/// </summary>
public sealed record Encounter837Envelope
{
    public required string SenderIdQualifier { get; init; }
    public required string SenderId { get; init; }
    public required string ReceiverIdQualifier { get; init; }
    public required string ReceiverId { get; init; }
    public required string SubmitterName { get; init; }
    public string? ContactName { get; init; }
    public string? ContactPhone { get; init; }
    public required string ReceiverName { get; init; }
    public required string DestinationPayerName { get; init; }
    public required string DestinationPayerId { get; init; }
    public required string DestinationPayerResponsibility { get; init; }
    public required string DestinationClaimFilingIndicator { get; init; }
    public required string PlanName { get; init; }
    public required string PlanPayerId { get; init; }
    public required string PlanPayerResponsibility { get; init; }
    public required string PlanClaimFilingIndicator { get; init; }
    public required string UsageIndicator { get; init; }

    /// <summary>ISA13, 1–999999999. Also used as GS06.</summary>
    public required long InterchangeControlNumber { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
}

/// <summary>Where one encounter landed in the interchange, so responses can be matched back.</summary>
public sealed record Encounter837Placement(
    string SubmissionId,
    string ClaimId,
    string PatientControlNumber,
    string InterchangeControlNumber,
    string GroupControlNumber,
    string TransactionSetControlNumber);

public sealed record Encounter837File(
    EncounterFormType Form,
    string Content,
    IReadOnlyList<Encounter837Placement> Placements);

/// <summary>
/// Writes ASC X12 005010 837P (X222A1) and 837I (X223A2) encounter interchanges.
///
/// <para>Encounter shape (the pattern state Medicaid companion guides share):
/// BHT06 = RP (reporting); the state is the destination payer in 2010BB; the
/// plan's own adjudication is reported as the other payer in 2320 / 2330B with
/// its paid amount, and per line in 2430 SVD / CAS / DTP*573. Each encounter is
/// its own ST/SE so a 999 IK2 or a 277CA points at exactly one record.</para>
///
/// <para>State-specific segments (plan codes, provider Medicaid IDs, extra REFs)
/// are added only where a companion guide calls for them; the generic shape here
/// is what both TMHP and DHCS start from.</para>
/// </summary>
public static class Encounter837Writer
{
    public const string ProfessionalVersion = "005010X222A1";
    public const string InstitutionalVersion = "005010X223A2";

    private const char Element = '*';
    private const char Component = ':';
    private const char Repetition = '^';
    private const char Terminator = '~';

    public static Encounter837File Write(
        EncounterFormType form, IReadOnlyList<EncounterClaim> claims, Encounter837Envelope envelope)
    {
        if (form == EncounterFormType.Dental)
            throw new NotSupportedException("837D encounters are not built yet.");
        if (claims.Count == 0)
            throw new ArgumentException("At least one encounter is required.", nameof(claims));
        if (claims.Any(c => c.Form != form))
            throw new ArgumentException("Every encounter in a file must have the same form type.", nameof(claims));
        if (envelope.InterchangeControlNumber is < 1 or > 999_999_999)
            throw new ArgumentOutOfRangeException(nameof(envelope), "ISA13 must be 1–999999999.");

        var version = form == EncounterFormType.Institutional ? InstitutionalVersion : ProfessionalVersion;
        var now = envelope.CreatedAtUtc;
        var isa13 = envelope.InterchangeControlNumber.ToString("D9", CultureInfo.InvariantCulture);
        var gs06 = envelope.InterchangeControlNumber.ToString(CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        void Seg(params string?[] elements) => sb.Append(Join(elements)).Append(Terminator);

        sb.Append("ISA*00*          *00*          *")
          .Append(Fixed(envelope.SenderIdQualifier, 2)).Append(Element)
          .Append(Fixed(envelope.SenderId, 15)).Append(Element)
          .Append(Fixed(envelope.ReceiverIdQualifier, 2)).Append(Element)
          .Append(Fixed(envelope.ReceiverId, 15)).Append(Element)
          .Append(now.ToString("yyMMdd", CultureInfo.InvariantCulture)).Append(Element)
          .Append(now.ToString("HHmm", CultureInfo.InvariantCulture)).Append(Element)
          .Append(Repetition).Append("*00501*").Append(isa13)
          .Append("*1*").Append(envelope.UsageIndicator == "P" ? "P" : "T")
          .Append(Element).Append(Component).Append(Terminator);

        Seg("GS", "HC", envelope.SenderId, envelope.ReceiverId,
            now.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            now.ToString("HHmm", CultureInfo.InvariantCulture), gs06, "X", version);

        var placements = new List<Encounter837Placement>(claims.Count);
        var st = 0;
        foreach (var claim in claims)
        {
            st++;
            var st02 = st.ToString("D4", CultureInfo.InvariantCulture);
            var body = new List<string>();
            WriteTransaction(body, claim, envelope, version, st02, now);
            foreach (var s in body) sb.Append(s).Append(Terminator);
            placements.Add(new Encounter837Placement(
                claim.SubmissionId, claim.ClaimId, claim.PatientControlNumber, isa13, gs06, st02));
        }

        Seg("GE", st.ToString(CultureInfo.InvariantCulture), gs06);
        Seg("IEA", "1", isa13);

        return new Encounter837File(form, sb.ToString(), placements);
    }

    private static void WriteTransaction(
        List<string> seg, EncounterClaim c, Encounter837Envelope env, string version, string st02, DateTime now)
    {
        var institutional = c.Form == EncounterFormType.Institutional;
        void Add(params string?[] e) => seg.Add(Join(e));

        Add("ST", "837", st02, version);
        Add("BHT", "0019", "00", Clean(c.PatientControlNumber),
            now.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            now.ToString("HHmm", CultureInfo.InvariantCulture), "RP");

        // 1000A submitter, 1000B receiver
        Add("NM1", "41", "2", Clean(env.SubmitterName), "", "", "", "", "46", Clean(env.SenderId));
        if (!string.IsNullOrWhiteSpace(env.ContactPhone))
            Add("PER", "IC", Clean(env.ContactName ?? env.SubmitterName), "TE", Digits(env.ContactPhone));
        else
            Add("PER", "IC", Clean(env.ContactName ?? env.SubmitterName));
        Add("NM1", "40", "2", Clean(env.ReceiverName), "", "", "", "", "46", Clean(env.ReceiverId));

        // 2000A / 2010AA billing provider
        Add("HL", "1", "", "20", "1");
        if (!string.IsNullOrWhiteSpace(c.BillingProvider.TaxonomyCode))
            Add("PRV", "BI", "PXC", Clean(c.BillingProvider.TaxonomyCode));
        var bp = c.BillingProvider;
        Add("NM1", "85", bp.EntityType == "1" ? "1" : "2", Clean(bp.LastOrOrganizationName),
            bp.EntityType == "1" ? Clean(bp.FirstName) : "", "", "", "", "XX", Clean(bp.Npi));
        Add("N3", Clean(bp.AddressLine));
        Add("N4", Clean(bp.City), Clean(bp.State), Digits(bp.PostalCode));
        Add("REF", "EI", Digits(bp.TaxId));

        // 2000B subscriber (member is the patient: SBR02 = 18, no 2000C)
        var m = c.Member;
        Add("HL", "2", "1", "22", "0");
        Add("SBR", env.DestinationPayerResponsibility, "18", Clean(m.GroupNumber), "", "", "", "", "",
            env.DestinationClaimFilingIndicator);
        Add("NM1", "IL", "1", Clean(m.LastName), Clean(m.FirstName), "", "", "", "MI", Clean(m.MemberId));
        Add("N3", Clean(m.AddressLine));
        Add("N4", Clean(m.City), Clean(m.State), Digits(m.PostalCode));
        Add("DMG", "D8", m.DateOfBirth?.ToString("yyyyMMdd", CultureInfo.InvariantCulture), m.Gender);
        Add("NM1", "PR", "2", Clean(env.DestinationPayerName), "", "", "", "", "PI", Clean(env.DestinationPayerId));

        // 2300 claim
        var clm05 = institutional
            ? $"{c.FacilityTypeCode}{Component}A{Component}{c.FrequencyCode}"
            : $"{c.PlaceOfService}{Component}B{Component}{c.FrequencyCode}";
        if (institutional)
            Add("CLM", Clean(c.PatientControlNumber), Money(c.TotalCharge), "", "", clm05, "", "A", "Y", "Y");
        else
            Add("CLM", Clean(c.PatientControlNumber), Money(c.TotalCharge), "", "", clm05, "Y", "A", "Y", "Y");

        if (institutional)
        {
            if (c.AdmissionDate is { } admit)
            {
                var hour = Digits(c.AdmissionHour);
                if (hour.Length == 4)
                    Add("DTP", "435", "DT", admit.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + hour);
                else
                    Add("DTP", "435", "D8", admit.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            }
            Add("DTP", "434", "RD8", Range(c.ServiceFrom, c.ServiceTo));
            Add("CL1", Clean(c.AdmissionTypeCode), Clean(c.AdmissionSourceCode), Clean(c.PatientStatusCode));
        }

        if (c.FrequencyCode is "7" or "8" && !string.IsNullOrWhiteSpace(c.OriginalStateReference))
            Add("REF", "F8", Clean(c.OriginalStateReference));
        if (!string.IsNullOrWhiteSpace(c.PriorAuthorizationNumber))
            Add("REF", "G1", Clean(c.PriorAuthorizationNumber));

        WriteDiagnoses(seg, c, institutional);

        // 2310A attending (837I) / 2310B rendering (837P) when it differs from billing
        if (!string.IsNullOrWhiteSpace(c.RenderingNpi) && c.RenderingNpi != bp.Npi)
            Add("NM1", institutional ? "71" : "82", "1", Clean(c.RenderingLastName), Clean(c.RenderingFirstName),
                "", "", "", "XX", Clean(c.RenderingNpi));

        // 2320 / 2330A / 2330B — the plan's own adjudication, as the other payer
        Add("SBR", env.PlanPayerResponsibility, "18", Clean(m.GroupNumber), "", "", "", "", "",
            env.PlanClaimFilingIndicator);
        Add("AMT", "D", Money(c.PlanPaidAmount));
        if (institutional) Add("OI", "", "", "Y", "", "", "Y");
        else Add("OI", "", "", "Y", "P", "", "Y");
        Add("NM1", "IL", "1", Clean(m.LastName), Clean(m.FirstName), "", "", "", "MI", Clean(m.MemberId));
        Add("NM1", "PR", "2", Clean(env.PlanName), "", "", "", "", "PI", Clean(env.PlanPayerId));
        Add("DTP", "573", "D8", c.PlanAdjudicationDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

        // 2400 lines, each with 2430 line adjudication
        foreach (var line in c.Lines)
        {
            Add("LX", line.LineNumber.ToString(CultureInfo.InvariantCulture));
            var hasProcedure = !string.IsNullOrWhiteSpace(line.ProcedureCode);
            // A revenue-code-only institutional line (room and board) has no SV202;
            // its 2430 SVD03 reports the revenue code under the NUBC qualifier.
            var product = hasProcedure ? Composite(line.ProcedureQualifier, line.ProcedureCode, line.Modifiers) : "";
            if (institutional)
            {
                Add("SV2", Clean(line.RevenueCode), product, Money(line.Charge), line.UnitType, Quantity(line.Units));
            }
            else
            {
                var pointers = string.Join(Component, line.DiagnosisPointers.Take(4)
                    .Select(p => p.ToString(CultureInfo.InvariantCulture)));
                var linePos = string.Equals(line.PlaceOfService, c.PlaceOfService, StringComparison.Ordinal)
                    ? "" : Clean(line.PlaceOfService);
                Add("SV1", product, Money(line.Charge), line.UnitType, Quantity(line.Units), linePos, "", pointers);
            }

            if (line.ServiceFrom == line.ServiceTo)
                Add("DTP", "472", "D8", line.ServiceFrom.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            else
                Add("DTP", "472", "RD8", Range(line.ServiceFrom, line.ServiceTo));

            if (!string.IsNullOrWhiteSpace(line.NationalDrugCode) && line.DrugQuantity is > 0)
            {
                Add("LIN", "", "N4", Digits(line.NationalDrugCode));
                Add("CTP", "", "", "", Quantity(line.DrugQuantity.Value), Clean(line.DrugUnit ?? "UN"));
            }

            if (institutional && !hasProcedure)
                Add("SVD", Clean(env.PlanPayerId), Money(line.PaidAmount), "NU" + Component + Clean(line.RevenueCode), "",
                    Quantity(line.Units));
            else if (institutional)
                Add("SVD", Clean(env.PlanPayerId), Money(line.PaidAmount), product, Clean(line.RevenueCode),
                    Quantity(line.Units));
            else
                Add("SVD", Clean(env.PlanPayerId), Money(line.PaidAmount), product, "", Quantity(line.Units));

            foreach (var group in line.Adjustments.Where(a => a.Amount != 0).GroupBy(a => a.GroupCode))
            {
                // CAS carries up to six reason/amount/quantity triples per group.
                foreach (var chunk in group.Chunk(6))
                {
                    var cas = new List<string?> { "CAS", group.Key };
                    foreach (var adj in chunk)
                    {
                        cas.Add(Clean(adj.ReasonCode));
                        cas.Add(Money(adj.Amount));
                        cas.Add(adj.Quantity is { } q ? Quantity(q) : "");
                    }
                    Add(cas.ToArray());
                }
            }

            Add("DTP", "573", "D8", c.PlanAdjudicationDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }

        seg.Add(Join("SE", (seg.Count + 1).ToString(CultureInfo.InvariantCulture), st02));
    }

    private static void WriteDiagnoses(List<string> seg, EncounterClaim c, bool institutional)
    {
        if (c.Diagnoses.Count == 0) return;
        if (institutional)
        {
            var principal = c.Diagnoses[0];
            seg.Add(Join("HI", DiagnosisComposite("ABK", principal, true)));
            var others = c.Diagnoses.Skip(1).Take(24).ToList();
            foreach (var chunk in others.Chunk(12))
                seg.Add(Join(new[] { "HI" }.Concat(chunk.Select(d => DiagnosisComposite("ABF", d, true))).ToArray()));
        }
        else
        {
            var parts = new List<string> { "HI", DiagnosisComposite("ABK", c.Diagnoses[0], false) };
            parts.AddRange(c.Diagnoses.Skip(1).Take(11).Select(d => DiagnosisComposite("ABF", d, false)));
            seg.Add(Join(parts.ToArray()));
        }
    }

    private static string DiagnosisComposite(string qualifier, EncounterDiagnosis d, bool withPoa)
    {
        var code = Clean(d.Code).Replace(".", "", StringComparison.Ordinal);
        if (withPoa && !string.IsNullOrWhiteSpace(d.PresentOnAdmission))
            return string.Join(Component, qualifier, code, "", "", "", "", "", "", Clean(d.PresentOnAdmission));
        return qualifier + Component + code;
    }

    private static string Composite(string qualifier, string code, IReadOnlyList<string> modifiers)
    {
        var parts = new List<string> { Clean(qualifier), Clean(code) };
        parts.AddRange(modifiers.Where(x => !string.IsNullOrWhiteSpace(x)).Take(4).Select(Clean));
        return string.Join(Component, parts);
    }

    /// <summary>Joins elements and drops trailing empty ones, as X12 requires.</summary>
    internal static string Join(params string?[] elements)
    {
        var last = elements.Length - 1;
        while (last > 0 && string.IsNullOrEmpty(elements[last])) last--;
        return string.Join(Element, elements.Take(last + 1).Select(e => e ?? ""));
    }

    internal static string Money(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture);

    private static string Quantity(decimal value) =>
        decimal.Round(value, 3, MidpointRounding.AwayFromZero).ToString("0.###", CultureInfo.InvariantCulture);

    private static string Range(DateOnly from, DateOnly to) =>
        from.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" +
        to.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>Strips every delimiter so a value can never split a segment or element.</summary>
    internal static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is Element or Component or Repetition or Terminator or '\r' or '\n') continue;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    private static string Digits(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : new string(value.Where(char.IsDigit).ToArray());

    private static string Fixed(string value, int length)
    {
        var v = Clean(value);
        return v.Length >= length ? v[..length] : v.PadRight(length);
    }
}
