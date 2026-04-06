using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Generators;

/// <summary>
/// Generates CMS-1500 professional claims with realistic variation across
/// office visits, multi-line procedures, global surgery, bilateral, assistant
/// surgeon, telemedicine, and lab/pathology sub-types.
/// </summary>
public class ProfessionalClaimGenerator : IClaimGenerator
{
    private readonly IReferenceDataProvider _refData;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfessionalClaimGenerator"/> class.
    /// </summary>
    /// <param name="refData">Reference data provider for codes and entities.</param>
    public ProfessionalClaimGenerator(IReferenceDataProvider refData)
    {
        _refData = refData;
    }

    /// <inheritdoc />
    public string ClaimType => "Professional";

    /// <inheritdoc />
    public SyntheticClaim Generate(int sequenceNumber, string subType, Random random)
    {
        var member = _refData.GenerateMember(random);
        var renderingProvider = _refData.GenerateProvider(random);
        var billingProvider = _refData.GenerateProvider(random);
        var serviceDate = new DateTime(2024, 1, 1).AddDays(random.Next(365));
        var pos = _refData.GetPlaceOfServiceCode(random, subType);
        var diagnosisContext = subType == "behavioralhealth" ? "behavioral" :
                               subType is "surgical" or "globalsurgery" or "bilateral" or "assistantsurgeon" ? "surgical" :
                               "general";

        var lines = GenerateLines(random, subType, serviceDate);
        var totalCharges = lines.Sum(l => l.ChargeAmount * l.Units);
        var primaryDx = _refData.GetDiagnosisCode(random, diagnosisContext);
        var secondaryDxCount = random.Next(0, 4);

        var claim = new SyntheticClaim
        {
            ClaimId = $"MCC-P-{sequenceNumber:D7}",
            ClaimType = ClaimType,
            DateOfService = serviceDate,
            DateReceived = serviceDate.AddDays(random.Next(1, 30)),
            Member = member,
            RenderingProvider = renderingProvider,
            BillingProvider = billingProvider,
            PlaceOfService = pos,
            BenefitPlanId = member.PlanId,
            Lines = lines,
            PrimaryDiagnosisCode = primaryDx,
            SecondaryDiagnosisCodes = secondaryDxCount > 0
                ? _refData.GetSecondaryDiagnosisCodes(random, secondaryDxCount).ToList()
                : new List<string>(),
            TotalCharges = totalCharges,
            PriorAuthStatus = RequiresPriorAuth(subType) ? "OnFile" : "NotRequired",
            PriorAuthNumber = RequiresPriorAuth(subType) ? $"AUTH-{random.Next(100000, 999999)}" : null,
            FhirResourceGenerated = true,
            PayerToPayerReady = true
        };

        claim.ExpectedOutcome = ComputeExpectedOutcome(claim, random);
        return claim;
    }

