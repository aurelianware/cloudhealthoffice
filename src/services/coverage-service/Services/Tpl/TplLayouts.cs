using CoverageService.Models.Tpl;

namespace CoverageService.Services.Tpl;

/// <summary>
/// Built-in TPL file layouts.
///
/// <list type="bullet">
///   <item><description><c>cho-csv</c>: CHO's own CSV, any state. Works today
///     for a plan that exports its state data to a spreadsheet.</description></item>
///   <item><description><c>tx-tpr</c>: Texas Medicaid third-party resource
///     data as HHSC / OIG Third Party Recoveries supplies it to MCOs.</description></item>
///   <item><description><c>ca-ohc</c>: Medi-Cal other health coverage from the
///     Medi-Cal Eligibility Record, which DHCS supplies to MCPs monthly
///     (APL 21-002 / APL 23-002).</description></item>
/// </list>
///
/// Neither state publishes its MCO record layout openly, so the Texas and
/// California layouts read a delimited file by header alias and carry the
/// code tables only. A plan holding the state's fixed-width spec uploads it as
/// a layout JSON (positions per field) and keeps these code tables. Both are
/// marked unverified until checked against the state's companion guide.
/// </summary>
public static class TplLayouts
{
    public const string ChoCsv = "cho-csv";
    public const string TexasTpr = "tx-tpr";
    public const string CaliforniaOhc = "ca-ohc";

    public static IReadOnlyDictionary<string, TplFileLayout> BuiltIn { get; } =
        new Dictionary<string, TplFileLayout>(StringComparer.OrdinalIgnoreCase)
        {
            [ChoCsv] = BuildChoCsv(),
            [TexasTpr] = BuildTexas(),
            [CaliforniaOhc] = BuildCalifornia(),
        };

    public static TplFileLayout? Find(string? id) =>
        id is not null && BuiltIn.TryGetValue(id, out var layout) ? layout : null;

    /// <summary>Header aliases shared by every delimited layout.</summary>
    private static Dictionary<TplField, TplFieldSpec> CommonFields() => new()
    {
        [TplField.MemberId] = TplFieldSpec.Alias(
            "member_id", "memberid", "member id", "medicaid_id", "medicaid id", "medicaid number",
            "medicaid_number", "client_id", "client number", "cin", "recipient_id", "subscriber_id"),
        [TplField.CarrierCode] = TplFieldSpec.Alias(
            "carrier_code", "carrier code", "carrier_id", "carrier id", "insurer_code", "tpr_carrier_id"),
        [TplField.CarrierName] = TplFieldSpec.Alias(
            "carrier_name", "carrier name", "carrier", "insurer", "insurance_company", "tpr_name", "payer_name"),
        [TplField.PolicyNumber] = TplFieldSpec.Alias(
            "policy_number", "policy number", "policy_no", "policy", "policy_id", "subscriber_number"),
        [TplField.GroupNumber] = TplFieldSpec.Alias(
            "group_number", "group number", "group_no", "group"),
        [TplField.PolicyholderName] = TplFieldSpec.Alias(
            "policyholder_name", "policyholder", "policy holder", "subscriber_name", "insured_name"),
        [TplField.CoverageCode] = TplFieldSpec.Alias(
            "coverage_code", "coverage code", "coverage_type", "coverage type", "ohc_code", "ohc code",
            "ohc", "tpr_coverage_type", "type_of_coverage"),
        [TplField.Scope] = TplFieldSpec.Alias("scope", "scope_of_coverage", "scope of coverage", "services"),
        [TplField.EffectiveDate] = TplFieldSpec.Alias(
            "effective_date", "effective date", "start_date", "begin_date", "coverage_start", "eff_date",
            "ohc_start_date"),
        [TplField.EndDate] = TplFieldSpec.Alias(
            "end_date", "end date", "term_date", "termination_date", "coverage_end", "stop_date",
            "ohc_end_date"),
        [TplField.RecordAction] = TplFieldSpec.Alias("action", "record_action", "transaction_type", "change_code"),
    };

    private static TplFileLayout BuildChoCsv() => new()
    {
        Id = ChoCsv,
        Description = "CHO TPL CSV: one row per member, carrier and coverage span. Pass state on the request.",
        Fields = CommonFields(),
        Codes = new(StringComparer.OrdinalIgnoreCase),
        Verified = true,
        VerificationNote = "CHO's own format; see docs/integration/MEDICAID-TPL.md.",
    };

