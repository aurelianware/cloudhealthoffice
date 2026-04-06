using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Generators;

/// <summary>
/// Generates ADA dental claims across preventive, restorative, endodontics,
/// periodontics, orthodontics, and oral surgery categories.
/// </summary>
public class DentalClaimGenerator : IClaimGenerator
{
    private readonly IReferenceDataProvider _refData;

    private static readonly string[] ToothNumbers =
    {
        "1", "2", "3", "4", "5", "6", "7", "8", "9", "10",
        "11", "12", "13", "14", "15", "16", "17", "18", "19", "20",
        "21", "22", "23", "24", "25", "26", "27", "28", "29", "30",
        "31", "32"
    };

    private static readonly string[] ToothSurfaces = { "M", "O", "D", "B", "L" };

    /// <summary>
    /// Initializes a new instance of the <see cref="DentalClaimGenerator"/> class.
    /// </summary>
    /// <param name="refData">Reference data provider for codes and entities.</param>
    public DentalClaimGenerator(IReferenceDataProvider refData)
    {
        _refData = refData;
    }

    /// <inheritdoc />
    public string ClaimType => "Dental";

    /// <inheritdoc />
    public SyntheticClaim Generate(int sequenceNumber, string subType, Random random)
    {
        var member = _refData.GenerateMember(random);
        var renderingProvider = _refData.GenerateProvider(random, "dental");
        var billingProvider = _refData.GenerateProvider(random, "dental");
        var serviceDate = new DateTime(2024, 1, 1).AddDays(random.Next(365));

        var lines = GenerateLines(random, subType, serviceDate);
        var totalCharges = lines.Sum(l => l.ChargeAmount * l.Units);

        var claim = new SyntheticClaim
        {
            ClaimId = $"MCC-D-{sequenceNumber:D7}",
            ClaimType = ClaimType,
            DateOfService = serviceDate,
            DateReceived = serviceDate.AddDays(random.Next(1, 21)),
            Member = member,
            RenderingProvider = renderingProvider,
            BillingProvider = billingProvider,
            PlaceOfService = "11",
            BenefitPlanId = member.PlanId,
            Lines = lines,
            PrimaryDiagnosisCode = _refData.GetDiagnosisCode(random, "dental"),
            SecondaryDiagnosisCodes = new List<string>(),
            TotalCharges = totalCharges,
            PriorAuthStatus = RequiresPriorAuth(subType) ? "OnFile" : "NotRequired",
            PriorAuthNumber = RequiresPriorAuth(subType) ? $"AUTH-{random.Next(100000, 999999)}" : null,
            FhirResourceGenerated = true,
            PayerToPayerReady = true
        };

        claim.ExpectedOutcome = ComputeExpectedOutcome(claim, subType, random);
        return claim;
    }

    private List<ClaimLine> GenerateLines(Random random, string subType, DateTime serviceDate)
    {
        var lines = new List<ClaimLine>();

        if (subType == "orthodontics")
        {
            // Orthodontic claims have a single high-value line
            var (code, desc, charge) = _refData.GetDentalCode(random, "orthodontics");
            lines.Add(new ClaimLine
            {
                LineNumber = 1,
                ProcedureCode = code,
                Description = desc,
                Units = 1,
                ChargeAmount = charge + random.Next(-200, 500),
                ServiceDate = serviceDate,
                DiagnosisPointers = new List<int> { 1 }
            });
            return lines;
        }

        var lineCount = subType switch
        {
            "preventive" => random.Next(1, 4),
            "restorative" => random.Next(1, 3),
            "endodontics" => random.Next(1, 3),
            "periodontics" => random.Next(1, 5),
            "oralsurgery" => random.Next(1, 3),
            _ => random.Next(1, 3)
        };

        for (int i = 0; i < lineCount; i++)
        {
            var (code, desc, charge) = _refData.GetDentalCode(random, subType);
            var tooth = NeedsTooth(subType) ? ToothNumbers[random.Next(ToothNumbers.Length)] : null;
            List<string>? surfaces = NeedsSurfaces(subType, code)
                ? Enumerable.Range(0, random.Next(1, 4))
                    .Select(_ => ToothSurfaces[random.Next(ToothSurfaces.Length)])
                    .Distinct()
                    .ToList()
                : null;

            lines.Add(new ClaimLine
            {
                LineNumber = i + 1,
                ProcedureCode = code,
                Description = desc,
                Units = 1,
                ChargeAmount = charge + random.Next(-20, 50),
                ServiceDate = serviceDate,
                ToothNumber = tooth,
                ToothSurfaces = surfaces,
                DiagnosisPointers = new List<int> { 1 }
            });
        }

        return lines;
    }

