using EncounterSubmissionService.StateRoutes;
using static CloudHealthOffice.EncounterSubmissionService.Tests.StateRoutes.StateRouteTestData;

namespace CloudHealthOffice.EncounterSubmissionService.Tests.StateRoutes;

public class EncounterValidatorTests
{
    [Fact]
    public void CompleteEncounters_Pass()
    {
        EncounterValidator.Validate(Professional()).Should().BeEmpty();
        EncounterValidator.Validate(Institutional()).Should().BeEmpty();
    }

    [Fact]
    public void UnbalancedLine_IsCaught()
    {
        var c = Professional();
        var bad = c with { Lines = new[] { c.Lines[0] with { PaidAmount = 95m }, c.Lines[1] }, PlanPaidAmount = 125m };

        EncounterValidator.Validate(bad).Should().ContainSingle(e => e.StartsWith("line 1: charge 150.00 does not balance"));
    }

    [Fact]
    public void PlanPaid_MustEqualLinePaid()
    {
        EncounterValidator.Validate(Professional() with { PlanPaidAmount = 999m })
            .Should().Contain(e => e.StartsWith("plan paid 999.00"));
    }

    [Fact]
    public void BillingProvider_NeedsValidNpi_Ein_AndZipPlusFour()
    {
        var c = Professional() with
        {
            BillingProvider = Billing() with { Npi = "1234567890", TaxId = "123", PostalCode = "78701" }
        };
        var errors = EncounterValidator.Validate(c);

        errors.Should().Contain(e => e.Contains("not a valid NPI"));
        errors.Should().Contain(e => e.Contains("tax ID"));
        errors.Should().Contain(e => e.Contains("ZIP+4"));
    }

    [Fact]
    public void Member_NeedsDobAndAddress()
    {
        var c = Professional() with { Member = Member() with { DateOfBirth = null, AddressLine = null } };
        var errors = EncounterValidator.Validate(c);

        errors.Should().Contain(e => e.Contains("date of birth"));
        errors.Should().Contain(e => e.Contains("member address"));
    }

    [Fact]
    public void Inpatient_NeedsAdmissionDetails_AndRevenueCodes()
    {
        var c = Institutional();
        var bad = c with
        {
            AdmissionDate = null,
            AdmissionTypeCode = null,
            Lines = new[] { c.Lines[0] with { RevenueCode = null }, c.Lines[1] }
        };
        var errors = EncounterValidator.Validate(bad);

        errors.Should().Contain(e => e.Contains("admission date"));
        errors.Should().Contain(e => e.Contains("admission type"));
        errors.Should().Contain(e => e.Contains("revenue code"));
    }

    [Fact]
    public void ReplacementWithoutStateReference_IsCaught()
    {
        EncounterValidator.Validate(Professional() with { FrequencyCode = "7" })
            .Should().Contain(e => e.Contains("REF*F8"));
    }

    [Fact]
    public void DiagnosisPointer_BeyondReportedDiagnoses_IsCaught()
    {
        var c = Professional();
        var bad = c with { Lines = new[] { c.Lines[0] with { DiagnosisPointers = new[] { 3 } }, c.Lines[1] } };

        EncounterValidator.Validate(bad).Should().Contain(e => e.Contains("diagnosis pointer"));
    }

    [Fact]
    public void Dental_IsHeldBack_NotSent()
    {
        EncounterValidator.Validate(Professional() with { Form = EncounterFormType.Dental })
            .Should().ContainSingle(e => e.Contains("837D"));
    }

    [Theory]
    [InlineData("1234567893", true)]
    [InlineData("1245319599", true)]
    [InlineData("1234567890", false)]
    [InlineData("123456789", false)]
    [InlineData("12345678AB", false)]
    public void Npi_CheckDigit(string npi, bool valid) =>
        EncounterValidator.IsValidNpi(npi).Should().Be(valid);
}

