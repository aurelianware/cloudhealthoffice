using CloudHealthOffice.PricingApi.Data;
using CloudHealthOffice.PricingApi.Models;
using CloudHealthOffice.PricingApi.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudHealthOffice.PricingApi.Tests;

public class RepricingServiceTests
{
    private readonly Mock<IFeeScheduleRepository> _feeScheduleRepo = new();
    private readonly RepricingService _sut;

    public RepricingServiceTests()
    {
        _sut = new RepricingService(_feeScheduleRepo.Object, NullLogger<RepricingService>.Instance);
    }

    // ─────────────────────────────────────────────────────────
    //  Professional claim pricing
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RepriceClaimAsync_ProfessionalClaim_SingleLine_ReturnsAllowedAmount()
    {
        // Arrange
        var scheduleId = "MEDICARE_RBRVS_2025";
        SetupScheduleInfo(scheduleId);
        SetupFeeEntry(scheduleId, "99213", nonFacilityRate: 110.00m, facilityRate: 75.00m);

        var request = BuildRequest(scheduleId, ClaimType.Professional, lines: new[]
        {
            new ClaimLineRequest { LineNumber = 1, ProcedureCode = "99213", Units = 1 }
        });

        // Act
        var result = await _sut.RepriceClaimAsync(request);

        // Assert
        result.Lines.Should().HaveCount(1);
        result.Lines[0].AllowedAmount.Should().Be(110.00m);
        result.Lines[0].Status.Should().Be(PricingStatus.Priced);
        result.TotalAllowed.Should().Be(110.00m);
    }

    [Fact]
    public async Task RepriceClaimAsync_FacilityPlaceOfService_UsesFacilityRate()
    {
        var scheduleId = "MEDICARE_RBRVS_2025";
        SetupScheduleInfo(scheduleId);
        SetupFeeEntry(scheduleId, "99213", nonFacilityRate: 110.00m, facilityRate: 75.00m);

        var request = BuildRequest(scheduleId, ClaimType.Professional,
            placeOfService: "22", // On-Campus Outpatient Hospital (facility)
            lines: new[]
            {
                new ClaimLineRequest { LineNumber = 1, ProcedureCode = "99213", Units = 1 }
            });

        var result = await _sut.RepriceClaimAsync(request);

        result.Lines[0].AllowedAmount.Should().Be(75.00m);
        result.Lines[0].Breakdown.FacilityIndicator.Should().Be("Facility");
    }

    [Fact]
    public async Task RepriceClaimAsync_NonFacilityPlaceOfService_UsesNonFacilityRate()
    {
        var scheduleId = "MEDICARE_RBRVS_2025";
        SetupScheduleInfo(scheduleId);
        SetupFeeEntry(scheduleId, "99213", nonFacilityRate: 110.00m, facilityRate: 75.00m);

        var request = BuildRequest(scheduleId, ClaimType.Professional,
            placeOfService: "11", // Office (non-facility)
            lines: new[]
            {
                new ClaimLineRequest { LineNumber = 1, ProcedureCode = "99213", Units = 1 }
            });

        var result = await _sut.RepriceClaimAsync(request);

        result.Lines[0].AllowedAmount.Should().Be(110.00m);
        result.Lines[0].Breakdown.FacilityIndicator.Should().Be("Non-Facility");
    }

    [Fact]
    public async Task RepriceClaimAsync_MultipleUnits_MultipliesRate()
    {
        var scheduleId = "MEDICARE_RBRVS_2025";
        SetupScheduleInfo(scheduleId);
        SetupFeeEntry(scheduleId, "99213", nonFacilityRate: 110.00m);

        var request = BuildRequest(scheduleId, ClaimType.Professional, lines: new[]
        {
            new ClaimLineRequest { LineNumber = 1, ProcedureCode = "99213", Units = 3 }
        });

        var result = await _sut.RepriceClaimAsync(request);

        result.Lines[0].AllowedAmount.Should().Be(330.00m);
    }