    /// <summary>
    /// Texas. HHSC's MCO third-party resource layout is supplied to contracted
    /// MCOs and is not public, so this table maps the coverage-type words such
    /// files use, not the state's codes.
    /// </summary>
    private static TplFileLayout BuildTexas()
    {
        static TplCodeMapping M(TplScope scope, string description) => new()
        {
            Scope = scope, Description = description, ScopeNeedsVerification = true,
        };

        return new TplFileLayout
        {
            Id = TexasTpr,
            State = "TX",
            Description = "Texas Medicaid third-party resource (TPR) data for MCOs, delimited with a header.",
            Fields = CommonFields(),
            Codes = new(StringComparer.OrdinalIgnoreCase)
            {
                ["MAJOR MEDICAL"] = M(TplScope.Medical, "Major medical"),
                ["MEDICAL"] = M(TplScope.Medical, "Medical"),
                ["HOSPITAL"] = M(TplScope.Hospital, "Hospital only"),
                ["PHYSICIAN"] = M(TplScope.Professional, "Physician / professional only"),
                ["HMO"] = M(TplScope.Medical, "Commercial HMO"),
                ["PPO"] = M(TplScope.Medical, "Commercial PPO"),
                ["COMPREHENSIVE"] = M(TplScope.Comprehensive, "Comprehensive with drugs"),
                ["PHARMACY"] = M(TplScope.Pharmacy, "Pharmacy / drug"),
                ["RX"] = M(TplScope.Pharmacy, "Pharmacy / drug"),
                ["DENTAL"] = M(TplScope.Dental, "Dental"),
                ["VISION"] = M(TplScope.Vision, "Vision"),
                ["TRICARE"] = M(TplScope.Comprehensive, "TRICARE (TMPPM 8.3 lists it as a TPR)"),
                ["MEDICARE A"] = M(TplScope.MedicarePartA, "Medicare Part A"),
                ["MEDICARE B"] = M(TplScope.MedicarePartB, "Medicare Part B"),
                ["MEDICARE C"] = M(TplScope.MedicarePartC | TplScope.Medical, "Medicare Advantage"),
                ["MEDICARE ADVANTAGE"] = M(TplScope.MedicarePartC | TplScope.Medical, "Medicare Advantage"),
                ["MEDICARE D"] = M(TplScope.MedicarePartD | TplScope.Pharmacy, "Medicare Part D"),
                ["CASUALTY"] = M(TplScope.Casualty, "Accident / casualty (TMPPM 8.3)"),
                ["LONG TERM CARE"] = M(TplScope.LongTermCare, "Long-term care insurance"),
                ["LTC"] = M(TplScope.LongTermCare, "Long-term care insurance"),
                ["CANCER"] = new TplCodeMapping
                {
                    Skip = true,
                    Description = "Indemnity / disease-specific policy that pays cash, not services; " +
                                  "a TPR only when assignable (TMPPM 8.2). Recorded by hand when it is.",
                },
            },
            Verified = false,
            VerificationNote =
                "Columns are read by header alias and the code table maps coverage-type words. Check both " +
                "against the HHSC MCO third-party resource file layout (Uniform Managed Care Manual) before " +
                "loading production data; upload a fixed-width layout JSON if the state file is positional.",
            References =
            [
                "TMPPM Vol. 1 ch. 8, Third Party Liability (May 2026): 8.1 TPR definition, 8.3 TPR sources and " +
                "STAR Health exception, 8.6 TPL for managed care",
                "CMCS Informational Bulletin, 27 Aug 2021 (BBA 2018 / MSIAA 2019 cost-avoidance changes)",
            ],
        };
    }

    /// <summary>
    /// California. Medi-Cal OHC codes from DHCS MHSUDS Information Notice
    /// 17-058, which lists the cost-avoidance codes. The service-level
    /// crosswalk (APL 21-002 Appendix A) is not reproduced in the APL, so the
    /// scope of the less specific codes is CHO's reading and is flagged.
    /// </summary>
    private static TplFileLayout BuildCalifornia()
    {
        static TplCodeMapping M(TplScope scope, string description, bool verify = false) => new()
        {
            Scope = scope, Description = description, ScopeNeedsVerification = verify,
        };

        return new TplFileLayout
        {
            Id = CaliforniaOhc,
            State = "CA",
            Description = "Medi-Cal other health coverage (OHC) from the Medi-Cal Eligibility Record, delimited with a header.",
            Fields = CommonFields(),
            Codes = new(StringComparer.OrdinalIgnoreCase)
            {
                ["A"] = new TplCodeMapping
                {
                    Scope = TplScope.Comprehensive,
                    PayAndChaseOnly = true,
                    Description = "Pay and chase (any carrier): Medi-Cal pays, the plan recovers (APL 21-002).",
                },
                ["C"] = M(TplScope.Comprehensive, "Military benefits comprehensive"),
                ["D"] = M(TplScope.MedicarePartD | TplScope.Pharmacy, "Medicare Part D prescription drug coverage"),
                ["E"] = M(TplScope.Vision, "Vision plans"),
                ["F"] = M(TplScope.MedicarePartC | TplScope.Medical, "Medicare Part C health plan"),
                ["G"] = new TplCodeMapping { Skip = true, Description = "Medical parolee: an eligibility flag, not a coverage source." },
                ["H"] = M(TplScope.Comprehensive, "Multiple plans comprehensive"),
                ["I"] = new TplCodeMapping { Skip = true, Description = "Institutionalized: an eligibility flag, not a coverage source." },
                ["K"] = M(TplScope.Comprehensive, "Kaiser"),
                ["L"] = M(TplScope.Dental, "Dental only policies"),
                ["P"] = M(TplScope.Medical, "PPO/PHP/HMO/EPO not otherwise specified", verify: true),
                ["Q"] = M(TplScope.Pharmacy, "Commercial pharmacy plans"),
                ["V"] = M(TplScope.Comprehensive, "Any carrier other than the above (historical since 1 Jan 2017)", verify: true),
                ["V2"] = M(TplScope.Comprehensive, "Any carrier other than the above, includes multiple coverage", verify: true),
                ["W"] = M(TplScope.Hospital | TplScope.Professional, "Multiple plans non-comprehensive", verify: true),
                ["N"] = new TplCodeMapping { MeansNoCoverage = true, Description = "No other health coverage." },
            },
            Verified = false,
            VerificationNote =
                "OHC codes are from DHCS Information Notice 17-058. Columns are read by header alias; check " +
                "them against the DHCS MCP OHC/eligibility file layout, and check the P, V, V2 and W scopes " +
                "against APL 21-002 Appendix A, before loading production data.",
            References =
            [
                "DHCS MHSUDS Information Notice 17-058, Carrier and Provider Recoveries and Other Health Coverage Codes",
                "DHCS APL 21-002 / APL 23-002, Cost Avoidance and Post-Payment Recovery for Other Health Coverage",
            ],
        };
    }
}