public class StateEncounterRouteRegistryTests
{
    [Fact]
    public void Florida_IsAlwaysRouted_ForMedicaid()
    {
        var registry = StateEncounterRouteRegistry.FloridaOnly();

        registry.Resolve("FL", "Medicaid", "tenant-1", out _).Should().Be(StateEncounterRoute.Florida);
        registry.Resolve("fl", "MEDICAID", "tenant-1", out _).Should().Be(StateEncounterRoute.Florida);
        registry.Resolve("FL", "Commercial", "tenant-1", out _).Should().BeNull();
    }

    [Theory]
    [InlineData("TX")]
    [InlineData("CA")]
    public void TexasAndCalifornia_AreOff_WithoutConfiguration(string state)
    {
        StateEncounterRouteRegistry.FloridaOnly().Resolve(state, "Medicaid", "tenant-1", out var reason)
            .Should().BeNull();
        reason.Should().Contain("not enabled");
    }

    [Fact]
    public void EnabledRoute_StillNeedsTheTenantsStateIdentities()
    {
        var registry = Registry(tx: TexasOptions(withTenant: false));

        registry.Resolve("TX", "Medicaid", "tenant-1", out var reason).Should().BeNull();
        reason.Should().Contain("not enrolled");
    }

    [Fact]
    public void IncompleteTenantIdentities_AreNamed()
    {
        var tx = TexasOptions();
        tx.Tenants["tenant-1"].PlanPayerId = null;

        Registry(tx: tx).Resolve("TX", "Medicaid", "tenant-1", out var reason).Should().BeNull();
        reason.Should().Contain("PlanPayerId");
    }

    [Fact]
    public void MissingReceiverIdentity_IsNamed()
    {
        var tx = TexasOptions();
        tx.ReceiverId = null;

        Registry(tx: tx).Resolve("TX", "Medicaid", "tenant-1", out var reason).Should().BeNull();
        reason.Should().Contain("ReceiverId");
    }

    [Fact]
    public void ConfiguredRoutes_AdmitOnlyTheirTenants_AndOnlyMedicaid()
    {
        var registry = Registry(tx: TexasOptions(), ca: CaliforniaOptions());

        registry.Resolve("TX", "Medicaid", "tenant-1", out _).Should().Be(StateEncounterRoute.Texas);
        registry.Resolve("CA", "Medicaid", "TENANT-1", out _).Should().Be(StateEncounterRoute.California);
        registry.Resolve("TX", "Medicaid", "tenant-2", out _).Should().BeNull();
        registry.Resolve("TX", "Exchange", "tenant-1", out _).Should().BeNull();
        registry.Resolve("IL", "Medicaid", "tenant-1", out _).Should().BeNull();
    }
}

public class StateEncounterDeadlineTests
{
    [Theory]
    [InlineData("2026-10-03T10:00:00Z", "2026-11-30T23:59:59Z")]
    [InlineData("2026-10-31T23:00:00Z", "2026-11-30T23:59:59Z")]
    [InlineData("2027-01-15T00:00:00Z", "2027-03-02T23:59:59Z")]
    [InlineData("2028-01-15T00:00:00Z", "2028-03-01T23:59:59Z")]
    public void Texas_DefaultsToThirtyDaysAfterTheAdjudicationMonth(string adjudicated, string expected)
    {
        var deadline = StateEncounterRoute.Texas.ComputeDeadline(
            DateTime.Parse(adjudicated).ToUniversalTime(), null);

        deadline.Should().Be(DateTime.Parse(expected).ToUniversalTime());
    }

    [Fact]
    public void Texas_DoesNotInheritFloridasSixtyDayRule()
    {
        var adjudicated = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);

        StateEncounterRoute.Texas.ComputeDeadline(adjudicated, null)
            .Should().NotBe(StateEncounterRoute.Florida.ComputeDeadline(adjudicated, null));
    }

    [Fact]
    public void ConfiguredRule_OverridesTheDefault()
    {
        var options = new StateEncounterRouteOptions
        {
            DeadlineRule = EncounterDeadlineRule.DaysAfterAdjudication,
            DeadlineDays = 45
        };
        var adjudicated = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);

        StateEncounterRoute.California.ComputeDeadline(adjudicated, options).Should().Be(adjudicated.AddDays(45));
    }
}