    [Fact]
    public async Task RepriceClaimAsync_CodeNotFound_ReturnsNotFoundStatus()
    {
        var scheduleId = "MEDICARE_RBRVS_2025";
        SetupScheduleInfo(scheduleId);
        _feeScheduleRepo.Setup(r => r.LookupCodesAsync(scheduleId, It.IsAny<IEnumerable<string>>(), null))
            .ReturnsAsync(new List<FeeScheduleEntry>());

        var request = BuildRequest(scheduleId, ClaimType.Professional, lines: new[]
        {
            new ClaimLineRequest { LineNumber = 1, ProcedureCode = "XXXXX", Units = 1 }
        });

        var result = await _sut.RepriceClaimAsync(request);

        result.Lines[0].Status.Should().Be(PricingStatus.NotFound);
        result.Lines[0].AllowedAmount.Should().Be(0);
        result.Warnings.Should().Contain(w => w.Contains("XXXXX") && w.Contains("not found"));
    }

    [Fact]
    public async Task RepriceClaimAsync_InvalidFeeSchedule_Throws()
    {
        _feeScheduleRepo.Setup(r => r.GetScheduleInfoAsync("INVALID"))
            .ReturnsAsync((FeeScheduleInfo?)null);

        var request = BuildRequest("INVALID", ClaimType.Professional, lines: new[]
        {
            new ClaimLineRequest { LineNumber = 1, ProcedureCode = "99213", Units = 1 }
        });

        var act = () => _sut.RepriceClaimAsync(request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*INVALID*not found*");
    }

    // ─────────────────────────────────────────────────────────
    //  Multiple procedure reduction
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RepriceClaimAsync_MultipleSurgeries_Indicator2_RankedAt100_50_50()
    {
        var scheduleId = "MEDICARE_RBRVS_2025";
        SetupScheduleInfo(scheduleId);
        SetupFeeEntries(scheduleId, new (string, decimal, int?)[]
        {
            ("20610", 200.00m, 2),
            ("27447", 1500.00m, 2), // Total knee — highest value
            ("29881", 800.00m, 2),
        });

        var request = BuildRequest(scheduleId, ClaimType.Professional, lines: new[]
        {
            new ClaimLineRequest { LineNumber = 1, ProcedureCode = "20610", Units = 1 },
            new ClaimLineRequest { LineNumber = 2, ProcedureCode = "27447", Units = 1 },
            new ClaimLineRequest { LineNumber = 3, ProcedureCode = "29881", Units = 1 },
        });

        var result = await _sut.RepriceClaimAsync(request);

        result.Lines.Select(l => l.LineNumber).Should().Equal(1, 2, 3);
        result.Lines.Single(l => l.ProcedureCode == "27447").AllowedAmount.Should().Be(1500.00m);
        result.Lines.Single(l => l.ProcedureCode == "29881").AllowedAmount.Should().Be(400.00m);
        result.Lines.Single(l => l.ProcedureCode == "20610").AllowedAmount.Should().Be(100.00m);
    }

    [Fact]
    public async Task RepriceClaimAsync_EmIndicator0_NotReduced()
    {
        var scheduleId = "MEDICARE_RBRVS_2025";
        SetupScheduleInfo(scheduleId);
        SetupFeeEntries(scheduleId, new (string, decimal, int?)[]
        {
            ("27447", 1500.00m, 2), // Total knee — highest value
            ("99213", 110.00m, 0),  // E/M — no multiple procedure reduction
        });

        var request = BuildRequest(scheduleId, ClaimType.Professional, lines: new[]
        {
            new ClaimLineRequest { LineNumber = 1, ProcedureCode = "27447", Units = 1 },
            new ClaimLineRequest { LineNumber = 2, ProcedureCode = "99213", Units = 1, Modifiers = new List<string> { "25" } }
        });

        var result = await _sut.RepriceClaimAsync(request);

        result.Lines.Single(l => l.ProcedureCode == "27447").AllowedAmount.Should().Be(1500.00m);
        var em = result.Lines.Single(l => l.ProcedureCode == "99213");
        em.AllowedAmount.Should().Be(110.00m);
        em.Breakdown.MultiProcReduction.Should().BeNull();
        result.Warnings.Should().BeNull();
    }

    [Fact]
    public async Task RepriceClaimAsync_ImagingIndicator4_NotReducedBySurgeryRule_Warns()
    {
        var scheduleId = "MEDICARE_RBRVS_2025";
        SetupScheduleInfo(scheduleId);
        SetupFeeEntries(scheduleId, new (string, decimal, int?)[]
        {
            ("27447", 1500.00m, 2),
            ("73721", 300.00m, 4),
        });

        var request = BuildRequest(scheduleId, ClaimType.Professional, lines: new[]
        {
            new ClaimLineRequest { LineNumber = 1, ProcedureCode = "27447", Units = 1 },
            new ClaimLineRequest { LineNumber = 2, ProcedureCode = "73721", Units = 1 }
        });

        var result = await _sut.RepriceClaimAsync(request);

        result.Lines.Single(l => l.ProcedureCode == "27447").AllowedAmount.Should().Be(1500.00m);
        result.Lines.Single(l => l.ProcedureCode == "73721").AllowedAmount.Should().Be(300.00m);
        result.Warnings.Should().Contain(w => w.Contains("Line 2") && w.Contains("indicator 4") && w.Contains("not yet supported"));
    }

    [Fact]
    public async Task RepriceClaimAsync_UnknownIndicator_NoReduction_Warns()
    {
        var scheduleId = "MEDICARE_RBRVS_2025";
        SetupScheduleInfo(scheduleId);
        SetupFeeEntries(scheduleId, new (string, decimal, int?)[]
        {
            ("27447", 1500.00m, null),
            ("29881", 800.00m, null),
        });

        var request = BuildRequest(scheduleId, ClaimType.Professional, lines: new[]
        {
            new ClaimLineRequest { LineNumber = 1, ProcedureCode = "27447", Units = 1 },
            new ClaimLineRequest { LineNumber = 2, ProcedureCode = "29881", Units = 1 }
        });

        var result = await _sut.RepriceClaimAsync(request);

        result.Lines.Select(l => l.AllowedAmount).Should().Equal(1500.00m, 800.00m);
        result.Warnings.Should().Contain(w => w.Contains("Line 1") && w.Contains("No CMS multiple procedure indicator"));
        result.Warnings.Should().Contain(w => w.Contains("Line 2") && w.Contains("No CMS multiple procedure indicator"));
    }

    [Fact]
    public async Task RepriceClaimAsync_Opps_StatusT_DiscountedStatusS_NotDiscounted()
    {
        var scheduleId = "MEDICARE_OPPS_2025";
        SetupScheduleInfo(scheduleId);
        _feeScheduleRepo.Setup(r => r.LookupCodesAsync(
                scheduleId, It.IsAny<IEnumerable<string>>(), It.IsAny<string?>()))
            .ReturnsAsync(new List<FeeScheduleEntry>
            {
                new() { FeeScheduleId = scheduleId, ProcedureCode = "45380", ApcPaymentRate = 1441.28m, StatusIndicator = "T" },
                new() { FeeScheduleId = scheduleId, ProcedureCode = "10060", ApcPaymentRate = 302.78m, StatusIndicator = "T" },
                new() { FeeScheduleId = scheduleId, ProcedureCode = "73721", ApcPaymentRate = 267.83m, StatusIndicator = "S" },
            });

        var request = BuildRequest(scheduleId, ClaimType.Outpatient, placeOfService: "22", lines: new[]
        {
            new ClaimLineRequest { LineNumber = 1, ProcedureCode = "10060", Units = 1 },
            new ClaimLineRequest { LineNumber = 2, ProcedureCode = "45380", Units = 1 },
            new ClaimLineRequest { LineNumber = 3, ProcedureCode = "73721", Units = 1 },
        });

        var result = await _sut.RepriceClaimAsync(request);

        result.Lines.Single(l => l.ProcedureCode == "45380").AllowedAmount.Should().Be(1441.28m);
        result.Lines.Single(l => l.ProcedureCode == "10060").AllowedAmount.Should().Be(151.39m);
        result.Lines.Single(l => l.ProcedureCode == "73721").AllowedAmount.Should().Be(267.83m);
        result.Warnings.Should().NotContain(w => w.Contains("multiple procedure indicator", StringComparison.OrdinalIgnoreCase));
    }

    // ─────────────────────────────────────────────────────────
    //  Modifier adjustments
    // ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("50", 1.5)]   // Bilateral — 150%
    [InlineData("52", 0.5)]   // Reduced services — 50%
    [InlineData("80", 0.16)]  // Assistant surgeon — 16%
    [InlineData("62", 0.625)] // Co-surgeon — 62.5%
    public async Task RepriceClaimAsync_Modifier_AppliesCorrectFactor(string modifier, double expectedFactor)
    {
        var scheduleId = "MEDICARE_RBRVS_2025";
        SetupScheduleInfo(scheduleId);
        SetupFeeEntry(scheduleId, "27447", nonFacilityRate: 1000.00m);

        var request = BuildRequest(scheduleId, ClaimType.Professional, lines: new[]
        {
            new ClaimLineRequest
            {
                LineNumber = 1, ProcedureCode = "27447", Units = 1,
                Modifiers = new List<string> { modifier }
            }
        });

        var result = await _sut.RepriceClaimAsync(request);

        result.Lines[0].AllowedAmount.Should().Be(
            Math.Round(1000.00m * (decimal)expectedFactor, 2));
    }

    [Fact]
    public async Task RepriceClaimAsync_NeutralModifier_NoAdjustment()
    {
        var scheduleId = "MEDICARE_RBRVS_2025";
        SetupScheduleInfo(scheduleId);
        SetupFeeEntry(scheduleId, "27447", nonFacilityRate: 1000.00m);

        var request = BuildRequest(scheduleId, ClaimType.Professional, lines: new[]
        {
            new ClaimLineRequest
            {
                LineNumber = 1, ProcedureCode = "27447", Units = 1,
                Modifiers = new List<string> { "59" } // Distinct — no adjustment
            }
        });

        var result = await _sut.RepriceClaimAsync(request);

        result.Lines[0].AllowedAmount.Should().Be(1000.00m);
    }

    // ─────────────────────────────────────────────────────────
    //  Inpatient DRG pricing
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RepriceClaimAsync_InpatientClaim_UsesDrgWeight()
    {
        var scheduleId = "MEDICARE_DRG_2025";
        SetupScheduleInfo(scheduleId);
        _feeScheduleRepo.Setup(r => r.LookupDrgAsync(scheduleId, "470"))
            .ReturnsAsync(new FeeScheduleEntry
            {
                FeeScheduleId = scheduleId,
                ProcedureCode = "470",
                DrgWeight = 1.9m,
                DrgBaseRate = 7000.00m
            });

        var request = BuildRequest(scheduleId, ClaimType.Inpatient, drgCode: "470", lines: new[]
        {
            new ClaimLineRequest { LineNumber = 1, ProcedureCode = "27447", Units = 1 }
        });

        var result = await _sut.RepriceClaimAsync(request);

        // DRG payment = weight * base rate = 1.9 * 7000 = 13300
        result.TotalAllowed.Should().Be(13300.00m);
        result.Lines[0].Status.Should().Be(PricingStatus.Priced);
        result.Lines[0].Breakdown.DrgRelativeWeight.Should().Be(1.9m);
        result.Lines[0].Breakdown.HospitalBaseRate.Should().Be(7000.00m);
    }

    [Fact]
    public async Task RepriceClaimAsync_InpatientClaim_NoDrgCode_ReturnsNotFound()
    {
        var scheduleId = "MEDICARE_DRG_2025";
        SetupScheduleInfo(scheduleId);

        var request = BuildRequest(scheduleId, ClaimType.Inpatient, drgCode: null, lines: new[]
        {
            new ClaimLineRequest { LineNumber = 1, ProcedureCode = "27447", Units = 1 }
        });

        var result = await _sut.RepriceClaimAsync(request);

        result.Lines[0].Status.Should().Be(PricingStatus.NotFound);
        result.Lines[0].StatusReason.Should().Contain("DRG code required");
        result.Warnings.Should().Contain(w => w.Contains("No DRG code provided"));
    }

    [Fact]
    public async Task RepriceClaimAsync_InpatientClaim_DrgNotFound_ReturnsNotFound()
    {
        var scheduleId = "MEDICARE_DRG_2025";
        SetupScheduleInfo(scheduleId);
        _feeScheduleRepo.Setup(r => r.LookupDrgAsync(scheduleId, "999"))
            .ReturnsAsync((FeeScheduleEntry?)null);

        var request = BuildRequest(scheduleId, ClaimType.Inpatient, drgCode: "999", lines: new[]
        {
            new ClaimLineRequest { LineNumber = 1, ProcedureCode = "27447", Units = 1 }
        });

        var result = await _sut.RepriceClaimAsync(request);

        result.Lines[0].Status.Should().Be(PricingStatus.NotFound);
        result.Warnings.Should().Contain(w => w.Contains("DRG 999 not found"));
    }

    /// <summary>
    /// The DRG case rate is paid once per claim and allocated across the lines in
    /// proportion to billed charges: the engine's rule, shared with adjudication
    /// (ADR 014). Before the Pricing API delegated to the engine it put the whole
    /// case rate on line 1; the claim total is unchanged.
    /// </summary>
    [Fact]
    public async Task RepriceClaimAsync_InpatientMultipleLines_DrgCaseRateAllocatedByBilledCharges()
    {
        var scheduleId = "MEDICARE_DRG_2025";
        SetupScheduleInfo(scheduleId);
        SetupDrg(scheduleId, "470", weight: 2.0m, baseRate: 5000.00m);

        var request = BuildRequest(scheduleId, ClaimType.Inpatient, drgCode: "470", lines: new[]
        {
            new ClaimLineRequest { LineNumber = 1, ProcedureCode = "27447", Units = 1, BilledAmount = 30000m },
            new ClaimLineRequest { LineNumber = 2, ProcedureCode = "99213", Units = 1, BilledAmount = 10000m }
        });

        var result = await _sut.RepriceClaimAsync(request);

        result.Lines[0].AllowedAmount.Should().Be(7500.00m); // 75% of 2.0 × 5000
        result.Lines[1].AllowedAmount.Should().Be(2500.00m); // 25%
        result.Lines.Should().OnlyContain(l => l.Status == PricingStatus.Priced);
        result.Lines[1].StatusReason.Should().Contain("DRG case rate");
        result.TotalAllowed.Should().Be(10000.00m);
    }

    [Fact]
    public async Task RepriceClaimAsync_InpatientMultipleLines_NoBilledCharges_SplitEvenly()
    {
        var scheduleId = "MEDICARE_DRG_2025";
        SetupScheduleInfo(scheduleId);
        SetupDrg(scheduleId, "470", weight: 2.0m, baseRate: 5000.00m);

        var request = BuildRequest(scheduleId, ClaimType.Inpatient, drgCode: "470", lines: new[]
        {
            new ClaimLineRequest { LineNumber = 1, ProcedureCode = "27447", Units = 1 },
            new ClaimLineRequest { LineNumber = 2, ProcedureCode = "99213", Units = 1 }
        });

        var result = await _sut.RepriceClaimAsync(request);

        result.Lines.Select(l => l.AllowedAmount).Should().Equal(5000.00m, 5000.00m);
        result.TotalAllowed.Should().Be(10000.00m);
    }

    /// <summary>Modifiers 81 / 82 are assistant surgeon modifiers: 16% under CMS (the engine's rule).</summary>
    [Theory]
    [InlineData("81")]
    [InlineData("82")]
    public async Task RepriceClaimAsync_AssistantSurgeon81And82_PricedAt16Percent(string modifier)
    {
        var scheduleId = "MEDICARE_RBRVS_2025";
        SetupScheduleInfo(scheduleId);
        SetupFeeEntry(scheduleId, "27447", nonFacilityRate: 1000.00m);

        var request = BuildRequest(scheduleId, ClaimType.Professional, lines: new[]
        {
            new ClaimLineRequest { LineNumber = 1, ProcedureCode = "27447", Units = 1, Modifiers = new List<string> { modifier } }
        });

        var result = await _sut.RepriceClaimAsync(request);

        result.Lines[0].AllowedAmount.Should().Be(160.00m);
        result.Lines[0].Breakdown.ModifierAdjustment.Should().Be("Factor: 0.16");
    }

    [Fact]
    public async Task RepriceClaimAsync_TeamSurgery66_NotAdjusted_Warns()
    {
        var scheduleId = "MEDICARE_RBRVS_2025";
        SetupScheduleInfo(scheduleId);
        SetupFeeEntry(scheduleId, "27447", nonFacilityRate: 1000.00m);

        var request = BuildRequest(scheduleId, ClaimType.Professional, lines: new[]
        {
            new ClaimLineRequest { LineNumber = 1, ProcedureCode = "27447", Units = 1, Modifiers = new List<string> { "66" } }
        });

        var result = await _sut.RepriceClaimAsync(request);

        result.Lines[0].AllowedAmount.Should().Be(1000.00m);
        result.Warnings.Should().Contain(w => w.Contains("Modifier 66") && w.Contains("by report"));
    }

    [Fact]
    public async Task RepriceClaimAsync_RepeatedLineNumbers_PricedByPosition()
    {
        var scheduleId = "MEDICARE_RBRVS_2025";
        SetupScheduleInfo(scheduleId);
        SetupFeeEntries(scheduleId, new (string, decimal, int?)[]
        {
            ("27447", 1500.00m, 2),
            ("29881", 800.00m, 2),
        });

        // Line numbers default to 1 when a caller omits them.
        var request = BuildRequest(scheduleId, ClaimType.Professional, lines: new[]
        {
            new ClaimLineRequest { ProcedureCode = "29881", Units = 1 },
            new ClaimLineRequest { ProcedureCode = "27447", Units = 1 },
        });

        var result = await _sut.RepriceClaimAsync(request);

        result.Lines.Select(l => l.ProcedureCode).Should().Equal("29881", "27447");
        result.Lines.Select(l => l.AllowedAmount).Should().Equal(400.00m, 1500.00m);
    }

    // ─────────────────────────────────────────────────────────
    //  Outpatient (APC) pricing
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RepriceClaimAsync_OutpatientClaim_UsesApcRate()
    {
        var scheduleId = "MEDICARE_OPPS_2025";
        SetupScheduleInfo(scheduleId);

        // OPPS entries have null facility/non-facility rates; APC rate is used instead
        var entry = new FeeScheduleEntry
        {
            FeeScheduleId = scheduleId,
            ProcedureCode = "27447",
            FacilityRate = null,
            NonFacilityRate = null,
            ApcPaymentRate = 8500.00m,
            ApcCode = "5115"
        };
        _feeScheduleRepo.Setup(r => r.LookupCodesAsync(
                scheduleId, It.IsAny<IEnumerable<string>>(), It.IsAny<string?>()))
            .ReturnsAsync(new List<FeeScheduleEntry> { entry });

        var request = BuildRequest(scheduleId, ClaimType.Outpatient,
            placeOfService: "22", // Facility
            lines: new[]
            {
                new ClaimLineRequest { LineNumber = 1, ProcedureCode = "27447", Units = 1 }
            });

        var result = await _sut.RepriceClaimAsync(request);

        result.Lines[0].AllowedAmount.Should().Be(8500.00m);
        result.Lines[0].Breakdown.ApcCode.Should().Be("5115");
    }

    // ─────────────────────────────────────────────────────────
    //  Code lookup
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LookupCodeAsync_ExistingCode_ReturnsDetails()
    {
        _feeScheduleRepo.Setup(r => r.LookupCodeAsync("MEDICARE_RBRVS_2025", "99213", null))
            .ReturnsAsync(new FeeScheduleEntry
            {
                FeeScheduleId = "MEDICARE_RBRVS_2025",
                ProcedureCode = "99213",
                Description = "Office visit, established patient",
                NonFacilityRate = 110.00m,
                FacilityRate = 75.00m,
                WorkRvu = 1.3m,
                ConversionFactor = 33.89m
            });

        var result = await _sut.LookupCodeAsync(new CodeLookupRequest
        {
            ProcedureCode = "99213",
            FeeScheduleId = "MEDICARE_RBRVS_2025",
            Facility = false
        });

        result.Should().NotBeNull();
        result!.ProcedureCode.Should().Be("99213");
        result.AllowedAmount.Should().Be(110.00m);
        result.WorkRvu.Should().Be(1.3m);
        result.Facility.Should().BeFalse();
    }

    [Fact]
    public async Task LookupCodeAsync_FacilityRate_ReturnsFacilityAmount()
    {
        _feeScheduleRepo.Setup(r => r.LookupCodeAsync("MEDICARE_RBRVS_2025", "99213", null))
            .ReturnsAsync(new FeeScheduleEntry
            {
                FeeScheduleId = "MEDICARE_RBRVS_2025",
                ProcedureCode = "99213",
                NonFacilityRate = 110.00m,
                FacilityRate = 75.00m
            });

        var result = await _sut.LookupCodeAsync(new CodeLookupRequest
        {
            ProcedureCode = "99213",
            FeeScheduleId = "MEDICARE_RBRVS_2025",
            Facility = true
        });

        result!.AllowedAmount.Should().Be(75.00m);
        result.Facility.Should().BeTrue();
    }

    [Fact]
    public async Task LookupCodeAsync_CodeNotFound_ReturnsNull()
    {
        _feeScheduleRepo.Setup(r => r.LookupCodeAsync("MEDICARE_RBRVS_2025", "XXXXX", null))
            .ReturnsAsync((FeeScheduleEntry?)null);

        var result = await _sut.LookupCodeAsync(new CodeLookupRequest
        {
            ProcedureCode = "XXXXX",
            FeeScheduleId = "MEDICARE_RBRVS_2025"
        });

        result.Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────
    //  Response structure
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RepriceClaimAsync_PopulatesResponseMetadata()
    {
        var scheduleId = "MEDICARE_RBRVS_2025";
        SetupScheduleInfo(scheduleId);
        SetupFeeEntry(scheduleId, "99213", nonFacilityRate: 110.00m);

        var request = BuildRequest(scheduleId, ClaimType.Professional, lines: new[]
        {
            new ClaimLineRequest { LineNumber = 1, ProcedureCode = "99213", Units = 1, BilledAmount = 200.00m }
        });

        var result = await _sut.RepriceClaimAsync(request);

        result.FeeScheduleId.Should().Be(scheduleId);
        result.FeeScheduleVersion.Should().Be("2025.1");
        result.ClaimType.Should().Be(ClaimType.Professional);
        result.RequestId.Should().NotBeNullOrEmpty();
        result.TotalBilled.Should().Be(200.00m);
        result.PricedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    // ─────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────

    private void SetupScheduleInfo(string scheduleId)
    {
        _feeScheduleRepo.Setup(r => r.GetScheduleInfoAsync(scheduleId))
            .ReturnsAsync(new FeeScheduleInfo
            {
                Id = scheduleId,
                Name = scheduleId,
                Type = FeeScheduleType.MedicareRbrvs,
                Version = "2025.1",
                EffectiveDate = new DateOnly(2025, 1, 1),
                CodeCount = 10000,
                LastUpdated = DateTimeOffset.UtcNow
            });
    }

    private void SetupFeeEntry(string scheduleId, string code,
        decimal nonFacilityRate = 0, decimal facilityRate = 0,
        decimal? apcPaymentRate = null, string? apcCode = null)
    {
        var entry = new FeeScheduleEntry
        {
            FeeScheduleId = scheduleId,
            ProcedureCode = code,
            NonFacilityRate = nonFacilityRate,
            FacilityRate = facilityRate,
            ApcPaymentRate = apcPaymentRate,
            ApcCode = apcCode
        };

        _feeScheduleRepo.Setup(r => r.LookupCodesAsync(
                scheduleId, It.Is<IEnumerable<string>>(codes => codes.Contains(code)), It.IsAny<string?>()))
            .ReturnsAsync(new List<FeeScheduleEntry> { entry });
    }

    private void SetupFeeEntries(string scheduleId, (string code, decimal rate, int? multProc)[] entries)
    {
        var feeEntries = entries.Select(e => new FeeScheduleEntry
        {
            FeeScheduleId = scheduleId,
            ProcedureCode = e.code,
            NonFacilityRate = e.rate,
            FacilityRate = e.rate,
            MultipleProcedureIndicator = e.multProc
        }).ToList();

        _feeScheduleRepo.Setup(r => r.LookupCodesAsync(
                scheduleId, It.IsAny<IEnumerable<string>>(), It.IsAny<string?>()))
            .ReturnsAsync(feeEntries);
    }

    private void SetupDrg(string scheduleId, string drgCode, decimal weight, decimal baseRate)
    {
        _feeScheduleRepo.Setup(r => r.LookupDrgAsync(scheduleId, drgCode))
            .ReturnsAsync(new FeeScheduleEntry
            {
                FeeScheduleId = scheduleId,
                ProcedureCode = drgCode,
                DrgWeight = weight,
                DrgBaseRate = baseRate
            });
    }

    private static RepricingRequest BuildRequest(string scheduleId, ClaimType claimType,
        string? placeOfService = null, string? drgCode = null, ClaimLineRequest[]? lines = null)
    {
        return new RepricingRequest
        {
            FeeScheduleId = scheduleId,
            ClaimType = claimType,
            PlaceOfService = placeOfService,
            DrgCode = drgCode,
            Lines = lines?.ToList() ?? new List<ClaimLineRequest>()
        };
    }
}
