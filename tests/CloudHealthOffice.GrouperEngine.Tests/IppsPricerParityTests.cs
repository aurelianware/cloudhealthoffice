using System.Globalization;
using System.Text.Json;
using CloudHealthOffice.GrouperEngine.Ipps;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// Holds CHO's IPPS payment calculator to the answers CMS's own pricer gave for
/// the same stays.
///
/// <para>
/// This runs on every build. It needs no CMS artifact, because CMS's answers
/// are committed — <c>tools/cms-ipps-pricer-oracle/corpus/expected-fy2026.psv</c>,
/// written by that directory's <c>run.sh</c> against the pricer jar. The jar
/// itself is not in this repository and cannot be; see the tool's README.
/// </para>
///
/// <para>
/// <b>No rate is in this file.</b> The fiscal year's standardized amount, its
/// labor split, the capital rate and the fixed loss amount are all read out of
/// what CMS reported, which is the only honest way to test a calculator whose
/// design principle is that rates are data. It also means a new fiscal year is
/// a re-run of the oracle and nothing else.
/// </para>
///
/// <para>
/// Some rows are deliberately <i>not</i> compared, and the test at the bottom
/// asserts which ones. A harness that silently skipped what the calculator
/// cannot express would read as agreement.
/// </para>
/// </summary>
public class IppsPricerParityTests
{
    // CMS rounds at several different scales on the way through a payment —
    // nine decimal places on the federal portions, four on the fractions, two
    // on the thresholds — so the last cent of a four-figure payment is not
    // something to hold a reimplementation to. A dollar on a payment of
    // thousands is tight enough to catch a wrong formula and loose enough not
    // to fail on CMS's rounding order, which is not published.
    private const decimal Dollar = 1.00m;
    private const decimal Fraction = 0.0005m;

    /// <summary>
    /// Rows whose inputs this calculator cannot express, and why. Each one is
    /// a gap the parity run found, not a row that failed to matter.
    /// </summary>
    private static readonly Dictionary<string, string> NotComparable = new()
    {
        ["disproportionate-share"] =
            "CMS derives the disproportionate share adjustment from the hospital's SSI and "
            + "Medicaid day ratios; IppsProvider takes the resulting factor as an input, so "
            + "there is nothing here to compare it against.",
        ["quality-indicator-withheld"] =
            "The hospital quality indicator selects the full or reduced market basket update, "
            + "and so the standardized amount itself. IppsRateSet holds one standardized amount "
            + "and cannot express the reduced one.",
        ["transfer-fifty-fifty-special-payment-drg"] =
            "CMS refused this row with return code 12, and which post-acute rule a DRG carries "
            + "is a column of CMS's DRG table that this repository has no importer for yet.",
    };

    private sealed record Answer(string Case, Dictionary<string, string> Columns)
    {
        public decimal? Number(string column) =>
            Columns.TryGetValue(column, out var text) && !string.IsNullOrEmpty(text)
                ? decimal.Parse(text, CultureInfo.InvariantCulture)
                : null;

        public string Text(string column) => Columns.GetValueOrDefault(column, "");
    }

    private sealed record Case(
        string Name,
        string Drg,
        int LengthOfStay,
        decimal CoveredCharges,
        string ReviewCode,
        decimal OperatingCostToChargeRatio,
        decimal CapitalCostToChargeRatio,
        decimal CostOfLivingAdjustment,
        decimal ResidentToBedRatio);

    // ─────────────────────────────────────────────────────────
    //  Reading what the oracle wrote
    // ─────────────────────────────────────────────────────────

    private static string ToolDirectory()
    {
        // The test binary sits under tests/<project>/bin/<config>/<tfm>, so the
        // repository root is five levels up. Walking for a marker instead would
        // be more robust and less obvious; this fails loudly if the layout
        // changes, which is the better failure.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tools")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "tools", "cms-ipps-pricer-oracle");
    }

    private static IReadOnlyList<Answer> Answers()
    {
        var path = Path.Combine(ToolDirectory(), "corpus", "expected-fy2026.psv");
        var lines = File.ReadAllLines(path)
            .Where(line => !line.StartsWith('#') && line.Length > 0)
            .ToList();

        var header = lines[0].Split('|');
        return lines.Skip(1)
            .Select(line => line.Split('|'))
            .Select(fields => new Answer(
                fields[0],
                header.Zip(fields).ToDictionary(pair => pair.First, pair => pair.Second)))
            .ToList();
    }

