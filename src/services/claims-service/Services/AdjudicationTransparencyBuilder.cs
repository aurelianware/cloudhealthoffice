using ClaimsService.Models;

namespace ClaimsService.Services;

public static class AdjudicationTransparencyBuilder
{
    public static AdjudicationTransparencyData? Build(Claim claim)
    {
        if (claim.AdjudicationResult is null && claim.PendDetails is null && !claim.ClaimLines.Any(l => l.AdjudicationResult is not null))
        {
            return null;
        }

        var data = new AdjudicationTransparencyData
        {
            Steps = BuildSteps(claim),
            NcciResults = BuildNcciResults(claim),
            FeeScheduleResults = BuildFeeScheduleResults(claim),
            BenefitCalculation = BuildBenefitCalculation(claim)
        };

        return data;
    }

    private static List<AdjudicationStep> BuildSteps(Claim claim)
    {
        var steps = new List<AdjudicationStep>
        {
            new()
            {
                StepNumber = 100,
                StepName = "Claim Intake",
                Status = "Passed",
                Timestamp = claim.ReceivedDate,
                Summary = $"Claim {claim.ClaimNumber} accepted for adjudication."
            },
            new()
            {
                StepNumber = 200,
                StepName = "Benefit Calculation",
                Status = claim.AdjudicationResult is null ? "Skipped" : "Passed",
                Summary = claim.AdjudicationResult is null
                    ? "No persisted benefit calculation projection is available for this claim."
                    : $"Allowed {claim.AdjudicationResult.AllowedAmount:C}; payer payment {claim.AdjudicationResult.PayerPayment:C}; member responsibility {claim.AdjudicationResult.PatientResponsibility:C}."
            }
        };

        if (claim.PendDetails is { } pendDetails)
        {
            steps.Add(new AdjudicationStep
            {
                StepNumber = 250,
                StepName = "Pend Review",
                Status = "Warning",
                Timestamp = pendDetails.PendedAt,
                Summary = $"{pendDetails.PendCode}: {pendDetails.PendReason ?? "Claim pended for review."}"
            });
        }

        steps.Add(new AdjudicationStep
        {
            StepNumber = 300,
            StepName = "Disposition",
            Status = ResolveDispositionStepStatus(claim),
            Timestamp = claim.AdjudicatedDate ?? claim.LastUpdatedDate,
            Summary = BuildDispositionSummary(claim),
            ErrorDetail = claim.AdjudicationResult?.DenialReason
        });

        steps.Add(new AdjudicationStep
        {
            StepNumber = 400,
            StepName = "Persistence",
            Status = "Passed",
            Timestamp = claim.LastUpdatedDate,
            Summary = $"Persisted claim status {claim.Status}."
        });

        return steps;
    }

    private static List<NcciEditResult> BuildNcciResults(Claim claim)
    {
        if (claim.PendDetails?.EditFailures is not { Count: > 0 } editFailures)
        {
            return new List<NcciEditResult>();
        }

        return editFailures.Select(edit => new NcciEditResult
        {
            EditCode = edit.RuleId,
            EditType = edit.EditType,
            Description = edit.Message ?? string.Empty,
            Passed = false,
            FailureReason = edit.SuggestedCarc is null ? null : $"Suggested CARC {edit.SuggestedCarc}",
            AffectedProcedureCode = edit.Column2Code ?? edit.Column1Code,
            AffectedModifier = edit.ModifierOverridePresent ? "Modifier override present" : null
        }).ToList();
    }

    private static List<FeeScheduleResult> BuildFeeScheduleResults(Claim claim)
    {
        var networkTier = claim.AdjudicationResult?.NetworkTier ?? "Not captured";

        return claim.ClaimLines
            .Where(line => line.AdjudicationResult is not null)
            .OrderBy(line => line.LineNumber)
            .Select(line => new FeeScheduleResult
            {
                ProcedureCode = line.ProcedureCode,
                Modifier = string.Join(",", line.Modifiers),
                FeeScheduleName = "Persisted adjudication projection",
                BilledAmount = line.ChargeAmount,
                AllowedAmount = line.AdjudicationResult!.AllowedAmount,
                ContractedRate = line.AdjudicationResult.AllowedAmount,
                RateBasis = line.MpipMultiplierApplied.HasValue ? "MPIP" : "PersistedProjection",
                RateMultiplier = line.MpipMultiplierApplied ?? 1m,
                NetworkTier = networkTier
            })
            .ToList();
    }

    private static BenefitCalculationResult? BuildBenefitCalculation(Claim claim)
    {
        var adjudication = claim.AdjudicationResult;
        if (adjudication is null)
        {
            return null;
        }

        return new BenefitCalculationResult
        {
            ServiceType = claim.ClaimType.ToString(),
            BenefitRuleApplied = adjudication.DenialReasonCode is { Length: > 0 }
                ? $"CARC {adjudication.DenialReasonCode}"
                : "Persisted adjudication projection",
            NetworkTier = adjudication.NetworkTier ?? "Not captured",
            AllowedAmount = adjudication.AllowedAmount,
            DeductibleApplied = adjudication.DeductibleAmount,
            CopayAmount = adjudication.CopayAmount,
            CoinsuranceAmount = adjudication.CoinsuranceAmount,
            PlanPayment = adjudication.PayerPayment,
            MemberResponsibility = adjudication.PatientResponsibility,
            DeductibleMet = false,
            OopMaxMet = false
        };
    }

    private static string ResolveDispositionStepStatus(Claim claim) =>
        claim.Status switch
        {
            ClaimStatus.Pended => "Warning",
            ClaimStatus.Approved or ClaimStatus.Paid or ClaimStatus.PartiallyPaid or ClaimStatus.Denied => "Passed",
            _ => "Warning"
        };

    private static string BuildDispositionSummary(Claim claim)
    {
        if (claim.Status == ClaimStatus.Pended && claim.PendDetails is { } pendDetails)
        {
            return $"Pended for {pendDetails.PendCode}: {pendDetails.PendReason ?? "review required"}.";
        }

        if (claim.Status == ClaimStatus.Denied && claim.AdjudicationResult?.DenialReasonCode is { Length: > 0 } denialCode)
        {
            return $"Denied with CARC {denialCode}: {claim.AdjudicationResult.DenialReason ?? "reason not captured"}.";
        }

        if (claim.AdjudicationResult is { } adjudication)
        {
            return $"{claim.Status}: payer payment {adjudication.PayerPayment:C}; member responsibility {adjudication.PatientResponsibility:C}.";
        }

        return $"{claim.Status}: no persisted adjudication projection is available.";
    }
}
