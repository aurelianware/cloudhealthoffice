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
        decimal ResidentToBedRatio,
        decimal UncompensatedCarePerDischarge,
        bool QualityDataSubmitted,
        bool ElectronicRecordsReduction,
        decimal? ReadmissionsAdjustmentFactor,
        decimal? ValueBasedPurchasingAdjustmentFactor,
        bool LowVolumeTemporaryRelief,
        decimal LowVolumeAdjustmentFactor,
        decimal PassThroughPerDiem,
        IppsHospitalSpecificCategory HospitalSpecificCategory,
        decimal FacilitySpecificRate,
        bool HospitalAcquiredConditionReduction);

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

            // The two payment-reduction indicators are strings in CMS's
            // provider record, and CMS reads them the strict way: the quality
            // requirement is met only on "1", and the record reduction applies
            // only on "Y". A missing indicator is therefore not neutral, and
            // the defaults block does not carry one for the record reduction
            // at all, so an absent key reads as blank and not as true.
            string Indicator(string name)
            {
                if (element.TryGetProperty("provider", out var overrides)
                    && overrides.TryGetProperty(name, out var overridden))
                {
                    return overridden.GetString() ?? "";
                }

                return defaults.TryGetProperty(name, out var fallback)
                    ? fallback.GetString() ?? ""
                    : "";
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
                Provider("internsToBedsRatio"),
                Provider("uncompensatedCareAmount"),
                QualityDataSubmitted: Indicator("hospitalQualityIndicator") == "1",
                ElectronicRecordsReduction: Indicator("ehrReductionIndicator") == "Y",
                // CMS's participant indicators say whether the hospital is in
                // the programme at all, which is a different thing from a
                // factor of 1.0 — so a non-participant reads as null here and
                // not as a neutral factor.
                ReadmissionsAdjustmentFactor: Indicator("hrrParticipantIndicator") == "1"
                    ? Provider("hrrAdjustment")
                    : null,
                ValueBasedPurchasingAdjustmentFactor:
                    Indicator("vbpParticipantIndicator") == "Y"
                        ? Provider("vbpAdjustment")
                        : null,
                LowVolumeTemporaryRelief: Indicator("temporaryReliefIndicator") == "Y",
                LowVolumeAdjustmentFactor: Provider("lowVolumeAdjustmentFactor"),
                PassThroughPerDiem: Provider("passThroughTotalAmount"),
                HospitalSpecificCategory: CategoryFor(Indicator("providerType")),
                FacilitySpecificRate: Provider("ppsFacilitySpecificRate"),
                HospitalAcquiredConditionReduction:
                    Indicator("hacReductionParticipantIndicator") == "Y");
        }).ToList();
    }

    /// <summary>
    /// CMS's two-character provider type, as the three hospital-specific
    /// categories. The mapping lives here rather than in the engine because it
    /// is a fact about CMS's provider-specific file format, and translating it
    /// is a loader's job — but it has to be written down somewhere exactly
    /// once, and getting a pair into the wrong branch costs 25 percent of the
    /// portion with no other symptom.
    /// </summary>
    private static IppsHospitalSpecificCategory CategoryFor(string providerType) => providerType switch
    {
        "16" or "17" => IppsHospitalSpecificCategory.SoleCommunity,
        "21" or "22" => IppsHospitalSpecificCategory.EssentialAccessCommunity,
        "14" or "15" => IppsHospitalSpecificCategory.MedicareDependent,
        _ => IppsHospitalSpecificCategory.None,
    };

    /// <summary>
    /// Which row of CMS's operating rate table a reference stay was paid from.
    /// </summary>
    private static IppsRateTableKey KeyFor(Case input, Answer answer) => new(
        input.QualityDataSubmitted,
        input.ElectronicRecordsReduction,
        WageIndexAboveOne: answer.Number("wage_index")!.Value > 1m);

    /// <summary>
    /// The fiscal year's rates, read out of what CMS reported rather than
    /// written down here.
    ///
    /// <para>
    /// The eight-row operating rate table is built from CMS's answers rather
    /// than from its rate table file, which is the stronger check of the two:
    /// the pricer exposes no endpoint for that file, so the only way to learn a
    /// row is to price a claim that selects it, and that exercises CMS's own
    /// selection branch as well as its amounts. The corpus therefore has to
    /// carry at least one stay per row, and this fails by name if it does not.
    /// </para>
    /// </summary>
    private static IppsRateSet RatesFrom(IReadOnlyList<Case> cases, IReadOnlyList<Answer> answers)
    {
        var byName = answers.ToDictionary(answer => answer.Case);
        var rows = new Dictionary<int, IppsStandardizedAmount>();

        foreach (var input in cases)
        {
            var answer = byName[input.Name];
            var labor = answer.Number("national_labor_cost");
            var nonLabor = answer.Number("national_non_labor_cost");
            if (labor is null || nonLabor is null)
                continue;

            var amount = new IppsStandardizedAmount(labor.Value, nonLabor.Value);
            var number = KeyFor(input, answer).CmsTableNumber;

            // Two stays that select the same row must have been paid the same
            // amount. If they were not, either the key is wrong or something
            // other than these three facts moves the standardized amount, and
            // both are worth stopping for rather than quietly keeping the last
            // one seen.
            if (rows.TryGetValue(number, out var already))
            {
                Assert.Equal(already, amount);
                continue;
            }

            rows[number] = amount;
        }

        var missing = Enumerable.Range(1, 8).Where(number => !rows.ContainsKey(number)).ToList();
        Assert.True(
            missing.Count == 0,
            "the corpus has no stay selecting CMS rate table row(s) "
            + string.Join(", ", missing.Select(number => $"tab{number}")));

        // CMS reports the capital rate already multiplied by the hospital's
        // geographic adjustment factor, so the federal rate is that divided
        // back out.
        var reference = byName["wage-index-below-one"];
        var capitalRate = reference.Number("capital_federal_rate")!.Value
            / reference.Number("capital_geographic_adjustment_factor")!.Value;

        // The labor percentages are the outlier threshold's, and CMS does not
        // report them, so they are derived from a row each side of a wage index
        // of 1.0. Derived rather than written down for the same reason as
        // everything else here, and they come out at the published 62 and 66
        // percent to within a rounded cent.
        var statutory = rows[3];
        var higher = rows[1];

        return new IppsRateSet(
            FiscalYear: 2026,
            FirstDischargeDate: new DateOnly(2025, 10, 1),
            LastDischargeDate: new DateOnly(2026, 9, 30),
            OperatingRates: new IppsOperatingRateTable(rows),
            HigherLaborPercentages: new IppsLaborShare(
                higher.LaborRelated / higher.Total, higher.NonLaborRelated / higher.Total),
            StatutoryLaborPercentages: new IppsLaborShare(
                statutory.LaborRelated / statutory.Total,
                statutory.NonLaborRelated / statutory.Total),
            CapitalFederalRate: capitalRate,
            // The threshold column is the year's fixed loss amount before any
            // geographic adjustment, which is what IppsOutlierPolicy wants.
            Outlier: new IppsOutlierPolicy(
                reference.Number("cost_threshold")!.Value,
                MarginalCostFactor: 0.80m,
                BurnMarginalCostFactor: 0.90m,
                BurnDrgs: new HashSet<int> { 927, 928, 929, 933, 934, 935 }),
            Teaching: new IppsTeachingPolicy(0.405d, 1.35m),
            HospitalSpecificUpdate: HospitalSpecificUpdateFrom(cases, answers),
            EmpiricallyJustifiedDshShare: 0.25m);
    }

    /// <summary>
    /// The year's hospital-specific update factors, derived from the reference
    /// stays that are plain enough to invert.
    ///
    /// <para>
    /// CMS reports no column for this factor, and unlike the rate table rows it
    /// cannot be read off a payment in general: the portion CMS pays is a
    /// <i>difference</i>, so inverting it needs the federal side exactly. On a
    /// plain discharge with no teaching, no disproportionate share, no outlier
    /// and no uncompensated care payment, the federal side is the reported
    /// federal specific portion and nothing else, and the inversion is sound to
    /// a hundredth of a cent. The corpus's two sole-community discharges are
    /// therefore <b>calibration rather than comparison</b> — the same standing
    /// the <c>wage-index-below-one</c> row has for the capital rate — and the
    /// other five hospital-specific rows, which carry the 75 percent, the
    /// full-weight transfer, the losing comparison and the ordering, are not
    /// implied by them.
    /// </para>
    /// </summary>
    private static IppsHospitalSpecificUpdate HospitalSpecificUpdateFrom(
        IReadOnlyList<Case> cases, IReadOnlyList<Answer> answers)
    {
        var byName = answers.ToDictionary(answer => answer.Case);
        var factors = new Dictionary<bool, decimal>();

        foreach (var input in cases)
        {
            var answer = byName[input.Name];
            var portion = answer.Number("hospital_specific_portion") ?? 0m;

            // Only a row this can be inverted from: a full-rate category, a
            // portion CMS actually paid, and nothing else on the federal side.
            if (portion <= 0m
                || input.HospitalSpecificCategory != IppsHospitalSpecificCategory.SoleCommunity
                || input.ReviewCode != "00"
                || input.FacilitySpecificRate <= 0m
                || (answer.Number("operating_ime_adjustment") ?? 0m) != 0m
                || (answer.Number("operating_dsh_adjustment") ?? 0m) != 0m
                || (answer.Number("operating_outlier_payment") ?? 0m) != 0m
                || (answer.Number("uncompensated_care_payment") ?? 0m) != 0m)
            {
                continue;
            }

            var federal = answer.Number("operating_federal_specific_portion")!.Value;
            var weight = answer.Number("drg_weight")!.Value;
            var factor = decimal.Round(
                (portion + federal) / (input.FacilitySpecificRate * weight),
                5,
                MidpointRounding.AwayFromZero);

            // Two such rows sharing the two payment-reduction flags must invert
            // to the same factor, or something other than those flags moves it.
            if (factors.TryGetValue(input.ElectronicRecordsReduction, out var already))
            {
                Assert.Equal(already, factor);
                continue;
            }

            factors[input.ElectronicRecordsReduction] = factor;
        }

        Assert.True(
            factors.ContainsKey(false) && factors.ContainsKey(true),
            "the corpus has no sole-community discharge to derive the hospital-specific "
            + "update factor from, with and without the electronic health record reduction");

        // The two factors no corpus stay selects are left at zero rather than
        // filled in with a plausible number. A stay that selected one would be
        // paid no hospital-specific portion at all, which is the loud failure
        // wanted here; a guessed factor would be a quiet wrong payment.
        return new IppsHospitalSpecificUpdate(
            WithQualityData: factors[false],
            WithQualityDataAndRecordsReduction: factors[true],
            WithoutQualityData: 0m,
            WithoutQualityDataAndRecordsReduction: 0m,
            // The published factor is a market basket update times two
            // budget-neutrality factors times a cumulative prior-year chain,
            // and only the product can be inverted from a payment. The split
            // matters to whoever loads a fiscal year, not to the arithmetic.
            BudgetNeutrality: 1m,
            CumulativePriorYears: 1m);
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
            UncompensatedCarePerDischarge = input.UncompensatedCarePerDischarge,
            QualityDataSubmitted = input.QualityDataSubmitted,
            ElectronicRecordsReduction = input.ElectronicRecordsReduction,
            ReadmissionsAdjustmentFactor = input.ReadmissionsAdjustmentFactor,
            ValueBasedPurchasingAdjustmentFactor = input.ValueBasedPurchasingAdjustmentFactor,
            LowVolumeTemporaryRelief = input.LowVolumeTemporaryRelief,
            LowVolumeAdjustmentFactor = input.LowVolumeAdjustmentFactor,
            PassThroughPerDiem = input.PassThroughPerDiem,
            HospitalSpecificCategory = input.HospitalSpecificCategory,
            FacilitySpecificRate = input.FacilitySpecificRate,
            HospitalAcquiredConditionReduction = input.HospitalAcquiredConditionReduction,
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
        var cases = Cases();
        var rates = RatesFrom(cases, answers);

        foreach (var input in cases)
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
    public void The_standardized_amount_CHO_pays_from_is_the_one_CMS_paid_from()
    {
        var failures = new List<string>();
        var rowsSeen = new HashSet<int>();

        foreach (var (input, expected, actual) in Comparable())
        {
            var labor = expected.Number("national_labor_cost");
            var nonLabor = expected.Number("national_non_labor_cost");
            if (labor is null || nonLabor is null)
                continue;

            rowsSeen.Add(KeyFor(input, expected).CmsTableNumber);

            // Exactly, not within a dollar. These are published amounts: CHO
            // either selected the row CMS selected or it did not, and a
            // near-miss would mean it had computed one from another, which is
            // the rounding error IppsStandardizedAmount exists to avoid.
            if (actual.Operating.StandardizedAmount.LaborRelated != labor.Value
                || actual.Operating.StandardizedAmount.NonLaborRelated != nonLabor.Value)
            {
                failures.Add(
                    $"{input.Name}: CHO {actual.Operating.StandardizedAmount.LaborRelated:F2}"
                    + $"/{actual.Operating.StandardizedAmount.NonLaborRelated:F2}, "
                    + $"CMS {labor.Value:F2}/{nonLabor.Value:F2}");
            }
        }

        Assert.Empty(failures);

        // And every row of the table was reached, or this is eight assertions
        // about one row.
        Assert.Equal(8, rowsSeen.Count);

        // What this test does and does not establish, since the table it
        // compares against was built from these same answers. It establishes
        // that the three facts in IppsRateTableKey are the whole key — a
        // calculator ignoring either indicator collides two of CMS's rows and
        // fails in RatesFrom — and that each row is reached and used whole.
        // It cannot catch a wrong but consistent permutation of CMS's eight
        // table numbers, because every row would still be looked up by the
        // same name it was stored under. That is what the numbering theory in
        // IppsPaymentCalculatorTests is for, and it matters to a loader
        // reading CMS's rate file rather than to this calculation.
    }

    [Fact]
    public void The_four_hospital_level_adjustments_agree_with_CMS_one_by_one()
    {
        var failures = new List<string>();
        var compared = 0;

        foreach (var (input, expected, actual) in Comparable())
        {
            void Compare(string column, decimal cho)
            {
                var cms = expected.Number(column) ?? 0m;
                if (cms == 0m && cho == 0m)
                    return;

                compared++;
                var difference = Math.Abs(cho - cms);
                if (difference > Dollar)
                {
                    failures.Add(
                        $"{input.Name} {column}: CHO {cho:F2}, CMS {cms:F2}, "
                        + $"off by {difference:F2}");
                }
            }

            Compare("low_volume_payment", actual.Adjustments.LowVolume);
            Compare("readmissions_adjustment", actual.Adjustments.Readmissions);
            Compare(
                "value_based_purchasing_adjustment", actual.Adjustments.ValueBasedPurchasing);
            Compare(
                "hospital_acquired_condition_payment",
                actual.Adjustments.HospitalAcquiredConditionPenalty);
        }

        Assert.Empty(failures);

        // One by one rather than on the total, because the four are taken in an
        // order 42 CFR does not give and a wrong order agrees on each in
        // isolation. The corpus has a row carrying all four at once for that,
        // and this count fails if these rows are dropped.
        Assert.True(compared >= 8, $"only {compared} adjustment amounts compared");
    }

    [Fact]
    public void The_hospital_specific_portion_agrees_with_CMS_on_every_stay_that_has_one()
    {
        var failures = new List<string>();
        var paid = 0;
        var refused = 0;

        foreach (var (input, expected, actual) in Comparable())
        {
            var cms = expected.Number("hospital_specific_portion") ?? 0m;

            // A hospital CMS does not recognise as one of the three categories
            // reports no percentage at all, and must be paid no portion. This
            // is the direction that matters most: the portion is a four-figure
            // amount, so paying one to an ordinary hospital is not a rounding
            // disagreement.
            if (expected.Number("hospital_specific_portion_percent") is null)
            {
                if (actual.HospitalSpecific.Payment != 0m)
                {
                    failures.Add(
                        $"{input.Name}: CMS pays no hospital-specific portion at this hospital "
                        + $"and CHO paid {actual.HospitalSpecific.Payment:F2}");
                }

                continue;
            }

            if (cms == 0m)
            {
                // Recognised, but its own rate lost the comparison. CHO has to
                // lose it too: the alternative is an engine that pays every
                // sole community hospital a top-up.
                refused++;
                if (actual.HospitalSpecific.Payment != 0m)
                {
                    failures.Add(
                        $"{input.Name}: CMS paid no portion and CHO paid "
                        + $"{actual.HospitalSpecific.Payment:F2}");
                }

                continue;
            }

            paid++;
            var difference = Math.Abs(actual.HospitalSpecific.Payment - cms);
            if (difference > Dollar)
            {
                failures.Add(
                    $"{input.Name}: CHO {actual.HospitalSpecific.Payment:F2}, CMS {cms:F2}, "
                    + $"off by {difference:F2}");
            }
        }

        Assert.Empty(failures);

        // The corpus has to carry both outcomes, or each half of the comparison
        // is being asserted about nothing. Two of the paid rows are the ones
        // HospitalSpecificUpdateFrom calibrates against; the rest are not.
        Assert.True(paid >= 4, $"only {paid} stays were paid a hospital-specific portion");
        Assert.True(refused >= 1, "no stay in the corpus lost the hospital-specific comparison");
    }

    [Fact]
    public void The_pass_through_is_the_one_amount_this_oracle_cannot_confirm()
    {
        var withAmountOnFile = 0;

        foreach (var (input, expected, actual) in Comparable())
        {
            // CMS computes the pass-through only for a request marked as coming
            // from its web pricer — `cms-pricer-client: WEB-PRICER` — and the
            // price-claim endpoint this oracle drives never takes that path.
            // So the column is empty on every row, and this asserts it rather
            // than assuming it: the day CMS starts reporting a figure, this
            // fails and the pass-through becomes comparable like everything
            // else.
            Assert.Null(expected.Number("passthrough_total_plus_misc"));

            if (input.PassThroughPerDiem <= 0m)
            {
                Assert.Equal(0m, actual.PassThroughPayment);
                continue;
            }

            withAmountOnFile++;

            // And CHO does pay one, so the disagreement is explicit. This is
            // the one place in this file where CHO paying more than CMS
            // reported is the expected result, and the reason is that CMS did
            // not compute the amount rather than that it computed zero.
            Assert.Equal(
                input.PassThroughPerDiem * input.LengthOfStay, actual.PassThroughPayment);
            Assert.True(actual.PassThroughPayment > 0m);
        }

        // The corpus has to carry such a hospital, or the assertion above is
        // about nothing and the boundary goes unrecorded.
        Assert.True(
            withAmountOnFile >= 1,
            "no reference stay has a pass-through amount on its provider record");
    }

    [Fact]
    public void The_model_one_bundled_adjustment_is_dead_for_this_fiscal_year()
    {
        // CMS's rule multiplies the hospital's Model 1 discount by zero for any
        // discharge after 31 December 2016, so the adjustment is identically
        // zero however the provider record is filled in. The corpus's
        // pass-through row carries a 10 percent discount for exactly this
        // check: CMS pays it the same total as the plain stay it is otherwise
        // identical to in every rate-determining respect, which is why nothing
        // in CHO models the adjustment at all.
        var answers = Answers().ToDictionary(answer => answer.Case);

        var bundled = answers["pass-through-amount-on-file"].Number("total_payment");
        var plain = answers["wage-index-below-one"].Number("total_payment");

        Assert.NotNull(bundled);
        Assert.NotNull(plain);
        Assert.Equal(plain!.Value, bundled!.Value);
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