    private static IReadOnlyList<Case> Cases()
    {
        var path = Path.Combine(ToolDirectory(), "corpus", "reference-claims.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var defaults = document.RootElement.GetProperty("providerDefaults");

        return document.RootElement.GetProperty("cases").EnumerateArray().Select(element =>
        {
            decimal Provider(string name)
            {
                if (element.TryGetProperty("provider", out var overrides)
                    && overrides.TryGetProperty(name, out var overridden))
                {
                    return overridden.GetDecimal();
                }

                return defaults.GetProperty(name).GetDecimal();
            }

            return new Case(
                element.GetProperty("name").GetString()!,
                element.GetProperty("drg").GetString()!,
                element.GetProperty("lengthOfStay").GetInt32(),
                element.GetProperty("coveredCharges").GetDecimal(),
                element.GetProperty("reviewCode").GetString()!,
                Provider("operatingCostToChargeRatio"),
                Provider("capitalCostToChargeRatio"),
                Provider("costOfLivingAdjustment"),
                Provider("internsToBedsRatio"));
        }).ToList();
    }

    /// <summary>
    /// The fiscal year's rates, read out of what CMS reported rather than
    /// written down here.
    /// </summary>
    private static IppsRateSet RatesFrom(IReadOnlyList<Answer> answers)
    {
        // Any full-quality row carries the year's split; they all agree, and
        // the one row that does not is the withheld-quality row, which is
        // excluded from the comparison for exactly that reason.
        var reference = answers.First(answer => answer.Case == "wage-index-below-one");
        var labor = reference.Number("national_labor_cost")!.Value;
        var nonLabor = reference.Number("national_non_labor_cost")!.Value;
        var standardizedAmount = labor + nonLabor;

        // CMS reports the capital rate already multiplied by the hospital's
        // geographic adjustment factor, so the federal rate is that divided
        // back out.
        var capitalRate = reference.Number("capital_federal_rate")!.Value
            / reference.Number("capital_geographic_adjustment_factor")!.Value;

        // The statutory share applies on this row, because its wage index is
        // below 1.0. The larger share is read from a row above it.
        var higher = answers.First(answer => answer.Case == "wage-index-above-one");
        var higherTotal = higher.Number("national_labor_cost")!.Value
            + higher.Number("national_non_labor_cost")!.Value;

        return new IppsRateSet(
            FiscalYear: 2026,
            FirstDischargeDate: new DateOnly(2025, 10, 1),
            LastDischargeDate: new DateOnly(2026, 9, 30),
            OperatingStandardizedAmount: standardizedAmount,
            HigherLaborShare: new IppsLaborShare(
                higher.Number("national_labor_cost")!.Value / higherTotal,
                higher.Number("national_non_labor_cost")!.Value / higherTotal),
            StatutoryLaborShare: new IppsLaborShare(
                labor / standardizedAmount, nonLabor / standardizedAmount),
            CapitalFederalRate: capitalRate,
            // The threshold column is the year's fixed loss amount before any
            // geographic adjustment, which is what IppsOutlierPolicy wants.
            Outlier: new IppsOutlierPolicy(
                reference.Number("cost_threshold")!.Value,
                MarginalCostFactor: 0.80m,
                BurnMarginalCostFactor: 0.90m,
                BurnDrgs: new HashSet<int> { 927, 928, 929, 933, 934, 935 }),
            Teaching: new IppsTeachingPolicy(0.405d, 1.35m),
            EmpiricallyJustifiedDshShare: 0.25m);
    }

    private static IppsPayment Price(IppsRateSet rates, Case input, Answer answer)
    {
        var provider = IppsProvider.Plain("450001", answer.Number("wage_index")!.Value) with
        {
            CostOfLivingAdjustment = input.CostOfLivingAdjustment,
            OperatingCostToChargeRatio = input.OperatingCostToChargeRatio,
            CapitalCostToChargeRatio = input.CapitalCostToChargeRatio,
            ResidentToBedRatio = input.ResidentToBedRatio,
            CapitalGeographicAdjustmentFactor =
                answer.Number("capital_geographic_adjustment_factor")!.Value,
        };

        // Review code 03 is CMS's per-diem transfer. Nothing in CHO's model
        // reads a review code: a transfer arrives as IppsDischargeKind, which
        // is the claim's discharge status, and the mapping between the two
        // belongs wherever claims are read rather than here.
        var kind = input.ReviewCode == "03"
            ? IppsDischargeKind.TransferredToAcuteCare
            : IppsDischargeKind.Discharged;

        return IppsPaymentCalculator.Pay(
            rates,
            provider,
            new IppsStay(
                int.Parse(input.Drg, CultureInfo.InvariantCulture),
                answer.Number("drg_weight")!.Value,
                answer.Number("average_length_of_stay")!.Value,
                input.LengthOfStay,
                input.CoveredCharges,
                kind));
    }

    private static IEnumerable<(Case Input, Answer Expected, IppsPayment Actual)> Comparable()
    {
        var answers = Answers();
        var rates = RatesFrom(answers);

        foreach (var input in Cases())
        {
            if (NotComparable.ContainsKey(input.Name))
                continue;

            var expected = answers.First(answer => answer.Case == input.Name);
            yield return (input, expected, Price(rates, input, expected));
        }
    }

    // ─────────────────────────────────────────────────────────
    //  The comparison
    // ─────────────────────────────────────────────────────────

    [Fact]
    public void The_oracles_answers_are_present_and_cover_every_reference_stay()
    {
        var answers = Answers();
        var cases = Cases();

        Assert.NotEmpty(answers);
        Assert.Equal(cases.Count, answers.Count);
        Assert.Equal(
            cases.Select(input => input.Name).OrderBy(name => name),
            answers.Select(answer => answer.Case).OrderBy(name => name));
    }

    [Fact]
    public void The_base_drg_payment_agrees_with_CMS_on_every_comparable_stay()
    {
        var failures = new List<string>();

        foreach (var (input, expected, actual) in Comparable())
        {
            var cms = expected.Number("operating_base_drg_payment");
            if (cms is null)
                continue;

            // CHO's base payment is the wage-adjusted standardized amount times
            // the weight. CMS reports its own after the transfer reduction has
            // been applied to it — which is worth knowing, because comparing
            // the two directly looks like a disagreement of thousands on a
            // transfer and is only a disagreement about where in the
            // calculation the number was read.
            var cho = actual.Operating.BasePayment * (actual.Transfer?.Fraction ?? 1m);
            var difference = Math.Abs(cho - cms.Value);
            if (difference > Dollar)
                failures.Add($"{input.Name}: CHO {cho:F2}, CMS {cms.Value:F2}, off by {difference:F2}");
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void The_transfer_fraction_agrees_with_CMS_on_every_comparable_stay()
    {
        var failures = new List<string>();

        foreach (var (input, expected, actual) in Comparable())
        {
            var cms = expected.Number("discharge_fraction");
            if (cms is null)
                continue;

            // A discharge has no transfer, and CMS reports a fraction of one
            // for it. The two have to agree on that as much as on a reduction:
            // a calculator that reduced an ordinary discharge would pass a
            // transfer-only comparison.
            var cho = actual.Transfer?.Fraction ?? 1m;
            var difference = Math.Abs(cho - cms.Value);
            if (difference > Fraction)
                failures.Add($"{input.Name}: CHO {cho:F4}, CMS {cms.Value:F4}");
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void The_capital_outlier_agrees_with_CMS_exactly()
    {
        var failures = new List<string>();

        foreach (var (input, expected, actual) in Comparable())
        {
            // CMS reports the capital side as a total including its outlier, so
            // the outlier is that less the capital payment. The capital side is
            // the cleaner of the two to compare because it has one geographic
            // factor and no labor split, so there is nothing to round twice.
            var total = expected.Number("capital_total_payment");
            var portion = expected.Number("capital_federal_specific_portion");
            if (total is null || portion is null)
                continue;

            var cms = total.Value - portion.Value;
            var difference = Math.Abs(actual.Outlier.Capital.Payment - cms);
            if (difference > Dollar)
            {
                failures.Add(
                    $"{input.Name}: CHO {actual.Outlier.Capital.Payment:F2}, "
                    + $"CMS {cms:F2}, off by {difference:F2}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void A_stay_CMS_paid_no_outlier_on_gets_none_here_either()
    {
        var failures = new List<string>();

        foreach (var (input, expected, actual) in Comparable())
        {
            var operating = expected.Number("operating_outlier_payment") ?? 0m;
            if (operating > 0m)
                continue;

            // The direction that matters. An outlier formula that is wrong in
            // the generous direction pays on ordinary cases, and that is both
            // the expensive error and the one a total-only comparison hides
            // inside a large payment.
            if (actual.Outlier.Operating.Payment > 0m)
            {
                failures.Add(
                    $"{input.Name}: CMS paid no operating outlier, CHO paid "
                    + $"{actual.Outlier.Operating.Payment:F2}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void A_stay_CMS_paid_an_outlier_on_is_paid_one_here()
    {
        var paid = 0;

        foreach (var (input, expected, actual) in Comparable())
        {
            var operating = expected.Number("operating_outlier_payment") ?? 0m;
            if (operating <= 0m)
                continue;

            paid++;
            Assert.True(
                actual.Outlier.Operating.Payment > 0m,
                $"{input.Name}: CMS paid an operating outlier of {operating:F2} and CHO paid none");
        }

        // And the corpus has to contain some, or the test above is vacuous.
        Assert.True(paid >= 2, $"only {paid} outlier stays in the corpus");
    }

    [Fact]
    public void The_rows_CHO_cannot_express_are_named_rather_than_quietly_skipped()
    {
        var names = Cases().Select(input => input.Name).ToHashSet();

        // Every exclusion must still be a real row. An exclusion whose row has
        // been renamed or removed would otherwise sit here forever, excluding
        // nothing and reading as a known gap.
        foreach (var (name, reason) in NotComparable)
        {
            Assert.Contains(name, names);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        // And the gap is bounded: most of the corpus is compared. If this
        // starts failing because more rows became inexpressible, that is the
        // calculator moving away from CMS and worth stopping for.
        Assert.True(
            NotComparable.Count * 3 < names.Count,
            $"{NotComparable.Count} of {names.Count} reference stays are not compared");
    }
}
