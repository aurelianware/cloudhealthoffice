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
    public void Dental_ValidEncounter_Passes_OnEveryRoute()
    {
        EncounterValidator.Validate(Dental()).Should().BeEmpty();
        EncounterValidator.Validate(Dental(), StateEncounterRoute.Texas).Should().BeEmpty();
        EncounterValidator.Validate(Dental() with { Diagnoses = Array.Empty<EncounterDiagnosis>(),
                Lines = new[] { Dental().Lines[0] }, TotalCharge = 55m, PlanPaidAmount = 30m })
            .Should().BeEmpty("837D diagnoses are situational");
    }

    [Fact]
    public void Dental_RefusesBadCdtToothSurfaceAndAreaCodes()
    {
        var claim = Dental();
        var bad = claim with
        {
            Lines = new[]
            {
                claim.Lines[0] with { ProcedureCode = "99213", ProcedureQualifier = "HC" },
                claim.Lines[1] with
                {
                    Teeth = new[] { new EncounterTooth("33", new[] { "M" }), new EncounterTooth("4", new[] { "X" }) },
                    OralCavityDesignations = new[] { "99" },
                    ProsthesisCrownInlayCode = "Z",
                    DiagnosisPointers = new[] { 2 }
                }
            }
        };

        var errors = EncounterValidator.Validate(bad);

        errors.Should().Contain(e => e.Contains("line 1") && e.Contains("qualifier must be AD"))
            .And.Contain(e => e.Contains("'99213' is not a CDT procedure code"))
            .And.Contain(e => e.Contains("tooth '33'"))
            .And.Contain(e => e.Contains("surface 'X'"))
            .And.Contain(e => e.Contains("oral cavity designation '99'"))
            .And.Contain(e => e.Contains("prosthesis, crown or inlay code 'Z'"))
            .And.Contain(e => e.Contains("line 2") && e.Contains("diagnosis pointer"));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("32", true)]
    [InlineData("A", true)]
    [InlineData("T", true)]
    [InlineData("51", true)]
    [InlineData("82", true)]
    [InlineData("AS", true)]
    [InlineData("0", false)]
    [InlineData("33", false)]
    [InlineData("U", false)]
    [InlineData("83", false)]
    [InlineData("", false)]
    public void ToothDesignation_FollowsTheAdaUniversalSystem(string tooth, bool valid) =>
        EncounterValidator.IsValidTooth(tooth).Should().Be(valid);

    [Theory]
    [InlineData("1234567893", true)]
    [InlineData("1245319599", true)]
    [InlineData("1234567890", false)]
    [InlineData("123456789", false)]
    [InlineData("12345678AB", false)]
    public void Npi_CheckDigit(string npi, bool valid) =>
        EncounterValidator.IsValidNpi(npi).Should().Be(valid);
}

public class TexasEncounterValidatorTests
{
    [Fact]
    public void Texas_RefusesAClaimNumberLongerThanTmhpEchoesBack()
    {
        var claim = StateRouteTestData.Professional() with { PatientControlNumber = "CLM-2026-11-000000000123" };

        EncounterValidator.Validate(claim, StateEncounterRoute.Texas)
            .Should().ContainSingle(e => e.Contains("longer than the 20 characters") && e.Contains("TMHP"));
        EncounterValidator.Validate(claim, StateEncounterRoute.California).Should().BeEmpty();
        EncounterValidator.Validate(claim).Should().BeEmpty();
    }

    [Fact]
    public void Texas_InpatientEncounter_NeedsPresentOnAdmissionOnEveryDiagnosis()
    {
        var claim = StateRouteTestData.Institutional() with
        {
            Diagnoses = new[] { new EncounterDiagnosis("J18.9", "Y"), new EncounterDiagnosis("E11.9") }
        };

        EncounterValidator.Validate(claim, StateEncounterRoute.Texas)
            .Should().ContainSingle(e => e.Contains("E11.9") && e.Contains("present-on-admission"));
        EncounterValidator.Validate(claim, StateEncounterRoute.California).Should().BeEmpty();
        EncounterValidator.Validate(StateRouteTestData.Institutional(), StateEncounterRoute.Texas).Should().BeEmpty();
    }

    [Fact]
    public void Texas_PresentOnAdmission_RejectsAnUnknownValue()
    {
        var claim = StateRouteTestData.Institutional() with
        {
            Diagnoses = new[] { new EncounterDiagnosis("J18.9", "X") }
        };

        EncounterValidator.Validate(claim, StateEncounterRoute.Texas).Should().Contain(e => e.Contains("J18.9"));
    }
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

public class DentalEncounterMapperTests
{
    [Fact]
    public void DentalClaim_MapsToAn837DEncounter_WithTeethAreasAndCdtQualifier()
    {
        var claim = new ClaimRecord
        {
            Id = "c-d1",
            ClaimNumber = "DEN-1",
            ClaimType = ClaimRecordType.Dental,
            MemberId = "M1",
            BillingProviderNPI = StateRouteTestData.BillingNpi,
            PlaceOfServiceCode = "11",
            TotalChargeAmount = 160m,
            ServiceDateFrom = new DateTime(2026, 10, 7),
            ServiceDateTo = new DateTime(2026, 10, 7),
            AdjudicatedDate = new DateTime(2026, 10, 21),
            ClaimLines = new()
            {
                new ClaimLineRecord
                {
                    LineNumber = 1, ProcedureCode = "D2392", ChargeAmount = 160m,
                    Teeth = new() { new ToothRecord { ToothCode = "30", Surfaces = new() { "M", "O" } } },
                    OralCavityDesignations = new() { "40" },
                    ProsthesisCrownInlayCode = "I",
                    AdjudicationResult = new LineAdjudicationRecord { PaidAmount = 88m }
                }
            }
        };

        var encounter = EncounterClaimMapper.Map("s-1", claim, null, null);

        encounter.Form.Should().Be(EncounterFormType.Dental);
        var line = encounter.Lines.Should().ContainSingle().Subject;
        line.ProcedureQualifier.Should().Be("AD");
        line.Teeth.Should().ContainSingle().Which.Should().Match<EncounterTooth>(t =>
            t.ToothCode == "30" && t.Surfaces.SequenceEqual(new[] { "M", "O" }));
        line.OralCavityDesignations.Should().Equal("40");
        line.ProsthesisCrownInlayCode.Should().Be("I");
    }
}
