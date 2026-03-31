using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>
/// Property round-trip tests for DTO model classes that had zero or near-zero coverage.
/// Each test instantiates a model, sets every property, then reads back and asserts the values.
/// This ensures the auto-generated getters/setters (and any backing-field logic) are exercised.
/// </summary>
public class ModelDtoPropertyTests
{
    // ── AccumulatorUpdate ────────────────────────────────────────────────────

    [Fact]
    public void AccumulatorUpdate_AllProperties_RoundTrip()
    {
        var sut = new AccumulatorUpdate
        {
            AccumulatorType = "IndividualDeductible",
            AmountApplied = 150.00m,
            NewBalance = 350.00m,
            Limit = 1500.00m
        };

        sut.AccumulatorType.Should().Be("IndividualDeductible");
        sut.AmountApplied.Should().Be(150.00m);
        sut.NewBalance.Should().Be(350.00m);
        sut.Limit.Should().Be(1500.00m);
    }

    // ── CapAdjustment ────────────────────────────────────────────────────────

    [Fact]
    public void CapAdjustment_AllProperties_RoundTrip()
    {
        var date = new DateTime(2026, 1, 15);
        var sut = new CapAdjustment
        {
            Type = "Retro",
            Description = "Retroactive rate correction",
            Amount = -250.00m,
            RelatedMemberId = "MBR-001",
            AdjustmentDate = date
        };

        sut.Type.Should().Be("Retro");
        sut.Description.Should().Be("Retroactive rate correction");
        sut.Amount.Should().Be(-250.00m);
        sut.RelatedMemberId.Should().Be("MBR-001");
        sut.AdjustmentDate.Should().Be(date);
    }

    // ── CapLineItem ──────────────────────────────────────────────────────────

    [Fact]
    public void CapLineItem_AllProperties_RoundTrip()
    {
        var sut = new CapLineItem
        {
            MemberId = "MBR-100",
            MemberName = "Jane Smith",
            PlanId = "PLN-001",
            MemberAge = 42,
            Gender = "F",
            BasePMPM = 285.00m,
            RiskScore = 1.12m,
            AdjustedPMPM = 319.20m,
            ProrationFactor = 0.5m,
            GrossAmount = 159.60m,
            WithholdAmount = 23.94m,
            NetAmount = 135.66m,
            IsRetroactive = true,
            AdjustmentReason = "Mid-month enrollment"
        };

        sut.MemberId.Should().Be("MBR-100");
        sut.MemberName.Should().Be("Jane Smith");
        sut.PlanId.Should().Be("PLN-001");
        sut.MemberAge.Should().Be(42);
        sut.Gender.Should().Be("F");
        sut.BasePMPM.Should().Be(285.00m);
        sut.RiskScore.Should().Be(1.12m);
        sut.AdjustedPMPM.Should().Be(319.20m);
        sut.ProrationFactor.Should().Be(0.5m);
        sut.GrossAmount.Should().Be(159.60m);
        sut.WithholdAmount.Should().Be(23.94m);
        sut.NetAmount.Should().Be(135.66m);
        sut.IsRetroactive.Should().BeTrue();
        sut.AdjustmentReason.Should().Be("Mid-month enrollment");
    }

    // ── CapRateTier ──────────────────────────────────────────────────────────

    [Fact]
    public void CapRateTier_AllProperties_RoundTrip()
    {
        var sut = new CapRateTier
        {
            TierName = "Adult",
            AgeFrom = 18,
            AgeTo = 64,
            Gender = "M",
            AgeSexCategory = "Adult Male",
            BasePMPM = 310.50m,
            ServiceCategory = "Medical"
        };

        sut.TierName.Should().Be("Adult");
        sut.AgeFrom.Should().Be(18);
        sut.AgeTo.Should().Be(64);
        sut.Gender.Should().Be("M");
        sut.AgeSexCategory.Should().Be("Adult Male");
        sut.BasePMPM.Should().Be(310.50m);
        sut.ServiceCategory.Should().Be("Medical");
    }

