using CloudHealthOffice.Infrastructure.Reporting;
using System.Text.RegularExpressions;

namespace PremiumBillingService.Mlr;

/// <summary>
/// The amounts finance enters on HHSC's MLR Report (UMCM 5.3.13.2, version
/// 2.4), line by line. Amounts on "Less:" lines are entered as positive
/// numbers: the template subtracts them. Lines 2.2(k) (solvency funds) and
/// 3.4 (other denominator adjustment) take a sign.
/// </summary>
public sealed class TexasMlrRequest
{
    /// <summary>State fiscal year, named for the year it ends: SFY 2027 is September 2026 to August 2027.</summary>
    public int StateFiscalYear { get; set; }

    /// <summary>MCO legal entity name, for the header and the attestation (required for the workbook).</summary>
    public string? McoName { get; set; }

    /// <summary>Default: today in Texas.</summary>
    public DateOnly? SubmissionDate { get; set; }

    /// <summary>Line 1. Default: the expected member-months loaded for the fiscal year.</summary>
    public int? MemberMonths { get; set; }

    /// <summary>2.1 Incurred claims (incl. Rx, excl. QI), per the FSR.</summary>
    public decimal IncurredClaims { get; set; }

    /// <summary>2.2 a. Withholds from payments to network providers.</summary>
    public decimal ProviderWithholds { get; set; }

    /// <summary>2.2 b. Less: claims recoverable for anticipated coordination of benefits.</summary>
    public decimal CoordinationOfBenefitsRecoverable { get; set; }

    /// <summary>2.2 c. Less: subrogation recoveries.</summary>
    public decimal SubrogationRecoveries { get; set; }

    /// <summary>2.2 d. Incentive and bonus payments to network providers tied to quality.</summary>
    public decimal ProviderIncentives { get; set; }

    /// <summary>2.2 e. Claims payments recovered through fraud reduction, up to fraud reduction expenses.</summary>
    public decimal FraudReductionRecoveries { get; set; }

    /// <summary>2.2 f. State directed payments to providers.</summary>
    public decimal StateDirectedPayments { get; set; }

    /// <summary>2.2 g. Other CFR-specified additions.</summary>
    public decimal OtherAdditions { get; set; }

    /// <summary>2.2 h. Less: overpayment recoveries from network providers.</summary>
    public decimal OverpaymentRecoveries { get; set; }

    /// <summary>2.2 i. Less: prescription drug rebates received or accrued.</summary>
    public decimal PrescriptionDrugRebates { get; set; }

    /// <summary>2.2 j. Less: other CFR-specified subtractions.</summary>
    public decimal OtherSubtractions { get; set; }

    /// <summary>2.2 k. Net solvency fund payments (positive) or receipts (negative).</summary>
    public decimal SolvencyFundsNet { get; set; }

    /// <summary>2.2 l. Incurred claims paid by one MCO later assumed by another entity.</summary>
    public decimal ClaimsAssumedByAnotherEntity { get; set; }

    /// <summary>2.3 Health care quality improvement.</summary>
    public decimal QualityImprovement { get; set; }

    /// <summary>2.4 Fraud prevention activities (counted up to 0.5% of the denominator).</summary>
    public decimal FraudPrevention { get; set; }

    /// <summary>3.1 Gross revenues per the FSR. Default: state capitation received on 820s for the fiscal year plus <see cref="OtherPremiumRevenue"/>.</summary>
    public decimal? GrossRevenue { get; set; }

    /// <summary>Added to the 820 capitation when <see cref="GrossRevenue"/> is not given.</summary>
    public decimal OtherPremiumRevenue { get; set; }

    /// <summary>3.2 Less: taxes, licensing and regulatory fees.</summary>
    public decimal TaxesAndFees { get; set; }

    /// <summary>3.3 Less: experience rebate (the state's share of profit).</summary>
    public decimal ExperienceRebate { get; set; }

    /// <summary>3.4 Other CFR-specified adjustment to the denominator.</summary>
    public decimal OtherDenominatorAdjustment { get; set; }

    public List<string> Validate(bool forWorkbook)
    {
        var errors = new List<string>();
        if (StateFiscalYear is < 2000 or > 2100) errors.Add("stateFiscalYear must be a year, e.g. 2027 for September 2026 to August 2027");
        if (forWorkbook && string.IsNullOrWhiteSpace(McoName)) errors.Add("mcoName is required for the workbook");
        if (McoName is { Length: > 200 }) errors.Add("mcoName is longer than 200 characters");
        if (McoName is not null && Regex.IsMatch(McoName, @"\p{Cc}")) errors.Add("mcoName has control characters");
        if (MemberMonths < 0) errors.Add("memberMonths cannot be negative");
        foreach (var (name, value) in NonNegative())
            if (value < 0) errors.Add($"{name} cannot be negative (enter \"Less:\" lines as positive amounts)");
        return errors;
    }

