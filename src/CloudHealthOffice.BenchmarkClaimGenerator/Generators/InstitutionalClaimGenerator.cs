using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Generators;

/// <summary>
/// Generates UB-04 institutional claims including inpatient DRG, outpatient per diem,
/// emergency department, observation stays, stop-loss/outlier, and skilled nursing facility.
/// </summary>
public class InstitutionalClaimGenerator : IClaimGenerator
{
    private readonly IReferenceDataProvider _refData;

    /// <summary>
    /// Initializes a new instance of the <see cref="InstitutionalClaimGenerator"/> class.
    /// </summary>
    /// <param name="refData">Reference data provider for codes and entities.</param>
    public InstitutionalClaimGenerator(IReferenceDataProvider refData)
    {
        _refData = refData;
    }

    /// <inheritdoc />
    public string ClaimType => "Institutional";

    /// <inheritdoc />
    public SyntheticClaim Generate(int sequenceNumber, string subType, Random random)
    {
        var member = _refData.GenerateMember(random);
        var renderingProvider = _refData.GenerateProvider(random);
        var billingProvider = _refData.GenerateProvider(random);
        var admitDate = new DateTime(2024, 1, 1).AddDays(random.Next(365));
        var diagContext = subType == "emergency" ? "emergency" : "general";

        var los = GetLengthOfStay(subType, random);
        var dischargeDate = admitDate.AddDays(los);
        var lines = GenerateLines(random, subType, admitDate, los);
        var totalCharges = lines.Sum(l => l.ChargeAmount * l.Units);

        string? drgCode = null;
        string? billType = GetBillType(subType);

        if (subType is "inpatient" or "stoploss")
        {
            var drg = _refData.GetDrgCode(random);
            drgCode = drg.Code;
        }

        var claim = new SyntheticClaim
        {
            ClaimId = $"MCC-I-{sequenceNumber:D7}",
            ClaimType = ClaimType,
            DateOfService = admitDate,
            DateReceived = dischargeDate.AddDays(random.Next(1, 14)),
            Member = member,
            RenderingProvider = renderingProvider,
            BillingProvider = billingProvider,
            PlaceOfService = _refData.GetPlaceOfServiceCode(random, subType),
            BenefitPlanId = member.PlanId,
            Lines = lines,
            PrimaryDiagnosisCode = _refData.GetDiagnosisCode(random, diagContext),
            SecondaryDiagnosisCodes = _refData.GetSecondaryDiagnosisCodes(random, random.Next(1, 5)).ToList(),
            FrequencyCode = "1",
            BillType = billType,
            DrgCode = drgCode,
            TotalCharges = totalCharges,
            PriorAuthStatus = subType == "inpatient" ? "OnFile" : "NotRequired",
            PriorAuthNumber = subType == "inpatient" ? $"AUTH-{random.Next(100000, 999999)}" : null,
            FhirResourceGenerated = true,
            PayerToPayerReady = true
        };

        claim.ExpectedOutcome = ComputeExpectedOutcome(claim, subType, random);
        return claim;
    }

    private List<ClaimLine> GenerateLines(Random random, string subType, DateTime admitDate, int los)
    {
        var lines = new List<ClaimLine>();
        var revCodeSubType = subType switch
        {
            "inpatient" or "stoploss" => "inpatient",
            "outpatient" => "outpatient",
            "emergency" => "emergency",
            "observation" => "observation",
            "skillednursing" => "skillednursing",
            _ => "outpatient"
        };

        var lineCount = subType switch
        {
            "inpatient" or "stoploss" => random.Next(5, 12),
            "emergency" => random.Next(3, 7),
            "observation" => random.Next(2, 5),
            "skillednursing" => random.Next(4, 8),
            _ => random.Next(2, 6)
        };

        for (int i = 0; i < lineCount; i++)
        {
            var (revCode, revDesc) = _refData.GetRevenueCode(random, revCodeSubType);
            var charge = GetChargeForSubType(subType, random);
            var units = IsPerDiemCode(revCode) ? los : 1;

            lines.Add(new ClaimLine
            {
                LineNumber = i + 1,
                ProcedureCode = revCode,
                Description = revDesc,
                RevenueCode = revCode,
                Units = units,
                ChargeAmount = charge,
                ServiceDate = admitDate,
                ServiceEndDate = admitDate.AddDays(los),
                DiagnosisPointers = new List<int> { 1 }
            });
        }

        return lines;
    }

