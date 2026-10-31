using System.Globalization;
using System.Text.RegularExpressions;

namespace BenefitPlanService.Import.QhpPuf;

/// <summary>What a QHP PUF cost-sharing cell says.</summary>
public enum QhpCostShareKind
{
    /// <summary>Blank cell.</summary>
    Blank,
    /// <summary>"Not Applicable": this kind of cost sharing does not apply.</summary>
    NotApplicable,
    /// <summary>"No Charge" (optionally "after deductible").</summary>
    NoCharge,
    /// <summary>A dollar copay or a coinsurance percent.</summary>
    Amount,
    /// <summary>Text the parser does not understand. Reported, never guessed.</summary>
    Unparsed,
}

/// <summary>
/// One parsed cost-sharing cell from the Benefits and Cost Sharing PUF
/// (CopayInnTier1, CoinsOutofNet, ...). <see cref="Value"/> is dollars for a
/// copay and a fraction (0.20) for coinsurance.
/// </summary>
public sealed record QhpCostShare(
    QhpCostShareKind Kind,
    decimal? Value,
    bool AfterDeductible,
    string? Per,
    string Raw)
{
    public static readonly QhpCostShare Blank = new(QhpCostShareKind.Blank, null, false, null, string.Empty);
}

/// <summary>
/// Parses the free-text cost-sharing and dollar cells of the CMS Exchange
/// PUFs. The data dictionaries give the shapes "No Charge", "No Charge after
/// deductible", "$X Copay", "$X Copay after deductible", "$X Copay with
/// deductible", "$X Copay per Day", "X% Coinsurance after deductible",
/// "Not Applicable", and bare "$X" / "X%". Anything else comes back as
/// <see cref="QhpCostShareKind.Unparsed"/> so the import report shows it.
/// </summary>
public static partial class QhpCostShareParser
{
    [GeneratedRegex(@"\$\s*([0-9][0-9,]*(?:\.[0-9]+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex DollarRegex();

    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)\s*%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"\bper\s+(day|stay|visit|admission|trip|script|prescription|procedure|item|month)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PerRegex();

    public static QhpCostShare ParseCopay(string? raw) => Parse(raw, percent: false);

    public static QhpCostShare ParseCoinsurance(string? raw) => Parse(raw, percent: true);

    /// <summary>
    /// A plan-level dollar cell (deductible, MOOP): "$1,500", "$0",
    /// "Not Applicable", blank. Returns null for anything but a dollar value.
    /// Older plan years put "$X per person | $Y per group" in one cell; the
    /// first amount is taken.
    /// </summary>
    public static decimal? ParseDollars(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = DollarRegex().Match(raw);
        if (!m.Success) return null;
        return decimal.Parse(m.Groups[1].Value.Replace(",", string.Empty), NumberStyles.Number, CultureInfo.InvariantCulture);
    }

    private static QhpCostShare Parse(string? raw, bool percent)
    {
        if (string.IsNullOrWhiteSpace(raw)) return QhpCostShare.Blank;
        var text = raw.Trim();
        var lower = text.ToLowerInvariant();

        // "with deductible" means the deductible applies as well as the copay;
        // CHO models both as a deductible that applies to the benefit.
        var afterDeductible = lower.Contains("after deductible") || lower.Contains("with deductible");
        var perMatch = PerRegex().Match(text);
        var per = perMatch.Success ? perMatch.Groups[1].Value.ToLowerInvariant() : null;

        if (lower.StartsWith("not applicable") || lower == "n/a" || lower == "na")
            return new(QhpCostShareKind.NotApplicable, null, false, null, text);

        if (lower.StartsWith("no charge"))
            return new(QhpCostShareKind.NoCharge, 0m, afterDeductible, per, text);

        if (percent)
        {
            var m = PercentRegex().Match(text);
            if (m.Success)
            {
                var pct = decimal.Parse(m.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture);
                if (pct is >= 0m and <= 100m)
                    return new(QhpCostShareKind.Amount, pct / 100m, afterDeductible, per, text);
            }
        }
        else
        {
            var m = DollarRegex().Match(text);
            if (m.Success)
            {
                var amount = decimal.Parse(m.Groups[1].Value.Replace(",", string.Empty), NumberStyles.Number, CultureInfo.InvariantCulture);
                return new(QhpCostShareKind.Amount, amount, afterDeductible, per, text);
            }
        }

        return new(QhpCostShareKind.Unparsed, null, afterDeductible, per, text);
    }
}
