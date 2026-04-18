using CloudHealthOffice.Portal.Formatters;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Formatters;

public class BenefitCostShareFormatterTests
{
    [Fact]
    public void Both_null_returns_dash()
    {
        var tier = new NetworkTierBenefit { Copay = null, Coinsurance = null };
        BenefitCostShareFormatter.Format(tier).Should().Be("—");
    }

    [Fact]
    public void Zero_copay_only_renders_No_copay()
    {
        var tier = new NetworkTierBenefit { Copay = 0m, Coinsurance = null };
        BenefitCostShareFormatter.Format(tier).Should().Be("No copay");
    }

    [Fact]
    public void Zero_coinsurance_only_renders_No_coinsurance()
    {
        var tier = new NetworkTierBenefit { Copay = null, Coinsurance = 0m };
        BenefitCostShareFormatter.Format(tier).Should().Be("No coinsurance");
    }

    [Fact]
    public void Both_zero_renders_No_charge()
    {
        var tier = new NetworkTierBenefit { Copay = 0m, Coinsurance = 0m };
        BenefitCostShareFormatter.Format(tier).Should().Be("No charge");
    }

    [Fact]
    public void Copay_only_renders_dollars()
    {
        var tier = new NetworkTierBenefit { Copay = 25m, Coinsurance = null };
        BenefitCostShareFormatter.Format(tier).Should().Be("$25 copay");
    }

    [Fact]
    public void Coinsurance_only_renders_percent_scaled_from_fraction()
    {
        var tier = new NetworkTierBenefit { Copay = null, Coinsurance = 0.2m };
        BenefitCostShareFormatter.Format(tier).Should().Be("20% coinsurance");
    }

    [Fact]
    public void Copay_and_coinsurance_are_joined_with_separator()
    {
        var tier = new NetworkTierBenefit { Copay = 25m, Coinsurance = 0.2m };
        BenefitCostShareFormatter.Format(tier).Should().Be("$25 copay · 20% coinsurance");
    }

    [Fact]
    public void Zero_copay_with_nonzero_coinsurance_shows_both()
    {
        var tier = new NetworkTierBenefit { Copay = 0m, Coinsurance = 0.2m };
        BenefitCostShareFormatter.Format(tier).Should().Be("No copay · 20% coinsurance");
    }

    [Fact]
    public void Coinsurance_passed_as_percent_is_not_double_scaled()
    {
        // Some callers already pass 20 (percent) rather than 0.2 (fraction).
        // Values > 1 are treated as already-percent.
        var tier = new NetworkTierBenefit { Copay = null, Coinsurance = 20m };
        BenefitCostShareFormatter.Format(tier).Should().Be("20% coinsurance");
    }
}
