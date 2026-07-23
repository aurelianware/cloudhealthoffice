using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Generators;

/// <summary>
/// Generates edge case claims that test complex adjudication pathways including
/// COB, retro-eligibility, newborn, prior authorization, subrogation,
/// behavioral health, and Medicaid subprogram scenarios.
/// </summary>
public class EdgeCaseClaimGenerator : IClaimGenerator
{
    private readonly IReferenceDataProvider _refData;

    /// <summary>
    /// Initializes a new instance of the <see cref="EdgeCaseClaimGenerator"/> class.
    /// </summary>
    /// <param name="refData">Reference data provider for codes and entities.</param>
    public EdgeCaseClaimGenerator(IReferenceDataProvider refData)
    {
        _refData = refData;
    }

    /// <inheritdoc />
    public string ClaimType => "EdgeCase";

    /// <inheritdoc />
    public SyntheticClaim Generate(int sequenceNumber, string subType, Random random)
    {
        var scenario = ParseScenario(subType);
        var member = GenerateMemberForScenario(scenario, random);
        var renderingProvider = _refData.GenerateProvider(random);
        var billingProvider = _refData.GenerateProvider(random);
        var serviceDate = new DateTime(2024, 1, 1).AddDays(random.Next(365));

        var diagContext = GetDiagnosisContext(scenario);
        var procSubType = GetProcedureSubType(scenario);
        var proc = _refData.GetProcedureCode(random, procSubType);

        var lines = new List<ClaimLine>
        {
            new()
            {
                LineNumber = 1,
                ProcedureCode = proc.Code,
                Description = proc.Description,
                Units = 1,
                ChargeAmount = ChargeWithVariance(proc.BaseCharge, random, -50, 100),
                ServiceDate = serviceDate,
                DiagnosisPointers = new List<int> { 1 }
            }
        };

        // Add additional lines for complex scenarios
        if (IsMultiLineScenario(scenario))
        {
            var proc2 = GetDistinctProcedureCode(random, procSubType, proc.Code);
            lines.Add(new ClaimLine
            {
                LineNumber = 2,
                ProcedureCode = proc2.Code,
                Description = proc2.Description,
                Units = 1,
                ChargeAmount = ChargeWithVariance(proc2.BaseCharge, random, -30, 60),
                ServiceDate = serviceDate,
                DiagnosisPointers = new List<int> { 1 }
            });
        }

        var totalCharges = lines.Sum(l => l.ChargeAmount * l.Units);
        var priorAuth = GetPriorAuthForScenario(scenario, random);

        var claim = new SyntheticClaim
        {
            ClaimId = $"MCC-E-{sequenceNumber:D7}",
            ClaimType = ClaimType,
            EdgeCase = scenario,
            DateOfService = serviceDate,
            DateReceived = serviceDate.AddDays(random.Next(1, 30)),
            Member = member,
            RenderingProvider = renderingProvider,
            BillingProvider = billingProvider,
            PlaceOfService = GetPlaceOfService(scenario),
            BenefitPlanId = member.PlanId,
            Lines = lines,
            PrimaryDiagnosisCode = _refData.GetDiagnosisCode(random, diagContext),
            SecondaryDiagnosisCodes = _refData.GetSecondaryDiagnosisCodes(random, random.Next(0, 3)).ToList(),
            TotalCharges = totalCharges,
            PriorAuthStatus = priorAuth.Status,
            PriorAuthNumber = priorAuth.Number,
            FhirResourceGenerated = true,
            PayerToPayerReady = true
        };

        ApplyScenarioMemberContext(claim, scenario);
        ApplyScenarioCoverageWindow(claim, scenario);
        ApplyScenarioRelatedCauses(claim, scenario, random);
        claim.ExpectedOutcome = ComputeExpectedOutcome(claim, scenario, random);
        return claim;
    }

    private static void ApplyScenarioRelatedCauses(SyntheticClaim claim, EdgeCaseScenario scenario, Random random)
    {
        // X12 837 CLM11-1: AA=Auto Accident, EM=Employment, OA=Other Accident.
        // Any related-causes code signals potential third-party liability that
        // requires subrogation investigation before the claim can pay.
        claim.RelatedCausesCode = scenario switch
        {
            EdgeCaseScenario.SubrogationAccidentRelated => "AA",
            EdgeCaseScenario.SubrogationThirdPartyLiability => "OA",
            EdgeCaseScenario.SubrogationWorkersComp => "EM",
            _ => null
        };

        if (claim.RelatedCausesCode is not null)
        {
            claim.AccidentDate = claim.DateOfService.Date.AddDays(-random.Next(1, 30));
        }
    }