    private IEnumerable<(string, decimal)> NonNegative() =>
    [
        ("incurredClaims", IncurredClaims), ("providerWithholds", ProviderWithholds),
        ("coordinationOfBenefitsRecoverable", CoordinationOfBenefitsRecoverable), ("subrogationRecoveries", SubrogationRecoveries),
        ("providerIncentives", ProviderIncentives), ("fraudReductionRecoveries", FraudReductionRecoveries),
        ("stateDirectedPayments", StateDirectedPayments), ("otherAdditions", OtherAdditions),
        ("overpaymentRecoveries", OverpaymentRecoveries), ("prescriptionDrugRebates", PrescriptionDrugRebates),
        ("otherSubtractions", OtherSubtractions), ("claimsAssumedByAnotherEntity", ClaimsAssumedByAnotherEntity),
        ("qualityImprovement", QualityImprovement), ("fraudPrevention", FraudPrevention),
        ("grossRevenue", GrossRevenue ?? 0m), ("otherPremiumRevenue", OtherPremiumRevenue),
        ("taxesAndFees", TaxesAndFees), ("experienceRebate", ExperienceRebate),
    ];
}

/// <summary>One input cell of the MLR Report sheet and the amount that goes in it.</summary>
public sealed record TexasMlrLine(string Cell, string Line, string Label, decimal Amount);

public sealed record TexasMlrFigures(
    int StateFiscalYear,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    int MemberMonths,
    IReadOnlyList<TexasMlrLine> Lines,
    decimal Numerator,
    decimal Denominator,
    MlrResult Mlr);

/// <summary>
/// HHSC's MLR Report (UMCM 5.3.13.2 v2.4, "MLR Report" sheet): which line
/// goes in which cell, and the same arithmetic as the sheet's formulas
/// (G24 numerator, G30 denominator, G34 MLR, G37 adjusted MLR), with the
/// partial credibility adjustment (G36) from <see cref="MedicaidMlrCalculator"/>.
/// </summary>
public static class TexasMlrReport
{
    public const string Sheet = "MLR Report";
    public const string TemplateVersion = "2.4";

    /// <summary>September 1 of the year before through August 31.</summary>
    public static (DateOnly Start, DateOnly End) FiscalYear(int sfy) => (new DateOnly(sfy - 1, 9, 1), new DateOnly(sfy, 8, 31));