    // ── CapRunCriteriaSummary ────────────────────────────────────────────────

    [Fact]
    public void CapRunCriteriaSummary_AllProperties_RoundTrip()
    {
        var origPeriod = new DateTime(2025, 12, 1);
        var sut = new CapRunCriteriaSummary
        {
            LineOfBusiness = "Commercial",
            ProviderNPI = "1234567890",
            ContractType = "Capitation",
            OriginalPeriod = origPeriod
        };

        sut.LineOfBusiness.Should().Be("Commercial");
        sut.ProviderNPI.Should().Be("1234567890");
        sut.ContractType.Should().Be("Capitation");
        sut.OriginalPeriod.Should().Be(origPeriod);
    }

    // ── ClaimAdjustmentInfo ──────────────────────────────────────────────────

    [Fact]
    public void ClaimAdjustmentInfo_AllProperties_RoundTrip()
    {
        var adjDate = new DateTime(2026, 2, 20);
        var sut = new ClaimAdjustmentInfo
        {
            AdjustmentType = "Reversal",
            OriginalClaimId = "CLM-ORG-001",
            RelatedClaimId = "CLM-REL-002",
            AdjustmentAmount = -500.00m,
            Reason = "Overpayment correction",
            AdjustmentDate = adjDate,
            AdjustedBy = "adjuster@healthplan.com"
        };

        sut.AdjustmentType.Should().Be("Reversal");
        sut.OriginalClaimId.Should().Be("CLM-ORG-001");
        sut.RelatedClaimId.Should().Be("CLM-REL-002");
        sut.AdjustmentAmount.Should().Be(-500.00m);
        sut.Reason.Should().Be("Overpayment correction");
        sut.AdjustmentDate.Should().Be(adjDate);
        sut.AdjustedBy.Should().Be("adjuster@healthplan.com");
    }

    // ── ClaimAudit ───────────────────────────────────────────────────────────

    [Fact]
    public void ClaimAudit_AllProperties_RoundTrip()
    {
        var ts = new DateTime(2026, 3, 10, 14, 30, 0, DateTimeKind.Utc);
        var sut = new ClaimAudit
        {
            Timestamp = ts,
            Action = "StatusChange",
            ChangedBy = "reviewer@healthplan.com",
            OldValue = "Submitted",
            NewValue = "Approved",
            Notes = "Reviewed and approved per policy"
        };

        sut.Timestamp.Should().Be(ts);
        sut.Action.Should().Be("StatusChange");
        sut.ChangedBy.Should().Be("reviewer@healthplan.com");
        sut.OldValue.Should().Be("Submitted");
        sut.NewValue.Should().Be("Approved");
        sut.Notes.Should().Be("Reviewed and approved per policy");
    }

    // ── ClaimLineAdjustment ──────────────────────────────────────────────────

    [Fact]
    public void ClaimLineAdjustment_AllProperties_RoundTrip()
    {
        var sut = new ClaimLineAdjustment
        {
            GroupCode = "CO",
            ReasonCode = "45",
            Amount = 75.00m,
            Description = "Charge exceeds fee schedule"
        };

        sut.GroupCode.Should().Be("CO");
        sut.ReasonCode.Should().Be("45");
        sut.Amount.Should().Be(75.00m);
        sut.Description.Should().Be("Charge exceeds fee schedule");
    }

    // ── PremiumSplitSummary ──────────────────────────────────────────────────

    [Fact]
    public void PremiumSplitSummary_AllProperties_RoundTrip()
    {
        var sut = new PremiumSplitSummary
        {
            SponsorPercentage = 75.0m,
            MemberPercentage = 25.0m,
            IsPlanSpecific = true
        };

        sut.SponsorPercentage.Should().Be(75.0m);
        sut.MemberPercentage.Should().Be(25.0m);
        sut.IsPlanSpecific.Should().BeTrue();
    }

    // ── ProviderContract ─────────────────────────────────────────────────────