    private List<ClaimLine> GenerateLines(Random random, string subType, DateTime serviceDate)
    {
        var lines = new List<ClaimLine>();

        switch (subType)
        {
            case "officevisit":
                var (code, desc, charge) = _refData.GetProcedureCode(random, "officevisit");
                lines.Add(new ClaimLine
                {
                    LineNumber = 1,
                    ProcedureCode = code,
                    Description = desc,
                    Units = 1,
                    ChargeAmount = charge + random.Next(-20, 50),
                    ServiceDate = serviceDate,
                    DiagnosisPointers = new List<int> { 1 }
                });
                break;

            case "multiline":
                var lineCount = random.Next(2, 6);
                for (int i = 0; i < lineCount; i++)
                {
                    var proc = i == 0
                        ? _refData.GetProcedureCode(random, "officevisit")
                        : _refData.GetProcedureCode(random, "surgical");
                    var mods = i > 0 ? _refData.GetModifiers(random, "multiprocedure").ToList() : new List<string>();
                    lines.Add(new ClaimLine
                    {
                        LineNumber = i + 1,
                        ProcedureCode = proc.Code,
                        Description = proc.Description,
                        Modifiers = mods,
                        Units = 1,
                        ChargeAmount = proc.BaseCharge + random.Next(-50, 100),
                        ServiceDate = serviceDate,
                        DiagnosisPointers = new List<int> { 1 }
                    });
                }
                break;

            case "globalsurgery":
                var surgProc = _refData.GetProcedureCode(random, "surgical");
                lines.Add(new ClaimLine
                {
                    LineNumber = 1,
                    ProcedureCode = surgProc.Code,
                    Description = surgProc.Description,
                    Modifiers = _refData.GetModifiers(random, "globalsurgery").ToList(),
                    Units = 1,
                    ChargeAmount = surgProc.BaseCharge + random.Next(-200, 500),
                    ServiceDate = serviceDate,
                    DiagnosisPointers = new List<int> { 1 }
                });
                break;

            case "bilateral":
                var bilProc = _refData.GetProcedureCode(random, "bilateral");
                lines.Add(new ClaimLine
                {
                    LineNumber = 1,
                    ProcedureCode = bilProc.Code,
                    Description = bilProc.Description,
                    Modifiers = new List<string> { "50" },
                    Units = 1,
                    ChargeAmount = bilProc.BaseCharge * 1.5m + random.Next(-100, 200),
                    ServiceDate = serviceDate,
                    DiagnosisPointers = new List<int> { 1 }
                });
                break;

            case "assistantsurgeon":
                var asstProc = _refData.GetProcedureCode(random, "assistantsurgeon");
                var asstMod = _refData.GetModifiers(random, "assistantsurgeon").ToList();
                lines.Add(new ClaimLine
                {
                    LineNumber = 1,
                    ProcedureCode = asstProc.Code,
                    Description = asstProc.Description,
                    Modifiers = asstMod,
                    Units = 1,
                    ChargeAmount = asstProc.BaseCharge * 0.20m + random.Next(-50, 100),
                    ServiceDate = serviceDate,
                    DiagnosisPointers = new List<int> { 1 }
                });
                break;

            case "telemedicine":
                var teleProc = _refData.GetProcedureCode(random, "telemedicine");
                lines.Add(new ClaimLine
                {
                    LineNumber = 1,
                    ProcedureCode = teleProc.Code,
                    Description = teleProc.Description,
                    Modifiers = new List<string> { "95" },
                    Units = 1,
                    ChargeAmount = teleProc.BaseCharge + random.Next(-10, 30),
                    ServiceDate = serviceDate,
                    PlaceOfService = "02",
                    DiagnosisPointers = new List<int> { 1 }
                });
                break;

            case "labpathology":
                var labLineCount = random.Next(1, 5);
                for (int i = 0; i < labLineCount; i++)
                {
                    var labProc = _refData.GetProcedureCode(random, "labpathology");
                    lines.Add(new ClaimLine
                    {
                        LineNumber = i + 1,
                        ProcedureCode = labProc.Code,
                        Description = labProc.Description,
                        Units = 1,
                        ChargeAmount = labProc.BaseCharge + random.Next(-5, 15),
                        ServiceDate = serviceDate,
                        PlaceOfService = "81",
                        DiagnosisPointers = new List<int> { 1 }
                    });
                }
                break;

            default:
                var defaultProc = _refData.GetProcedureCode(random, "officevisit");
                lines.Add(new ClaimLine
                {
                    LineNumber = 1,
                    ProcedureCode = defaultProc.Code,
                    Description = defaultProc.Description,
                    Units = 1,
                    ChargeAmount = defaultProc.BaseCharge,
                    ServiceDate = serviceDate,
                    DiagnosisPointers = new List<int> { 1 }
                });
                break;
        }

        return lines;
    }

    private static ExpectedOutcome ComputeExpectedOutcome(SyntheticClaim claim, Random random)
    {
        // V1 simplified pricing: allowed = charges * 0.65 for professional
        const decimal allowedRatio = 0.65m;
        var copay = 25m;
        var coinsuranceRate = 0.20m;
        var deductible = random.Next(100) < 30 ? 50m : 0m; // 30% chance deductible applies

        var lineOutcomes = new List<LineOutcome>();
        decimal totalAllowed = 0m;
        decimal totalPaid = 0m;

        foreach (var line in claim.Lines)
        {
            var lineAllowed = Math.Round(line.ChargeAmount * line.Units * allowedRatio, 2);
            var lineCoinsurance = Math.Round(lineAllowed * coinsuranceRate, 2);
            var linePaid = Math.Max(0, lineAllowed - lineCoinsurance);
            totalAllowed += lineAllowed;
            totalPaid += linePaid;

            lineOutcomes.Add(new LineOutcome
            {
                LineNumber = line.LineNumber,
                Disposition = "Paid",
                AllowedAmount = lineAllowed,
                PaidAmount = linePaid,
                ReasonCode = null
            });
        }

        totalPaid = Math.Max(0, totalPaid - copay - deductible);
        var memberLiability = copay + deductible + Math.Round(totalAllowed * coinsuranceRate, 2);

        return new ExpectedOutcome
        {
            Disposition = "Paid",
            ExpectedAllowedAmount = totalAllowed,
            ExpectedPaidAmount = totalPaid,
            ExpectedMemberLiability = memberLiability,
            ExpectedCopay = copay,
            ExpectedCoinsurance = Math.Round(totalAllowed * coinsuranceRate, 2),
            ExpectedDeductible = deductible,
            LineOutcomes = lineOutcomes,
            ExpectedFhirCompliant = true,
            ExpectedPriorAuthDecision = claim.PriorAuthStatus == "OnFile" ? "Approved" : "N/A"
        };
    }

    private static bool RequiresPriorAuth(string subType)
    {
        return subType is "globalsurgery" or "bilateral" or "assistantsurgeon";
    }
}