    public static DateOnly TexasToday(DateTime utcNow)
    {
        var central = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), central));
    }

    public static TexasMlrFigures Compute(TexasMlrRequest r, decimal capitationReceived, int expectedMemberMonths, MlrStandard standard)
    {
        var (start, end) = FiscalYear(r.StateFiscalYear);
        var memberMonths = r.MemberMonths ?? expectedMemberMonths;
        var gross = r.GrossRevenue ?? capitationReceived + r.OtherPremiumRevenue;

        var adjustedIncurred = r.IncurredClaims
            + r.ProviderWithholds - r.CoordinationOfBenefitsRecoverable - r.SubrogationRecoveries
            + r.ProviderIncentives + r.FraudReductionRecoveries + r.StateDirectedPayments + r.OtherAdditions
            - r.OverpaymentRecoveries - r.PrescriptionDrugRebates - r.OtherSubtractions
            + r.SolvencyFundsNet + r.ClaimsAssumedByAnotherEntity;

        // The sheet's denominator is G26 - G27 - G28 + G29; the calculator takes
        // premium less taxes, so the rebate and 3.4 adjustment go with premium.
        var mlr = MedicaidMlrCalculator.Calculate(new MlrInputs
        {
            IncurredClaims = adjustedIncurred,
            QualityImprovement = r.QualityImprovement,
            FraudPrevention = r.FraudPrevention,
            PremiumRevenue = gross - r.ExperienceRebate + r.OtherDenominatorAdjustment,
            TaxesAndFees = r.TaxesAndFees,
            MemberMonths = memberMonths,
        }, standard);

        var notes = mlr.Notes.ToList();
        if (r.FraudReductionRecoveries > 0)
            notes.Add("2.2 e: fraud reduction recoveries count only up to the MCO's fraud reduction expenses (42 CFR 438.8(e)(2)(iii)(B)); CHO does not check that limit");
        if (r.GrossRevenue is null)
            notes.Add("3.1 is the state capitation on recorded 820s for the fiscal year plus other premium revenue given; HHSC asks for gross revenue per the FSR, so compare before filing");

        TexasMlrLine L(string cell, string line, string label, decimal amount) => new(cell, line, label, amount);
        var lines = new List<TexasMlrLine>
        {
            L("G6", "1", "Number of member months", memberMonths),
            L("G8", "2.1", "Incurred claims (incl Rx, excl QI), per FSR", r.IncurredClaims),
            L("G10", "2.2 a", "Withholds from payments to network providers", r.ProviderWithholds),
            L("G11", "2.2 b", "Less: claims recoverable for anticipated coordination of benefits", r.CoordinationOfBenefitsRecoverable),
            L("G12", "2.2 c", "Less: subrogation recoveries", r.SubrogationRecoveries),
            L("G13", "2.2 d", "Incentive and bonus payments to network providers", r.ProviderIncentives),
            L("G14", "2.2 e", "Claims payments recovered through fraud reduction", r.FraudReductionRecoveries),
            L("G15", "2.2 f", "State directed payments to providers", r.StateDirectedPayments),
            L("G16", "2.2 g", "Other CFR-specified additions", r.OtherAdditions),
            L("G17", "2.2 h", "Less: overpayment recoveries from network providers", r.OverpaymentRecoveries),
            L("G18", "2.2 i", "Less: prescription drug rebates", r.PrescriptionDrugRebates),
            L("G19", "2.2 j", "Less: other CFR-specified subtractions", r.OtherSubtractions),
            L("G20", "2.2 k", "Net payments or receipts for state mandated solvency funds", r.SolvencyFundsNet),
            L("G21", "2.2 l", "Incurred claims later assumed by another entity", r.ClaimsAssumedByAnotherEntity),
            L("G22", "2.3", "Quality improvement", r.QualityImprovement),
            // The counted amount: anything over the 0.5% cap is not entered.
            L("G23", "2.4", "Fraud prevention activities (counted)", mlr.FraudPreventionCounted),
            L("G26", "3.1", "Gross revenues, per FSR", gross),
            L("G27", "3.2", "Less: taxes, licensing and regulatory fees", r.TaxesAndFees),
            L("G28", "3.3", "Less: experience rebate", r.ExperienceRebate),
            L("G29", "3.4", "Other CFR-specified adjustment to premiums", r.OtherDenominatorAdjustment),
            L("G36", "5.1", "Partial credibility adjustment", mlr.CredibilityAdjustment),
        };

        return new TexasMlrFigures(r.StateFiscalYear, start, end, memberMonths, lines, mlr.Numerator, mlr.AdjustedPremiumRevenue,
            mlr with { Notes = notes });
    }

    /// <summary>
    /// Labels and formulas the sheet must have for the cell map above to be
    /// right. A template HHSC has changed fails here rather than being filled
    /// in the wrong cells.
    /// </summary>
    private static readonly (string Cell, string Text)[] ExpectedLabels =
    [
        ("A1", "MCO Medical Loss Ratio (MLR) Report"),
        ("A6", "1. Number of member months"),
        ("B8", "Medical expenses"),
        ("C10", "Certain withholds from payments made to network providers"),
        ("C11", "Less: Certain claims that are recoverable for anticipated coordination of benefits"),
        ("C12", "Less: Certain claims payment recoveries received as a result of subrogation"),
        ("C13", "Certain incentive and bonus payments"),
        ("C14", "Certain claims payments recovered through fraud reduction efforts"),
        ("C15", "Certain State directed payments"),
        ("C16", "Other CFR-specified additions to incurred claims"),
        ("C17", "Less: Overpayment recoveries"),
        ("C18", "Less: Prescription drug rebates"),
        ("C19", "Less: Other CFR-specified subtractions"),
        ("C20", "Certain net payments or receipts related to State mandated solvency funds"),
        ("C21", "Incurred claims paid by one MCO that are later assumed by another entity"),
        ("B22", "Certain expenditures that improve health care quality"),
        ("B23", "Certain expenditures on certain \"fraud prevention activities.\""),
        ("B26", "Gross revenues"),
        ("B27", "Less: Taxes, licensing and regulatory fees"),
        ("B28", "Less: Experience rebate"),
        ("B29", "Any other CFR-specified adjustment"),
        ("B36", "Partial Credibility Adjustment"),
    ];

    private static readonly (string Cell, string Formula)[] ExpectedFormulas =
    [
        ("G24", "SUM(G8:G10)-SUM(G11:G12)+SUM(G13:G16)-SUM(G17:G19)+SUM(G20:G23)"),
        ("G30", "+G26-G27-G28+G29"),
        ("G32", "+G24"),
        ("G33", "+G30"),
        ("G34", "IF(G33=0,0,+G32/G33)"),
        ("G37", "+G34+G36"),
    ];

    /// <summary>Why this workbook is not the MLR Report layout the cell map is for; empty when it is.</summary>
    public static List<string> CheckLayout(XlsxTemplate workbook)
    {
        if (!workbook.SheetNames.Contains(Sheet))
            return [$"The workbook has no '{Sheet}' sheet; upload HHSC's UMCM 5.3.13.2 MLR Report Template"];

        var problems = new List<string>();
        foreach (var (cell, text) in ExpectedLabels)
        {
            var actual = Normalize(workbook.Text(Sheet, cell));
            if (!actual.StartsWith(Normalize(text), StringComparison.OrdinalIgnoreCase))
                problems.Add($"{Sheet}!{cell} should start \"{text}\" but is \"{Truncate(actual)}\"");
        }
        foreach (var (cell, formula) in ExpectedFormulas)
        {
            var actual = workbook.Formula(Sheet, cell);
            if (!string.Equals(actual?.Replace(" ", ""), formula, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{Sheet}!{cell} should have the formula ={formula} but has {(actual is null ? "none" : "=" + actual)}");
        }
        return problems;
    }

    /// <summary>The template version on its Document History sheet ("Version 2.4"), when it has one.</summary>
    public static string? Version(XlsxTemplate workbook) =>
        workbook.SheetNames.Contains("Document History")
        && Normalize(workbook.Text("Document History", "D3")) is var v && v.StartsWith("Version ", StringComparison.OrdinalIgnoreCase)
            ? v["Version ".Length..].Trim()
            : null;

    /// <summary>Writes the header and every input line, and caches the sheet's own totals.</summary>
    public static byte[] Fill(XlsxTemplate workbook, TexasMlrFigures figures, string mcoName, DateOnly submissionDate)
    {
        workbook.SetText(Sheet, "D2", mcoName.Trim());
        workbook.SetNumber(Sheet, "D3", figures.StateFiscalYear);
        workbook.SetDate(Sheet, "D4", submissionDate);
        workbook.SetDate(Sheet, "F5", figures.PeriodEnd);
        foreach (var line in figures.Lines)
            workbook.SetNumber(Sheet, line.Cell, line.Amount);

        var mlr = figures.Denominator == 0 ? 0m : figures.Numerator / figures.Denominator;
        workbook.SetCachedValue(Sheet, "G24", figures.Numerator);
        workbook.SetCachedValue(Sheet, "G30", figures.Denominator);
        workbook.SetCachedValue(Sheet, "G32", figures.Numerator);
        workbook.SetCachedValue(Sheet, "G33", figures.Denominator);
        workbook.SetCachedValue(Sheet, "G34", Math.Round(mlr, 10));
        workbook.SetCachedValue(Sheet, "G37", Math.Round(mlr + figures.Mlr.CredibilityAdjustment, 10));
        CacheAttestation(workbook, mcoName.Trim(), figures, submissionDate);
        return workbook.Save();
    }

    /// <summary>
    /// The Attestation sheet repeats the header by formula. Excel recalculates
    /// on open, but previewers show the cached results, so they are cached
    /// too, for each cell whose formula is the one expected.
    /// </summary>
    private static void CacheAttestation(XlsxTemplate workbook, string mcoName, TexasMlrFigures figures, DateOnly submissionDate)
    {
        const string attestation = "Attestation";
        if (!workbook.SheetNames.Contains(attestation)) return;
        bool Is(string cell, string formula) =>
            string.Equals(workbook.Formula(attestation, cell)?.Replace(" ", ""), formula.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        static decimal Serial(DateOnly d) => d.DayNumber - new DateOnly(1899, 12, 30).DayNumber;

        if (Is("B3", "+'MLR Report'!D2")) workbook.SetCachedText(attestation, "B3", mcoName);
        if (Is("B20", "+B3")) workbook.SetCachedText(attestation, "B20", mcoName);
        if (Is("B6", "+'MLR Report'!A1")) workbook.SetCachedText(attestation, "B6", workbook.Text(Sheet, "A1") ?? "");
        if (Is("C9", "+'MLR Report'!F5")) workbook.SetCachedValue(attestation, "C9", Serial(figures.PeriodEnd));
        if (Is("H9", "+'MLR Report'!F3")) workbook.SetCachedText(attestation, "H9", workbook.Text(Sheet, "F3") ?? "");
        if (Is("C10", "+'MLR Report'!D4")) workbook.SetCachedValue(attestation, "C10", Serial(submissionDate));
        if (Is("H10", "+'MLR Report'!D3")) workbook.SetCachedValue(attestation, "H10", figures.StateFiscalYear);
        if (Is("C11", "+'MLR Report'!D5")) workbook.SetCachedText(attestation, "C11", workbook.Text(Sheet, "D5") ?? "");
    }

    private static string Normalize(string? text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();

    private static string Truncate(string text) => text.Length <= 60 ? text : text[..60] + "…";
}