    private static decimal GetChargeForSubType(string subType, Random random)
    {
        return subType switch
        {
            "inpatient" => 2500m + random.Next(0, 5000),
            "stoploss" => 8000m + random.Next(0, 20000),
            "emergency" => 800m + random.Next(0, 3000),
            "observation" => 500m + random.Next(0, 2000),
            "skillednursing" => 400m + random.Next(0, 1500),
            "outpatient" => 300m + random.Next(0, 2500),
            _ => 500m + random.Next(0, 1500)
        };
    }

    private static int GetLengthOfStay(string subType, Random random)
    {
        return subType switch
        {
            "inpatient" => random.Next(1, 14),
            "stoploss" => random.Next(10, 45),
            "emergency" => 0,
            "observation" => random.Next(0, 2),
            "skillednursing" => random.Next(7, 90),
            "outpatient" => 0,
            _ => 0
        };
    }

    private static string GetBillType(string subType)
    {
        return subType switch
        {
            "inpatient" or "stoploss" => "0111",
            "outpatient" => "0131",
            "emergency" => "0131",
            "observation" => "0131",
            "skillednursing" => "0211",
            _ => "0131"
        };
    }

    private static bool IsPerDiemCode(string revCode)
    {
        return revCode.StartsWith("01") || revCode.StartsWith("019");
    }

    /// <summary>
    /// A line's expected allowed amount: the plan-default revenue-code rate
    /// (<see cref="SyntheticFeeScheduleGenerator.RevenueCodeRates"/>, the schedule the MCC
    /// validator seeds as the validation plan's default) × units, which is how the
    /// FeeScheduleEngine prices a flat-rate revenue-code line for a provider with no
    /// contract. Lines are revenue-code-only (no DRG schedule is seeded), so a DRG on
    /// the claim does not change the allowed amount. A code with no rate keeps the
    /// earlier 60%-of-charges estimate.
    /// </summary>
    internal static decimal ExpectedLineAllowed(ClaimLine line)
    {
        var rate = SyntheticFeeScheduleGenerator.RevenueCodeRate(line.RevenueCode);
        return rate is { } perUnit
            ? Math.Round(perUnit * line.Units, 2)
            : Math.Round(line.ChargeAmount * line.Units * 0.60m, 2);
    }

    private static ExpectedOutcome ComputeExpectedOutcome(SyntheticClaim claim, string subType, Random random)
    {
        decimal copay = 0m;
        decimal coinsuranceRate = 0.20m;
        decimal deductible = 0m;

        // Allowed amounts follow the seeded plan-default fee schedule. The cost-share
        // terms below are generator estimates, not the validation plan's benefits, so
        // paid / member liability are illustrative and the MCC validator does not score
        // plain institutional claims on them.
        if (subType is "inpatient" or "stoploss" && claim.DrgCode != null)
        {
            copay = 350m;
            // Kept even though only the deductible uses it: the draw is part of the
            // seeded random sequence, so removing it would change every later claim.
            deductible = random.Next(100) < 40 ? 500m : 0m;
        }
        else if (subType == "emergency")
        {
            copay = 150m;
        }
        else if (subType != "skillednursing")
        {
            copay = 75m;
        }

        var lineAllowedAmounts = claim.Lines.ToDictionary(l => l.LineNumber, ExpectedLineAllowed);
        var totalAllowed = lineAllowedAmounts.Values.Sum();
        var coinsurance = Math.Round(totalAllowed * coinsuranceRate, 2);
        var paid = Math.Max(0, totalAllowed - copay - deductible - coinsurance);
        var memberLiability = copay + deductible + coinsurance;

        var lineOutcomes = claim.Lines.Select(l =>
        {
            var lineAllowed = lineAllowedAmounts[l.LineNumber];
            return new LineOutcome
            {
                LineNumber = l.LineNumber,
                Disposition = "Paid",
                AllowedAmount = lineAllowed,
                PaidAmount = Math.Round(lineAllowed * (1m - coinsuranceRate), 2),
                ReasonCode = null
            };
        }).ToList();

        return new ExpectedOutcome
        {
            Disposition = "Paid",
            ExpectedAllowedAmount = totalAllowed,
            ExpectedPaidAmount = Math.Round(paid, 2),
            ExpectedMemberLiability = Math.Round(memberLiability, 2),
            ExpectedCopay = copay,
            ExpectedCoinsurance = coinsurance,
            ExpectedDeductible = deductible,
            ExpectedDrgCode = claim.DrgCode,
            LineOutcomes = lineOutcomes,
            ExpectedFhirCompliant = true,
            ExpectedPriorAuthDecision = claim.PriorAuthStatus == "OnFile" ? "Approved" : "N/A"
        };
    }
}
