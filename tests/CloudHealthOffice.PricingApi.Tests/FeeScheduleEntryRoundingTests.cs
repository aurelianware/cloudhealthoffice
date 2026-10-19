using CloudHealthOffice.PricingApi.Services;
using FluentAssertions;

namespace CloudHealthOffice.PricingApi.Tests;

/// <summary>
/// Seeded facility / non-facility rates are money and are rounded to the cent
/// half away from zero (MoneyRounding.ToCents), not with banker's rounding.
/// </summary>
public class FeeScheduleEntryRoundingTests
{
    [Fact]
    public void Rbrvs_RateAtHalfCent_RoundsAwayFromZero()
    {
        // Non-facility (1.00 + 0.25 + 0) × 10.10 = $12.625 → $12.63 (banker's: $12.62)
        // Facility     (1.00 + 0.05 + 0) × 10.10 = $10.605 → $10.61 (banker's: $10.60)
        var entry = FeeScheduleLoaderService.Rbrvs("MPFS", "20610", "Arthrocentesis",
            workRvu: 1.00m, peNonFac: 0.25m, peFac: 0.05m, mpRvu: 0m, convFactor: 10.10m, multProc: 2);

        entry.NonFacilityRate.Should().Be(12.63m);
        entry.FacilityRate.Should().Be(10.61m);
    }

    [Fact]
    public void Drg_RateAtHalfCent_RoundsAwayFromZero()
    {
        // 1.5 × $6,543.23 = $9,814.845 → $9,814.85 (banker's: $9,814.84)
        var entry = FeeScheduleLoaderService.Drg("DRG", "470", "Major joint replacement", 1.5m, 6543.23m);

        entry.NonFacilityRate.Should().Be(9814.85m);
        entry.FacilityRate.Should().Be(9814.85m);
    }
}