    private static void ApplyScenarioMemberContext(SyntheticClaim claim, EdgeCaseScenario scenario)
    {
        if (scenario is not (
            EdgeCaseScenario.NewbornAutoAdjudication or
            EdgeCaseScenario.NewbornMotherClaimLink or
            EdgeCaseScenario.NewbornFirstThirtyDays))
        {
            return;
        }

        claim.Member.DateOfBirth = scenario switch
        {
            EdgeCaseScenario.NewbornAutoAdjudication => claim.DateOfService.Date.AddDays(-2),
            EdgeCaseScenario.NewbornMotherClaimLink => claim.DateOfService.Date.AddDays(-5),
            EdgeCaseScenario.NewbornFirstThirtyDays => claim.DateOfService.Date.AddDays(-29),
            _ => claim.Member.DateOfBirth
        };
        claim.Member.Relationship = "Child";
        claim.Member.RelationshipCode = "19";
        claim.Member.IsSubscriber = false;
        claim.PriorAuthStatus = "OnFile";
        claim.PriorAuthNumber = $"NB-AUTH-{claim.ClaimId}";
    }

    private static void ApplyScenarioCoverageWindow(SyntheticClaim claim, EdgeCaseScenario scenario)
    {
        var serviceDate = claim.DateOfService.Date;

        if (scenario is EdgeCaseScenario.RetroEligibilityTermination)
        {
            claim.Member.CoverageEffectiveDate = serviceDate.AddYears(-1);
            claim.Member.CoverageTermDate = serviceDate.AddDays(-30);
            claim.Member.EnrollmentStatus = "Terminated";
            claim.Member.MaintenanceTypeCode = "024";

            foreach (var coverage in claim.Member.Coverages)
            {
                coverage.EffectiveDate = claim.Member.CoverageEffectiveDate;
                coverage.TermDate = claim.Member.CoverageTermDate;
                coverage.Status = "Terminated";
                coverage.MaintenanceTypeCode = "024";
            }

            return;
        }

        if (scenario is EdgeCaseScenario.RetroEligibilityCoverageChange)
        {
            // A benefit-plan correction recorded today, effective two weeks ago:
            // the claim's own service date falls inside the retroactive window,
            // so the plan in force on the date of service can't be trusted
            // without reconciliation. X12 834 maintenance type code 001 = Change.
            claim.Member.CoverageEffectiveDate = serviceDate.AddYears(-1);
            claim.Member.PlanChangeEffectiveDate = serviceDate.AddDays(-14);
            claim.Member.MaintenanceTypeCode = "001";
        }
    }

    private SyntheticMember GenerateMemberForScenario(EdgeCaseScenario scenario, Random random)
    {
        var member = _refData.GenerateMember(random);

        switch (scenario)
        {
            case EdgeCaseScenario.NewbornAutoAdjudication:
            case EdgeCaseScenario.NewbornMotherClaimLink:
            case EdgeCaseScenario.NewbornFirstThirtyDays:
                member.Relationship = "Child";
                member.RelationshipCode = "19";
                member.IsSubscriber = false;
                member.Gender = random.Next(2) == 0 ? "M" : "F";
                break;

            case EdgeCaseScenario.CobBirthdayRule:
                member.Relationship = "Child";
                break;

            case EdgeCaseScenario.MedicaidDualEligible:
                member.DateOfBirth = new DateTime(1940 + random.Next(30), 1 + random.Next(12), 1 + random.Next(28));
                break;

            case EdgeCaseScenario.MedicaidSpendDown:
                member.DateOfBirth = new DateTime(1940 + random.Next(30), 1 + random.Next(12), 1 + random.Next(28));
                // "Medically needy" spend-down: the member must incur this much
                // in medical expense before Medicaid activates for the budget
                // period. Amount met is deliberately short of the liability so
                // the scenario always lands in the still-pending window.
                member.MedicaidSpendDownLiabilityAmount = 500m + random.Next(0, 1000);
                member.MedicaidSpendDownAmountMet =
                    member.MedicaidSpendDownLiabilityAmount.Value * (0.3m + (decimal)random.NextDouble() * 0.4m);
                break;
        }

        return member;
    }

    private static EdgeCaseScenario ParseScenario(string subType)
    {
        return Enum.Parse<EdgeCaseScenario>(subType);
    }

    private static string GetDiagnosisContext(EdgeCaseScenario scenario)
    {
        return scenario switch
        {
            EdgeCaseScenario.NewbornAutoAdjudication or
            EdgeCaseScenario.NewbornMotherClaimLink or
            EdgeCaseScenario.NewbornFirstThirtyDays => "newborn",

            EdgeCaseScenario.BehavioralHealthCarveOut or
            EdgeCaseScenario.BehavioralHealthCarveIn or
            EdgeCaseScenario.BehavioralHealthParityCheck => "behavioral",

            EdgeCaseScenario.SubrogationAccidentRelated => "emergency",

            _ => "general"
        };
    }

