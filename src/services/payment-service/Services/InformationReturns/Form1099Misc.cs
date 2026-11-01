namespace PaymentService.Services.InformationReturns;

/// <summary>One payment this plan issued, as the 1099 needs it.</summary>
public sealed record IssuedPayment(string? PayeeNpi, string PayeeName, decimal Amount, DateTime PaymentDate, bool IsReversal);

/// <summary>
/// One 1099-MISC recipient: every NPI paid under one TIN, totalled for box 6.
/// <see cref="RecipientKey"/> is the lowest of its NPIs (stable, and not the TIN).
/// </summary>
public sealed record Form1099Recipient(
    string RecipientKey,
    string? TinLast4,
    string? TinType,
    string Name,
    IReadOnlyList<string> Npis,
    decimal Box6MedicalPayments,
    int Payments,
    bool Reportable,
    bool W9OnFile);

/// <summary>Payments that could not be put under a TIN, so cannot be reported yet.</summary>
public sealed record Form1099Exception(string? PayeeNpi, string PayeeName, decimal Amount, string Reason);

public sealed record Form1099Summary(
    int TaxYear,
    decimal Threshold,
    IReadOnlyList<Form1099Recipient> Recipients,
    IReadOnlyList<Form1099Exception> Exceptions,
    IReadOnlyList<string> Notes)
{
    public int ReportableCount => Recipients.Count(r => r.Reportable);
    public decimal ReportableTotal => Recipients.Where(r => r.Reportable).Sum(r => r.Box6MedicalPayments);
}

/// <summary>
/// Form 1099-MISC box 6 (medical and health care payments) by payee TIN for
/// a calendar year: the plan's provider payments, net of reversals (a
/// recoupment lowers the year it was taken in), totalled across every NPI
/// billing under the TIN. Payments to corporations are included: box 6 has
/// no corporate exemption. A TIN at or above the year's threshold is
/// reportable.
/// </summary>
public static class Form1099MiscBuilder
{
    /// <summary>
    /// The box 6 reporting threshold for payments in <paramref name="taxYear"/>:
    /// $600 through 2025; $2,000 from 2026 (P.L. 119-21), indexed for
    /// inflation after 2026, so later years must be confirmed against the
    /// IRS instructions for that year.
    /// </summary>
    public static decimal Threshold(int taxYear) => taxYear <= 2025 ? 600m : 2000m;

    public static Form1099Summary Build(
        int taxYear,
        IEnumerable<IssuedPayment> payments,
        IReadOnlyDictionary<string, PayeeTaxIdentity?> identities,
        decimal? thresholdOverride = null)
    {
        var threshold = thresholdOverride ?? Threshold(taxYear);
        var notes = new List<string>();
        if (taxYear >= 2027 && thresholdOverride == null)
            notes.Add($"Threshold {threshold:C0} is the 2026 amount; confirm the inflation-adjusted amount for {taxYear}");

        var exceptions = new List<Form1099Exception>();
        var byTin = new Dictionary<string, (PayeeTaxIdentity Id, HashSet<string> Npis, decimal Total, int Count)>();

        foreach (var npiGroup in payments.Where(p => p.PaymentDate.Year == taxYear).GroupBy(p => p.PayeeNpi?.Trim() ?? string.Empty))
        {
            var total = npiGroup.Sum(p => p.Amount);
            var name = npiGroup.Select(p => p.PayeeName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? string.Empty;
            if (npiGroup.Key.Length == 0)
            {
                exceptions.Add(new Form1099Exception(null, name, total, "Payment has no payee NPI"));
                continue;
            }
            if (!identities.TryGetValue(npiGroup.Key, out var id) || id == null)
            {
                exceptions.Add(new Form1099Exception(npiGroup.Key, name, total, "NPI is not a provider on file"));
                continue;
            }
            if (id.TaxIdDigits is not { Length: 9 } digits)
            {
                exceptions.Add(new Form1099Exception(npiGroup.Key, id.LegalName.Length > 0 ? id.LegalName : name, total,
                    "No valid 9-digit TIN on file: request a W-9 (backup withholding may apply)"));
                continue;
            }

            if (!byTin.TryGetValue(digits, out var acc))
                acc = (id, new HashSet<string>(), 0m, 0);
            acc.Npis.Add(npiGroup.Key);
            byTin[digits] = (acc.Id, acc.Npis, acc.Total + total, acc.Count + npiGroup.Count());
        }

        var recipients = byTin
            .Select(kv => new Form1099Recipient(
                RecipientKey: kv.Value.Npis.Min(StringComparer.Ordinal)!,
                TinLast4: kv.Key[^4..],
                TinType: kv.Value.Id.TaxIdType,
                Name: kv.Value.Id.LegalName,
                Npis: kv.Value.Npis.OrderBy(n => n, StringComparer.Ordinal).ToList(),
                Box6MedicalPayments: kv.Value.Total,
                Payments: kv.Value.Count,
                Reportable: kv.Value.Total >= threshold,
                W9OnFile: kv.Value.Id.W9OnFile))
            .OrderByDescending(r => r.Box6MedicalPayments)
            .ToList();

        return new Form1099Summary(taxYear, threshold, recipients, exceptions, notes);
    }
}