    [Fact]
    public void ProviderContract_AllProperties_RoundTrip()
    {
        var effective = new DateTime(2025, 1, 1);
        var termination = new DateTime(2025, 12, 31);
        var sut = new ProviderContract
        {
            ContractId = "CTR-PC-001",
            ReimbursementMethod = "Capitation",
            FeeScheduleTier = "Tier1",
            EffectiveDate = effective,
            TerminationDate = termination,
            CapitationRate = 320.00m
        };

        sut.ContractId.Should().Be("CTR-PC-001");
        sut.ReimbursementMethod.Should().Be("Capitation");
        sut.FeeScheduleTier.Should().Be("Tier1");
        sut.EffectiveDate.Should().Be(effective);
        sut.TerminationDate.Should().Be(termination);
        sut.CapitationRate.Should().Be(320.00m);
    }

    [Fact]
    public void ProviderContract_NullableProperties_Default()
    {
        var sut = new ProviderContract();

        sut.TerminationDate.Should().BeNull();
        sut.CapitationRate.Should().BeNull();
    }

    // ── ProviderPerformance ──────────────────────────────────────────────────

    [Fact]
    public void ProviderPerformance_AllProperties_RoundTrip()
    {
        var sut = new ProviderPerformance
        {
            ClaimsLast90Days = 142,
            TotalBilledLast90Days = 71000.00m,
            AvgClaimAmount = 500.00m,
            AuthorizationRequests = 30,
            AuthorizationApprovalRate = 0.87m,
            DenialCount = 4,
            DenialRate = 0.028m,
            AvgProcessingTimeDays = 3.2m,
            QualityScore = 94.5m
        };

        sut.ClaimsLast90Days.Should().Be(142);
        sut.TotalBilledLast90Days.Should().Be(71000.00m);
        sut.AvgClaimAmount.Should().Be(500.00m);
        sut.AuthorizationRequests.Should().Be(30);
        sut.AuthorizationApprovalRate.Should().Be(0.87m);
        sut.DenialCount.Should().Be(4);
        sut.DenialRate.Should().Be(0.028m);
        sut.AvgProcessingTimeDays.Should().Be(3.2m);
        sut.QualityScore.Should().Be(94.5m);
    }

    [Fact]
    public void ProviderPerformance_QualityScore_NullByDefault()
    {
        var sut = new ProviderPerformance();
        sut.QualityScore.Should().BeNull();
    }

    // ── ReportRequest ────────────────────────────────────────────────────────

    [Fact]
    public void ReportRequest_AllProperties_RoundTrip()
    {
        var from = new DateTime(2026, 1, 1);
        var to = new DateTime(2026, 3, 31);
        var sut = new ReportRequest
        {
            DateFrom = from,
            DateTo = to,
            ProviderId = "PRV-500",
            SponsorId = "SP-100",
            PlanId = "PLN-200"
        };

        sut.DateFrom.Should().Be(from);
        sut.DateTo.Should().Be(to);
        sut.ProviderId.Should().Be("PRV-500");
        sut.SponsorId.Should().Be("SP-100");
        sut.PlanId.Should().Be("PLN-200");
    }

    [Fact]
    public void ReportRequest_NullableProperties_Default()
    {
        var sut = new ReportRequest();
        sut.ProviderId.Should().BeNull();
        sut.SponsorId.Should().BeNull();
        sut.PlanId.Should().BeNull();
    }

    // ── ServiceLine ──────────────────────────────────────────────────────────

    [Fact]
    public void ServiceLine_AllProperties_RoundTrip()
    {
        var sut = new ServiceLine
        {
            ProcedureCode = "99214",
            Description = "Office/outpatient visit est",
            ChargeAmount = 250.00m,
            AllowedAmount = 175.00m,
            PayerAmount = 140.00m
        };

        sut.ProcedureCode.Should().Be("99214");
        sut.Description.Should().Be("Office/outpatient visit est");
        sut.ChargeAmount.Should().Be(250.00m);
        sut.AllowedAmount.Should().Be(175.00m);
        sut.PayerAmount.Should().Be(140.00m);
    }