    private static string GetProcedureSubType(EdgeCaseScenario scenario)
    {
        return scenario switch
        {
            EdgeCaseScenario.BehavioralHealthCarveOut or
            EdgeCaseScenario.BehavioralHealthCarveIn or
            EdgeCaseScenario.BehavioralHealthParityCheck => "behavioralhealth",

            EdgeCaseScenario.SubrogationAccidentRelated => "surgical",

            _ => "officevisit"
        };
    }

    private static string GetPlaceOfService(EdgeCaseScenario scenario)
    {
        return scenario switch
        {
            EdgeCaseScenario.SubrogationAccidentRelated => "23",
            EdgeCaseScenario.BehavioralHealthCarveOut or
            EdgeCaseScenario.BehavioralHealthCarveIn or
            EdgeCaseScenario.BehavioralHealthParityCheck => "11",
            EdgeCaseScenario.NewbornAutoAdjudication or
            EdgeCaseScenario.NewbornMotherClaimLink or
            EdgeCaseScenario.NewbornFirstThirtyDays => "21",
            _ => "11"
        };
    }

    private static bool IsMultiLineScenario(EdgeCaseScenario scenario)
    {
        return scenario is
            EdgeCaseScenario.NewbornMotherClaimLink or
            EdgeCaseScenario.SubrogationAccidentRelated;
    }

    private (string Code, string Description, decimal BaseCharge) GetDistinctProcedureCode(
        Random random,
        string procSubType,
        string firstProcedureCode)
    {
        var candidate = _refData.GetProcedureCode(random, procSubType);
        for (var attempt = 0; attempt < 100 && candidate.Code == firstProcedureCode; attempt++)
        {
            candidate = _refData.GetProcedureCode(random, procSubType);
        }

        if (candidate.Code == firstProcedureCode)
        {
            throw new InvalidOperationException(
                $"Could not select a distinct procedure code for multi-line {procSubType} edge-case claim.");
        }

        return candidate;
    }

    private static decimal ChargeWithVariance(decimal baseCharge, Random random, int minVariance, int maxVariance)
        => Math.Max(1m, baseCharge + random.Next(minVariance, maxVariance));

    private static (string Status, string? Number) GetPriorAuthForScenario(EdgeCaseScenario scenario, Random random)
    {
        return scenario switch
        {
            EdgeCaseScenario.PriorAuthRequired_AuthOnFile =>
                ("OnFile", $"AUTH-{random.Next(100000, 999999)}"),
            EdgeCaseScenario.PriorAuthRequired_NoAuth =>
                ("Required", null),
            EdgeCaseScenario.PriorAuthRequired_ExpiredAuth =>
                ("Expired", $"AUTH-{random.Next(100000, 999999)}"),
            EdgeCaseScenario.PriorAuthRequired_WrongProvider =>
                ("OnFile", $"AUTH-{random.Next(100000, 999999)}"),
            EdgeCaseScenario.PriorAuthRequired_WrongProcedure =>
                ("OnFile", $"AUTH-{random.Next(100000, 999999)}"),
            _ => ("NotRequired", null)
        };
    }

    private static ExpectedOutcome ComputeExpectedOutcome(
        SyntheticClaim claim, EdgeCaseScenario scenario, Random random)
    {
        var (disposition, denialCode, allowedRatio) = GetScenarioOutcomeRules(scenario);

        var totalAllowed = Math.Round(claim.TotalCharges * allowedRatio, 2);
        var copay = disposition == "Paid" ? 25m : 0m;
        var coinsuranceRate = disposition == "Paid" ? 0.20m : 0m;
        var coinsurance = Math.Round(totalAllowed * coinsuranceRate, 2);
        var deductible = disposition == "Paid" && random.Next(100) < 30 ? 50m : 0m;
        var paid = disposition == "Paid"
            ? Math.Max(0, totalAllowed - copay - deductible - coinsurance)
            : 0m;
        var memberLiability = disposition == "Paid"
            ? copay + deductible + coinsurance
            : 0m;

        var priorAuthDecision = scenario switch
        {
            EdgeCaseScenario.PriorAuthRequired_AuthOnFile => "Approved",
            EdgeCaseScenario.PriorAuthRequired_NoAuth => "Denied",
            EdgeCaseScenario.PriorAuthRequired_ExpiredAuth => "Denied",
            EdgeCaseScenario.PriorAuthRequired_WrongProvider => "Denied",
            EdgeCaseScenario.PriorAuthRequired_WrongProcedure => "Denied",
            _ => "N/A"
        };

        var lineOutcomes = claim.Lines.Select(l =>
        {
            var lineAllowed = disposition == "Paid"
                ? Math.Round(l.ChargeAmount * l.Units * allowedRatio, 2)
                : 0m;
            var lineCoins = Math.Round(lineAllowed * coinsuranceRate, 2);
            return new LineOutcome
            {
                LineNumber = l.LineNumber,
                Disposition = disposition,
                AllowedAmount = lineAllowed,
                PaidAmount = disposition == "Paid" ? Math.Max(0, lineAllowed - lineCoins) : 0m,
                ReasonCode = denialCode
            };
        }).ToList();

        return new ExpectedOutcome
        {
            Disposition = disposition,
            DenialReasonCode = denialCode,
            ExpectedAllowedAmount = totalAllowed,
            ExpectedPaidAmount = Math.Round(paid, 2),
            ExpectedMemberLiability = Math.Round(memberLiability, 2),
            ExpectedCopay = copay,
            ExpectedCoinsurance = coinsurance,
            ExpectedDeductible = deductible,
            LineOutcomes = lineOutcomes,
            ExpectedFhirCompliant = true,
            ExpectedPriorAuthDecision = priorAuthDecision
        };
    }

