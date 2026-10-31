using System.Globalization;
using BenefitPlanService.Models;
using BenefitPlanService.Models.Benefits;

namespace BenefitPlanService.Import.QhpPuf;

/// <summary>
/// Builds a CHO <see cref="BenefitPlan"/> from one Plan Attributes PUF row and
/// that plan's Benefits and Cost Sharing PUF rows. The plan id is the
/// 17-character HIOS plan id (standard component id plus CSR variant), which
/// is unique nationally and is what an 834 or a CPCDS coverage file from an
/// exchange plan carries.
///
/// <para>
/// The mapper never guesses silently. Where CHO's model is coarser than the
/// QHP template (one deductible flag per benefit, no separate drug
/// deductible, no second in-network tier, no per-day copay) it takes the
/// in-network tier 1 value and adds a warning to the result, so a reviewer
/// knows what to check before publishing the draft.
/// </para>
/// </summary>
public static class QhpPlanMapper
{
    public const string SourceName = "CMS QHP PUF";

    public static (BenefitPlan Plan, QhpPlanImportResult Result) Map(QhpRow attrs, IReadOnlyList<QhpRow> benefitRows)
    {
        var result = Describe(attrs);
        var warnings = result.Warnings;

        var year = int.TryParse(attrs["BusinessYear"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : DateTime.UtcNow.Year;

        var plan = new BenefitPlan
        {
            PlanId = result.PlanId,
            PlanName = result.PlanName,
            Payer = result.IssuerName.Length > 0 ? result.IssuerName : result.IssuerId,
            EffectiveDate = ParseDate(attrs["PlanEffectiveDate"]) ?? new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            TerminationDate = ParseDate(attrs["PlanExpirationDate"]) ?? new DateTime(year, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            PlanType = MapPlanType(attrs, warnings),
            MetalLevel = MapMetalLevel(attrs["MetalLevel"]),
            LineOfBusiness = attrs["MarketCoverage"].StartsWith("SHOP", StringComparison.OrdinalIgnoreCase)
                ? LineOfBusiness.Commercial
                : LineOfBusiness.Exchange,
            CostSharing = MapCostSharing(attrs, warnings),
            FamilyAccumulatorModel = FamilyAccumulatorModel.Embedded,
        };

        if (attrs.IsYes("MultipleInNetworkTiers"))
            warnings.Add("Plan has two in-network tiers; only tier 1 cost sharing was imported.");

        MapBenefits(plan, benefitRows, result);
        return (plan, result);
    }

    /// <summary>Plan identity fields for the report, without mapping benefits.</summary>
    public static QhpPlanImportResult Describe(QhpRow attrs)
    {
        var result = new QhpPlanImportResult
        {
            PlanId = attrs["PlanId"],
            StandardComponentId = attrs["StandardComponentId"],
            PlanName = attrs.First("PlanVariantMarketingName", "PlanMarketingName"),
            IssuerId = attrs["IssuerId"],
            IssuerName = attrs.First("IssuerMarketplaceMarketingName", "IssuerMarketPlaceMarketingName"),
            StateCode = attrs["StateCode"],
            BusinessYear = attrs["BusinessYear"],
            MetalLevel = attrs["MetalLevel"],
            CsrVariationType = attrs["CSRVariationType"],
        };

        foreach (var column in new[] { "NetworkId", "ServiceAreaId", "FormularyId", "HIOSProductId", "URLForSummaryofBenefitsCoverage", "PlanBrochure", "FormularyURL" })
        {
            var v = attrs[column];
            if (v.Length > 0 && v != "0") result.References[column] = v;
        }
        return result;
    }

    // ── Plan-level cost sharing ────────────────────────────────────────────

    private static CostSharing MapCostSharing(QhpRow a, List<string> warnings)
    {
        var dedIntegrated = a.IsYes("MedicalDrugDeductiblesIntegrated");
        var moopIntegrated = a.IsYes("MedicalDrugMaximumOutofPocketIntegrated");
        var ded = dedIntegrated ? "TEHBDed" : "MEHBDed";
        var moop = moopIntegrated ? "TEHB" : "MEHB";

        decimal? Dollars(params string[] columns)
        {
            foreach (var c in columns)
            {
                var v = QhpCostShareParser.ParseDollars(a[c]);
                if (v.HasValue) return v;
            }
            return null;
        }

        var indDed = Dollars($"{ded}InnTier1Individual");
        if (indDed is null && Dollars($"{ded}CombInnOonIndividual") is { } combDed)
        {
            indDed = combDed;
            warnings.Add($"Plan has one combined in- and out-of-network deductible (${combDed:0.##}); CHO tracks them separately, so it was set on both.");
        }
        var famDed = Dollars($"{ded}InnTier1FamilyPerGroup", $"{ded}InnTier1Family", $"{ded}CombInnOonFamilyPerGroup");
        var famPerPersonDed = Dollars($"{ded}InnTier1FamilyPerPerson");
        var oonIndDed = Dollars($"{ded}OutOfNetIndividual") ?? (indDed.HasValue && Dollars($"{ded}CombInnOonIndividual").HasValue ? indDed : null);
        var oonFamDed = Dollars($"{ded}OutOfNetFamilyPerGroup", $"{ded}OutOfNetFamily");

        var indMoop = Dollars($"{moop}InnTier1IndividualMOOP");
        if (indMoop is null && Dollars($"{moop}CombInnOonIndividualMOOP") is { } combMoop)
        {
            indMoop = combMoop;
            warnings.Add($"Plan has one combined in- and out-of-network out-of-pocket maximum (${combMoop:0.##}); CHO tracks them separately, so it was set on both.");
        }
        var famMoop = Dollars($"{moop}InnTier1FamilyPerGroupMOOP", $"{moop}InnTier1FamilyMOOP", $"{moop}CombInnOonFamilyPerGroupMOOP");
        var oonIndMoop = Dollars($"{moop}OutOfNetIndividualMOOP") ?? (indMoop.HasValue && Dollars($"{moop}CombInnOonIndividualMOOP").HasValue ? indMoop : null);
        var oonFamMoop = Dollars($"{moop}OutOfNetFamilyPerGroupMOOP", $"{moop}OutOfNetFamilyMOOP");

        if (!dedIntegrated && Dollars("DEHBDedInnTier1Individual") is { } drugDed && drugDed > 0)
            warnings.Add($"Plan has a separate ${drugDed:0.##} drug deductible; CHO has one deductible, so drug claims count against the medical deductible.");
        if (!moopIntegrated && Dollars("DEHBInnTier1IndividualMOOP") is { } drugMoop && drugMoop > 0)
            warnings.Add($"Plan has a separate ${drugMoop:0.##} drug out-of-pocket maximum; CHO has one, so drug cost sharing counts against the medical maximum.");
        if (famPerPersonDed.HasValue && indDed.HasValue && famPerPersonDed.Value != indDed.Value)
            warnings.Add($"Family per-person deductible (${famPerPersonDed:0.##}) differs from the individual deductible (${indDed:0.##}); CHO applies the individual amount to each family member.");

        var coins = QhpCostShareParser.ParseCoinsurance(a[$"{ded}InnTier1Coinsurance"]);

        return new CostSharing
        {
            Coinsurance = coins.Kind == QhpCostShareKind.Amount ? coins.Value!.Value : 0m,
            IndividualDeductible = indDed ?? 0m,
            InNetworkDeductible = indDed ?? 0m,
            FamilyDeductible = famDed ?? 0m,
            IndividualOutOfPocketMax = indMoop ?? 0m,
            InNetworkOutOfPocketMax = indMoop ?? 0m,
            FamilyOutOfPocketMax = famMoop ?? 0m,
            OutOfNetworkDeductible = oonIndDed ?? 0m,
            OutNetworkIndividualDeductible = oonIndDed,
            OutNetworkFamilyDeductible = oonFamDed,
            OutOfNetworkOutOfPocketMax = oonIndMoop ?? 0m,
            OutNetworkIndividualOutOfPocketMax = oonIndMoop,
            OutNetworkFamilyOutOfPocketMax = oonFamMoop,
        };
    }

    // ── Benefits ───────────────────────────────────────────────────────────

    private static void MapBenefits(BenefitPlan plan, IReadOnlyList<QhpRow> rows, QhpPlanImportResult result)
    {
        var byName = new Dictionary<string, QhpRow>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = QhpBenefitCategoryMap.Normalize(row["BenefitName"]);
            if (key.Length > 0) byName.TryAdd(key, row);
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var hmoLike = plan.PlanType is PlanType.HMO or PlanType.EPO;

        foreach (var target in QhpBenefitCategoryMap.Targets)
        {
            QhpRow? source = null;
            foreach (var name in target.QhpSources)
            {
                var key = QhpBenefitCategoryMap.Normalize(name);
                if (!byName.TryGetValue(key, out var row)) continue;
                if (source is null)
                {
                    source = row;
                    used.Add(key);
                }
                else
                {
                    used.Add(key);
                    result.FoldedBenefits.Add($"{row["BenefitName"]} (into {target.ChoCategory}, which uses {source["BenefitName"]})");
                }
            }

            if (source is null)
            {
                if (target.Kind != QhpBenefitCategoryMap.QhpBenefitKind.PharmacyTier)
                    result.CategoriesMissing.Add(target.ChoCategory);
                continue;
            }

            plan.Benefits.Add(BuildBenefit(target, source, hmoLike, result.Warnings));
            result.CategoriesFilled[target.ChoCategory] = source["BenefitName"];
        }

        foreach (var (key, row) in byName)
        {
            if (used.Contains(key)) continue;
            result.UnmappedBenefits.Add(new QhpUnmappedBenefit(
                row["BenefitName"], IsCovered(row), row["CopayInnTier1"], row["CoinsInnTier1"]));
        }
    }

    private static Benefit BuildBenefit(QhpBenefitCategoryMap.Target target, QhpRow r, bool hmoLike, List<string> warnings)
    {
        Benefit benefit = target.Kind switch
        {
            QhpBenefitCategoryMap.QhpBenefitKind.Pharmacy or QhpBenefitCategoryMap.QhpBenefitKind.PharmacyTier => new PharmacyBenefit
            {
                FormularyTier = target.FormularyTier ?? "Generic",
                IsSpecialtyDrug = target.FormularyTier == "Specialty Drug",
            },
            QhpBenefitCategoryMap.QhpBenefitKind.Preventive => new PreventiveBenefit(),
            _ => new MedicalBenefit(),
        };

        var name = r["BenefitName"];
        benefit.ServiceCategory = target.ChoCategory;
        benefit.Description = name;
        benefit.IsCovered = IsCovered(r);

        var copayIn = QhpCostShareParser.ParseCopay(r["CopayInnTier1"]);
        var coinsIn = QhpCostShareParser.ParseCoinsurance(r["CoinsInnTier1"]);
        var copayOon = QhpCostShareParser.ParseCopay(r["CopayOutofNet"]);
        var coinsOon = QhpCostShareParser.ParseCoinsurance(r["CoinsOutofNet"]);

        foreach (var cell in new[] { copayIn, coinsIn, copayOon, coinsOon })
        {
            if (cell.Kind == QhpCostShareKind.Unparsed)
                warnings.Add($"{target.ChoCategory}: could not read \"{cell.Raw}\" from {name}; left unset.");
            if (cell.Per is "day" or "stay" or "admission" && cell.Kind == QhpCostShareKind.Amount)
                warnings.Add($"{target.ChoCategory}: \"{cell.Raw}\" is per {cell.Per}; CHO applies it once per claim.");
        }

        benefit.InNetworkCopay = CopayValue(copayIn);
        benefit.InNetworkCoinsurance = CoinsuranceValue(coinsIn, copayIn);
        benefit.OutNetworkCopay = CopayValue(copayOon);
        benefit.OutNetworkCoinsurance = CoinsuranceValue(coinsOon, copayOon);

        var inAfter = copayIn.AfterDeductible || coinsIn.AfterDeductible;
        var oonAfter = copayOon.AfterDeductible || coinsOon.AfterDeductible;
        benefit.DeductibleApplies = inAfter;
        if (benefit.IsCovered && inAfter != oonAfter && IsStated(copayOon, coinsOon))
            warnings.Add($"{target.ChoCategory}: the deductible applies {(inAfter ? "in" : "out of")} network only; CHO uses the in-network rule for both.");

        // An HMO/EPO that states nothing out of network does not cover it.
        // CHO has no per-network coverage flag, so the member carries 100%
        // out of network; emergencies are priced in network by the engine.
        if (benefit.IsCovered && hmoLike && !IsStated(copayOon, coinsOon))
        {
            benefit.OutNetworkCopay = null;
            benefit.OutNetworkCoinsurance = 1.0m;
        }

        if (benefit.IsCovered && !IsStated(copayIn, coinsIn))
            warnings.Add($"{target.ChoCategory}: {name} states no in-network copay or coinsurance; imported with none.");

        benefit.OopApplies = !r.IsYes("IsExclFromInnMOOP");
        if (benefit is PreventiveBenefit preventive)
            preventive.IsAcaPreventive = copayIn.Kind is QhpCostShareKind.NoCharge or QhpCostShareKind.NotApplicable
                && coinsIn.Kind is QhpCostShareKind.NoCharge or QhpCostShareKind.NotApplicable
                && !inAfter;

        ApplyLimit(benefit, r);

        var notes = new[] { r["Explanation"], r["Exclusions"].Length > 0 ? "Exclusions: " + r["Exclusions"] : string.Empty }
            .Where(s => s.Length > 0)
            .ToList();
        if (notes.Count > 0 && string.IsNullOrEmpty(benefit.Limitations))
            benefit.Limitations = string.Join(" ", notes);
        else if (notes.Count > 0)
            benefit.Limitations += " " + string.Join(" ", notes);

        return benefit;
    }

    private static void ApplyLimit(Benefit benefit, QhpRow r)
    {
        if (!r.IsYes("QuantLimitOnSvc")) return;
        var qtyText = r["LimitQty"];
        var unit = r["LimitUnit"];
        if (!decimal.TryParse(qtyText.Replace(",", string.Empty).TrimStart('$'), NumberStyles.Number, CultureInfo.InvariantCulture, out var qty))
        {
            benefit.Limitations = $"Limit: {qtyText} {unit}".Trim() + ".";
            return;
        }

        var u = unit.ToLowerInvariant();
        if (u.Contains("dollar") && u.Contains("lifetime")) benefit.LifetimeMaximum = qty;
        else if (u.Contains("dollar") && u.Contains("year")) benefit.AnnualMaximum = qty;
        else if ((u.Contains("visit") || u.Contains("day") || u.Contains("treatment")) && u.Contains("year") && qty == decimal.Truncate(qty))
        {
            benefit.VisitLimit = (int)qty;
            benefit.VisitLimitPeriod = "Year";
            if (!u.Contains("visit")) benefit.Limitations = $"Limit: {qty:0} {unit} (counted as visits).";
        }
        else benefit.Limitations = $"Limit: {qty:0.##} {unit}.";
    }

    private static decimal? CopayValue(QhpCostShare copay) =>
        copay.Kind == QhpCostShareKind.Amount && copay.Value > 0 ? copay.Value : null;

    /// <summary>
    /// Coinsurance as a fraction. "No Charge" coinsurance is 0%; when the copay
    /// says "No Charge" and coinsurance is not stated, the service is free (0%).
    /// </summary>
    private static decimal? CoinsuranceValue(QhpCostShare coins, QhpCostShare copay) => coins.Kind switch
    {
        QhpCostShareKind.Amount => coins.Value,
        QhpCostShareKind.NoCharge => 0m,
        _ when copay.Kind == QhpCostShareKind.NoCharge => 0m,
        _ => null,
    };

    private static bool IsStated(QhpCostShare copay, QhpCostShare coins) =>
        copay.Kind is QhpCostShareKind.Amount or QhpCostShareKind.NoCharge
        || coins.Kind is QhpCostShareKind.Amount or QhpCostShareKind.NoCharge;

    private static bool IsCovered(QhpRow r)
    {
        var v = r["IsCovered"];
        if (v.Equals("Covered", StringComparison.OrdinalIgnoreCase)) return true;
        if (v.StartsWith("Not Covered", StringComparison.OrdinalIgnoreCase)) return false;
        // Blank: covered only when the row states some cost sharing.
        return IsStated(QhpCostShareParser.ParseCopay(r["CopayInnTier1"]), QhpCostShareParser.ParseCoinsurance(r["CoinsInnTier1"]));
    }

    // ── Plan attributes ────────────────────────────────────────────────────

    private static PlanType MapPlanType(QhpRow a, List<string> warnings)
    {
        var raw = a["PlanType"];
        var network = raw.ToUpperInvariant() switch
        {
            "HMO" => PlanType.HMO,
            "PPO" => PlanType.PPO,
            "EPO" => PlanType.EPO,
            "POS" => PlanType.POS,
            _ => (PlanType?)null,
        };
        if (network is null)
            warnings.Add($"Plan type \"{raw}\" has no CHO equivalent; imported as PPO.");

        if (a.IsYes("IsHSAEligible"))
        {
            warnings.Add($"HSA-eligible plan imported as HDHP; its network type ({raw}) is not kept on the plan type.");
            return PlanType.HDHP;
        }
        return network ?? PlanType.PPO;
    }

    private static MetalLevel? MapMetalLevel(string raw)
    {
        var v = raw.Trim().ToLowerInvariant();
        if (v.Contains("platinum")) return MetalLevel.Platinum;
        if (v.Contains("gold")) return MetalLevel.Gold;
        if (v.Contains("silver")) return MetalLevel.Silver;
        if (v.Contains("bronze")) return MetalLevel.Bronze;
        if (v.Contains("catastrophic")) return MetalLevel.Catastrophic;
        return null;
    }

    private static readonly string[] DateFormats =
        ["yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss", "M/d/yyyy H:mm", "M/d/yyyy h:mm:ss tt"];

    internal static DateTime? ParseDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "0") return null;
        if (DateTime.TryParseExact(raw.Trim(), DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
            return DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);
        return null;
    }
}