    private static ExpectedOutcome ComputeExpectedOutcome(SyntheticClaim claim, string subType, Random random)
    {
        // Dental: allowed = charges * 0.70 for most, orthodontics has lifetime max consideration
        var allowedRatio = subType == "orthodontics" ? 0.50m : 0.70m;
        var copay = 0m; // Most dental plans don't have copays
        var coinsuranceRate = subType switch
        {
            "preventive" => 0.0m,   // Preventive typically covered at 100%
            "restorative" => 0.20m, // Basic at 80%
            "endodontics" => 0.20m,
            "periodontics" => 0.20m,
            "orthodontics" => 0.50m, // Ortho at 50%
            "oralsurgery" => 0.20m,
            _ => 0.20m
        };
        var deductible = subType == "preventive" ? 0m : 50m;

        // 5% chance of denial for non-preventive
        var isDenied = subType != "preventive" && random.Next(100) < 5;

        var lineOutcomes = claim.Lines.Select(l =>
        {
            var lineAllowed = isDenied ? 0m : Math.Round(l.ChargeAmount * l.Units * allowedRatio, 2);
            var lineCoins = Math.Round(lineAllowed * coinsuranceRate, 2);
            var linePaid = Math.Max(0, lineAllowed - lineCoins);
            return new LineOutcome
            {
                LineNumber = l.LineNumber,
                Disposition = isDenied ? "Denied" : "Paid",
                AllowedAmount = lineAllowed,
                PaidAmount = linePaid,
                ReasonCode = isDenied ? "50" : null // CARC 50 = non-covered service
            };
        }).ToList();

        var totalAllowed = lineOutcomes.Sum(l => l.AllowedAmount);
        var totalCoinsurance = Math.Round(totalAllowed * coinsuranceRate, 2);
        var totalPaid = Math.Max(0, lineOutcomes.Sum(l => l.PaidAmount) - deductible);
        var memberLiability = deductible + totalCoinsurance;

        return new ExpectedOutcome
        {
            Disposition = isDenied ? "Denied" : "Paid",
            DenialReasonCode = isDenied ? "50" : null,
            ExpectedAllowedAmount = totalAllowed,
            ExpectedPaidAmount = Math.Round(totalPaid, 2),
            ExpectedMemberLiability = Math.Round(memberLiability, 2),
            ExpectedCopay = copay,
            ExpectedCoinsurance = totalCoinsurance,
            ExpectedDeductible = deductible,
            LineOutcomes = lineOutcomes,
            ExpectedFhirCompliant = true,
            ExpectedPriorAuthDecision = claim.PriorAuthStatus == "OnFile" ? "Approved" : "N/A"
        };
    }

    private static bool RequiresPriorAuth(string subType)
    {
        return subType is "orthodontics" or "oralsurgery";
    }

    private static bool NeedsTooth(string subType)
    {
        return subType is "restorative" or "endodontics" or "oralsurgery";
    }

    private static bool NeedsSurfaces(string subType, string code)
    {
        // Restorative amalgam and composite codes need surfaces
        return subType == "restorative" && (code.StartsWith("D21") || code.StartsWith("D23") || code.StartsWith("D24"));
    }
}