    private static (string Disposition, string? DenialCode, decimal AllowedRatio) GetScenarioOutcomeRules(
        EdgeCaseScenario scenario)
    {
        return scenario switch
        {
            // COB — secondary/tertiary pend for COB review
            EdgeCaseScenario.CobPrimaryPayer => ("Paid", null, 0.65m),
            EdgeCaseScenario.CobSecondaryPayer => ("Pended", "22", 0.65m), // CARC 22 = COB
            EdgeCaseScenario.CobTertiaryPayer => ("Pended", "22", 0.65m),
            EdgeCaseScenario.CobBirthdayRule => ("Pended", "22", 0.65m),
            EdgeCaseScenario.CobGenderRule => ("Pended", "22", 0.65m),
            EdgeCaseScenario.CobMedicareSecondary => ("Paid", null, 0.45m),

            // Retro-elig
            EdgeCaseScenario.RetroEligibilityAdd => ("Paid", null, 0.65m),
            EdgeCaseScenario.RetroEligibilityTermination => ("Denied", "27", 0m), // CARC 27 = not covered
            EdgeCaseScenario.RetroEligibilityCoverageChange => ("Pended", "N527", 0.65m),

            // Newborn
            EdgeCaseScenario.NewbornAutoAdjudication => ("Paid", null, 0.65m),
            EdgeCaseScenario.NewbornMotherClaimLink => ("Paid", null, 0.65m),
            EdgeCaseScenario.NewbornFirstThirtyDays => ("Paid", null, 0.65m),

            // Prior auth
            EdgeCaseScenario.PriorAuthRequired_AuthOnFile => ("Paid", null, 0.65m),
            EdgeCaseScenario.PriorAuthRequired_NoAuth => ("Denied", "197", 0m), // CARC 197 = auth required
            EdgeCaseScenario.PriorAuthRequired_ExpiredAuth => ("Denied", "197", 0m),
            EdgeCaseScenario.PriorAuthRequired_WrongProvider => ("Denied", "197", 0m),
            EdgeCaseScenario.PriorAuthRequired_WrongProcedure => ("Denied", "197", 0m),

            // Subrogation — pend for review
            EdgeCaseScenario.SubrogationAccidentRelated => ("Pended", "W1", 0.65m),
            EdgeCaseScenario.SubrogationWorkersComp => ("Pended", "W1", 0.65m),
            EdgeCaseScenario.SubrogationThirdPartyLiability => ("Pended", "W1", 0.65m),

            // Behavioral health
            EdgeCaseScenario.BehavioralHealthCarveOut => ("Denied", "96", 0m), // CARC 96 = non-covered charge
            EdgeCaseScenario.BehavioralHealthCarveIn => ("Paid", null, 0.65m),
            EdgeCaseScenario.BehavioralHealthParityCheck => ("Paid", null, 0.65m),

            // Medicaid
            EdgeCaseScenario.MedicaidTANF => ("Paid", null, 0.55m),
            EdgeCaseScenario.MedicaidSSI => ("Paid", null, 0.55m),
            EdgeCaseScenario.MedicaidCHIP => ("Paid", null, 0.60m),
            EdgeCaseScenario.MedicaidDualEligible => ("Pended", "22", 0.55m),
            EdgeCaseScenario.MedicaidSpendDown => ("Pended", "N527", 0.55m),

            _ => ("Paid", null, 0.65m)
        };
    }
}