    // ── ServiceLineRequest ───────────────────────────────────────────────────

    [Fact]
    public void ServiceLineRequest_AllProperties_RoundTrip()
    {
        var sut = new ServiceLineRequest
        {
            ProcedureCode = "99213",
            ChargeAmount = 180.00m,
            Units = 2
        };

        sut.ProcedureCode.Should().Be("99213");
        sut.ChargeAmount.Should().Be(180.00m);
        sut.Units.Should().Be(2);
    }

    [Fact]
    public void ServiceLineRequest_DefaultUnits_IsOne()
    {
        var sut = new ServiceLineRequest { ProcedureCode = "99211", ChargeAmount = 50m };
        sut.Units.Should().Be(1);
    }

    // ── WorkflowRunExtended ──────────────────────────────────────────────────

    [Fact]
    public void WorkflowRunExtended_AllProperties_RoundTrip()
    {
        var step = new WorkflowStepExtended
        {
            DurationMs = 450,
            NodeName = "worker-node-1",
            Message = "Step completed successfully",
            StepNumber = 3,
            Name = "parse-834",
            Status = "Succeeded",
            StartTime = new DateTime(2026, 1, 15, 8, 0, 10, DateTimeKind.Utc),
            FinishTime = new DateTime(2026, 1, 15, 8, 0, 15, DateTimeKind.Utc)
        };

        var sut = new WorkflowRunExtended
        {
            WorkflowTemplate = "edi-834-ingest-v2",
            TriggerSource = "Scheduled",
            StepCount = 5,
            CompletedStepCount = 5,
            DetailedSteps = new List<WorkflowStepExtended> { step },
            WorkflowId = "WF-EXT-001",
            Name = "daily-834-ingest",
            Status = "Succeeded",
            StartTime = new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc),
            DurationSeconds = 120
        };

        sut.WorkflowTemplate.Should().Be("edi-834-ingest-v2");
        sut.TriggerSource.Should().Be("Scheduled");
        sut.StepCount.Should().Be(5);
        sut.CompletedStepCount.Should().Be(5);
        sut.DetailedSteps.Should().ContainSingle();

        // Inherited from WorkflowRun
        sut.WorkflowId.Should().Be("WF-EXT-001");
        sut.Name.Should().Be("daily-834-ingest");
        sut.Status.Should().Be("Succeeded");
        sut.DurationSeconds.Should().Be(120);
    }

    // ── WorkflowStepExtended ─────────────────────────────────────────────────

    [Fact]
    public void WorkflowStepExtended_AllProperties_RoundTrip()
    {
        var start = new DateTime(2026, 1, 15, 8, 0, 10, DateTimeKind.Utc);
        var finish = new DateTime(2026, 1, 15, 8, 0, 13, DateTimeKind.Utc);
        var sut = new WorkflowStepExtended
        {
            DurationMs = 3200,
            NodeName = "worker-node-2",
            Message = "Processing completed",
            StepNumber = 2,
            // inherited WorkflowStep properties
            Name = "validate-schema",
            Status = "Succeeded",
            StartTime = start,
            FinishTime = finish
        };

        sut.DurationMs.Should().Be(3200);
        sut.NodeName.Should().Be("worker-node-2");
        sut.Message.Should().Be("Processing completed");
        sut.StepNumber.Should().Be(2);
        sut.Name.Should().Be("validate-schema");
        sut.Status.Should().Be("Succeeded");
        sut.StartTime.Should().Be((DateTime?)start);
        sut.FinishTime.Should().Be((DateTime?)finish);
    }

    [Fact]
    public void WorkflowStepExtended_NullableProperties_Default()
    {
        var sut = new WorkflowStepExtended { Name = "step-1", Status = "Running" };
        sut.DurationMs.Should().BeNull();
        sut.NodeName.Should().BeNull();
        sut.Message.Should().BeNull();
        sut.FinishTime.Should().BeNull();
    }
}
