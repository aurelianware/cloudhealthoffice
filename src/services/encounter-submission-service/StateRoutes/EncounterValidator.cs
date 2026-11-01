namespace EncounterSubmissionService.StateRoutes;

/// <summary>
/// Checks an encounter before it is written, against the 5010 implementation guide
/// rules that a state front end rejects on (SNIP levels 1–4 and 837 balancing).
/// A failing encounter is held back for rework instead of being sent and rejected.
/// </summary>
public static class EncounterValidator
{
    private static readonly HashSet<string> InpatientFacilityTypes = new(StringComparer.Ordinal)
    {
        "11", "18", "21", "41", "65", "66", "86"
    };

    private static readonly HashSet<string> AdjustmentGroups = new(StringComparer.Ordinal)
    {
        "CO", "CR", "OA", "PI", "PR"
    };

    public static IReadOnlyList<string> Validate(EncounterClaim c)
    {
        var errors = new List<string>();
        void Fail(string message) => errors.Add(message);

        if (c.Form == EncounterFormType.Dental)
        {
            Fail("837D dental encounters are not supported on this route yet");
            return errors;
        }

        var institutional = c.Form == EncounterFormType.Institutional;

        if (string.IsNullOrWhiteSpace(c.PatientControlNumber) || c.PatientControlNumber.Length > 38)
            Fail("CLM01 patient control number must be 1–38 characters");
        if (c.FrequencyCode is not ("1" or "7" or "8"))
            Fail($"CLM05-3 frequency code '{c.FrequencyCode}' is not 1, 7 or 8");
        if (c.FrequencyCode is "7" or "8" && string.IsNullOrWhiteSpace(c.OriginalStateReference))
            Fail("a replacement or void needs the state's original encounter reference (REF*F8)");

        if (c.Lines.Count == 0) Fail("an encounter needs at least one service line");
        if (!institutional && c.Lines.Count > 50) Fail("837P allows at most 50 service lines");
        if (institutional && c.Lines.Count > 999) Fail("837I allows at most 999 service lines");

        if (c.Diagnoses.Count == 0 || string.IsNullOrWhiteSpace(c.Diagnoses[0].Code))
            Fail(institutional ? "principal diagnosis (HI*ABK) is required" : "at least one diagnosis is required");

        if (c.ServiceTo < c.ServiceFrom) Fail("service end date is before the start date");

        // Member (2010BA): the patient is the subscriber, so address and DMG are required.
        var m = c.Member;
        if (string.IsNullOrWhiteSpace(m.MemberId)) Fail("member Medicaid identifier (2010BA NM109) is missing");
        if (string.IsNullOrWhiteSpace(m.LastName)) Fail("member last name is missing");
        if (m.DateOfBirth is null) Fail("member date of birth (DMG02) is missing");
        if (string.IsNullOrWhiteSpace(m.AddressLine) || string.IsNullOrWhiteSpace(m.City)
            || string.IsNullOrWhiteSpace(m.State) || Digits(m.PostalCode).Length is not (5 or 9))
            Fail("member address (2010BA N3/N4) is incomplete");

        // Billing provider (2010AA): NPI, EIN and a nine-digit ZIP are required in 5010.
        var bp = c.BillingProvider;
        if (!IsValidNpi(bp.Npi)) Fail($"billing provider NPI '{bp.Npi}' is not a valid NPI");
        if (string.IsNullOrWhiteSpace(bp.LastOrOrganizationName)) Fail("billing provider name is missing");
        if (Digits(bp.TaxId).Length != 9) Fail("billing provider tax ID (REF*EI) must be 9 digits");
        if (string.IsNullOrWhiteSpace(bp.AddressLine) || string.IsNullOrWhiteSpace(bp.City)
            || string.IsNullOrWhiteSpace(bp.State))
            Fail("billing provider address (2010AA N3/N4) is incomplete");
        if (Digits(bp.PostalCode).Length != 9) Fail("billing provider ZIP must be ZIP+4 (9 digits) in 5010");
        if (!string.IsNullOrWhiteSpace(c.RenderingNpi) && !IsValidNpi(c.RenderingNpi))
            Fail($"rendering/attending NPI '{c.RenderingNpi}' is not a valid NPI");

        if (institutional)
        {
            if (string.IsNullOrWhiteSpace(c.FacilityTypeCode) || Digits(c.FacilityTypeCode).Length != 2)
                Fail("837I needs the two-digit facility type from the type of bill");
            if (string.IsNullOrWhiteSpace(c.PatientStatusCode)) Fail("837I needs a patient status code (CL103)");
            if (c.FacilityTypeCode is { } ft && InpatientFacilityTypes.Contains(ft))
            {
                if (c.AdmissionDate is null) Fail("inpatient 837I needs an admission date (DTP*435)");
                if (string.IsNullOrWhiteSpace(c.AdmissionTypeCode)) Fail("inpatient 837I needs an admission type (CL101)");
            }
        }
        else if (string.IsNullOrWhiteSpace(c.PlaceOfService) || c.PlaceOfService.Trim().Length != 2)
        {
            Fail("837P needs a two-character place of service (CLM05-1)");
        }

        // Lines and balancing.
        var lineNumbers = new HashSet<int>();
        foreach (var line in c.Lines)
        {
            var label = $"line {line.LineNumber}";
            if (!lineNumbers.Add(line.LineNumber)) Fail($"{label}: duplicate line number");
            if (string.IsNullOrWhiteSpace(line.ProcedureCode) && !institutional)
                Fail($"{label}: procedure code is missing");
            if (institutional && string.IsNullOrWhiteSpace(line.RevenueCode))
                Fail($"{label}: revenue code (SV201) is missing");
            if (line.Charge < 0) Fail($"{label}: negative charge");
            if (line.Units <= 0) Fail($"{label}: units must be positive");
            if (!institutional)
            {
                if (line.DiagnosisPointers.Count == 0) Fail($"{label}: needs at least one diagnosis pointer");
                if (line.DiagnosisPointers.Any(p => p < 1 || p > Math.Min(12, c.Diagnoses.Count)))
                    Fail($"{label}: a diagnosis pointer does not point at a reported diagnosis");
            }

            foreach (var adj in line.Adjustments)
            {
                if (!AdjustmentGroups.Contains(adj.GroupCode))
                    Fail($"{label}: adjustment group '{adj.GroupCode}' is not CO, CR, OA, PI or PR");
                if (string.IsNullOrWhiteSpace(adj.ReasonCode))
                    Fail($"{label}: adjustment without a reason code");
            }

            var balance = line.Charge - line.PaidAmount - line.Adjustments.Sum(a => a.Amount);
            if (decimal.Round(balance, 2) != 0)
                Fail($"{label}: charge {line.Charge:0.00} does not balance to paid {line.PaidAmount:0.00} " +
                     $"plus adjustments {line.Adjustments.Sum(a => a.Amount):0.00} (difference {balance:0.00})");
        }

        if (c.Lines.Count > 0)
        {
            var lineCharges = c.Lines.Sum(l => l.Charge);
            if (decimal.Round(lineCharges - c.TotalCharge, 2) != 0)
                Fail($"claim charge {c.TotalCharge:0.00} does not equal the sum of line charges {lineCharges:0.00}");
            var linePaid = c.Lines.Sum(l => l.PaidAmount);
            if (decimal.Round(linePaid - c.PlanPaidAmount, 2) != 0)
                Fail($"plan paid {c.PlanPaidAmount:0.00} does not equal the sum of line paid amounts {linePaid:0.00}");
        }

        return errors;
    }

    /// <summary>NPI check digit (Luhn over "80840" + first nine digits).</summary>
    public static bool IsValidNpi(string? npi)
    {
        if (npi is null || npi.Length != 10 || !npi.All(char.IsDigit)) return false;
        var digits = "80840" + npi[..9];
        var sum = 0;
        var doubleIt = true;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            doubleIt = !doubleIt;
        }
        var check = (10 - sum % 10) % 10;
        return check == npi[9] - '0';
    }

    private static string Digits(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : new string(value.Where(char.IsDigit).ToArray());
}
