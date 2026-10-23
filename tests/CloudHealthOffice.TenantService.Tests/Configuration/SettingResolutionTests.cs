using CloudHealthOffice.PlanConfiguration;

namespace CloudHealthOffice.TenantService.Tests.Configuration;

/// <summary>The resolution rules shared by tenant-service and the client (plan-configuration.md §§3, 5, 6).</summary>
public class SettingResolutionTests
{
    private static readonly SettingDefinition Days = TestSettings.Days;
    private static readonly DateTime Jan1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private int _version;

    private SettingValue V(SettingLevel level, string scope, string? value, DateTime? from = null, DateTime? to = null, bool locked = false, bool cleared = false) => new()
    {
        TenantId = "t1", Key = Days.Key, Level = level, ScopeId = scope, Value = value, Cleared = cleared, Locked = locked,
        EffectiveFrom = from ?? Jan1, EffectiveTo = to, Version = ++_version,
    };

    private static ResolvedSetting Resolve(SettingScope scope, DateTime on, params SettingValue[] values) =>
        SettingResolver.Resolve(Days, scope, on, values);

    private static readonly SettingScope Acme = new() { LineOfBusiness = "Commercial", Sponsor = "ACME" };
    private static readonly DateTime Jun1 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Nothing_set_gives_the_default_for_the_line_of_business()
    {
        Resolve(Acme, Jun1).Should().Match<ResolvedSetting>(r => r.Value == "60" && r.SourceLevel == SettingLevel.Platform && r.SourceId == "Commercial");
        Resolve(new SettingScope { LineOfBusiness = "Medicare" }, Jun1).Value.Should().Be("30");
        Resolve(SettingScope.Empty, Jun1).Value.Should().Be("30");
    }

    [Fact]
    public void The_most_specific_level_that_sets_a_value_wins()
    {
        var tenant = V(SettingLevel.Tenant, "", "40");
        var lob = V(SettingLevel.LineOfBusiness, "Commercial", "50");
        var sponsor = V(SettingLevel.Sponsor, "ACME", "20");

        Resolve(Acme, Jun1, tenant, lob, sponsor).Should().Match<ResolvedSetting>(r => r.Value == "20" && r.SourceLevel == SettingLevel.Sponsor && r.SourceId == "ACME");
        Resolve(Acme with { Sponsor = "OTHER" }, Jun1, tenant, lob, sponsor).Value.Should().Be("50");
        Resolve(new SettingScope { LineOfBusiness = "Medicare", Sponsor = "OTHER" }, Jun1, tenant, lob, sponsor).Value.Should().Be("40");
        var trace = Resolve(Acme, Jun1, tenant, lob, sponsor).Trace;
        trace.Select(t => t.Level).Should().Equal(SettingLevel.Platform, SettingLevel.Tenant, SettingLevel.LineOfBusiness, SettingLevel.Sponsor);
    }

    [Fact]
    public void A_lock_stops_more_specific_levels_from_overriding()
    {
        var lob = V(SettingLevel.LineOfBusiness, "Commercial", "50", locked: true);
        var sponsor = V(SettingLevel.Sponsor, "ACME", "20");

        var r = Resolve(Acme, Jun1, lob, sponsor);

        r.Value.Should().Be("50");
        r.Locked.Should().BeTrue();
        r.Trace.Last().Note.Should().StartWith("Ignored: locked at LineOfBusiness");
        Resolve(new SettingScope { LineOfBusiness = "Medicare", Sponsor = "ACME" }, Jun1, lob, sponsor).Value.Should().Be("20", "the lock is on Commercial only");
    }

    [Fact]
    public void Floors_and_ranges_clamp_and_say_so()
    {
        var medicaid = new SettingScope { LineOfBusiness = "Medicaid", Sponsor = "STATE" };
        var r = Resolve(medicaid, Jun1, V(SettingLevel.Sponsor, "STATE", "5"));
        r.Value.Should().Be("10");
        r.ClampedBy.Should().Be("State rule X");

        Resolve(Acme, Jun1, V(SettingLevel.Sponsor, "ACME", "120")).Should()
            .Match<ResolvedSetting>(x => x.Value == "90" && x.ClampedBy == "Federal cap Y");
        Resolve(Acme, Jun1, V(SettingLevel.Sponsor, "ACME", "5")).ClampedBy.Should().BeNull("the Medicaid floor does not apply to Commercial");
    }

    [Fact]
    public void Values_are_effective_dated_and_resolved_as_of_a_date()
    {
        var jul1 = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var current = V(SettingLevel.Sponsor, "ACME", "20");
        var next = V(SettingLevel.Sponsor, "ACME", "25", from: jul1);

        Resolve(Acme, Jun1, current, next).Value.Should().Be("20", "the July value is not in force yet");
        Resolve(Acme, jul1, current, next).Value.Should().Be("25");
        Resolve(Acme, new DateTime(2025, 12, 31), current, next).Value.Should().Be("60", "before anything was set, the default");
    }

    [Fact]
    public void A_correction_wins_over_the_version_it_corrects_and_an_end_date_falls_back()
    {
        var mar1 = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var original = V(SettingLevel.Sponsor, "ACME", "20");
        var correction = V(SettingLevel.Sponsor, "ACME", "21"); // same start, later version
        var temporary = V(SettingLevel.Sponsor, "ACME", "45", from: mar1, to: Jun1);

        Resolve(Acme, new DateTime(2026, 2, 1), original, correction, temporary).Value.Should().Be("21");
        Resolve(Acme, new DateTime(2026, 4, 1), original, correction, temporary).Value.Should().Be("45");
        Resolve(Acme, Jun1, original, correction, temporary).Value.Should().Be("21", "the temporary value ended");
    }

    [Fact]
    public void Clearing_a_node_falls_through_to_the_next_level()
    {
        var lob = V(SettingLevel.LineOfBusiness, "Commercial", "50");
        var sponsor = V(SettingLevel.Sponsor, "ACME", "20");
        var cleared = V(SettingLevel.Sponsor, "ACME", null, from: Jun1, cleared: true);

        Resolve(Acme, new DateTime(2026, 5, 31), lob, sponsor, cleared).Value.Should().Be("20");
        Resolve(Acme, Jun1, lob, sponsor, cleared).Value.Should().Be("50");
    }

    [Theory]
    [InlineData(SettingLevel.Tenant, "", "40", null, true)]
    [InlineData(SettingLevel.Platform, "", "40", "why", false)]
    [InlineData(SettingLevel.SponsorPolicy, "P1", "40", "why", false)]
    [InlineData(SettingLevel.LineOfBusiness, "Dental", "40", "why", false)]
    [InlineData(SettingLevel.Sponsor, "", "40", "why", false)]
    [InlineData(SettingLevel.Sponsor, "ACME", "\"forty\"", "why", false)]
    [InlineData(SettingLevel.Sponsor, "ACME", "400", "why", false)]
    [InlineData(SettingLevel.Sponsor, "ACME", "-1", "why", false)]
    [InlineData(SettingLevel.LineOfBusiness, "Medicaid", "5", "why", false)]
    [InlineData(SettingLevel.LineOfBusiness, "Commercial", "5", "why", true)]
    [InlineData(SettingLevel.LineOfBusiness, "Commercial", "95", "why", false)]
    public void Writes_are_checked_for_level_type_range_and_floors(SettingLevel level, string scope, string value, string? reason, bool valid) =>
        SettingValidator.Validate(Days, level, scope, value, false, reason).Should().HaveCount(valid ? 0 : value == "400" ? 2 : 1);

    [Fact]
    public void A_reason_is_required_where_change_control_says_so() =>
        SettingValidator.Validate(PlanSettings.OrphanGraceDays, SettingLevel.Sponsor, "ACME", "3", false, " ").Should()
            .ContainSingle().Which.Should().Contain("reason");

    [Theory]
    [InlineData(SettingLevel.Sponsor, PlanConfigurationPermissions.Sponsor)]
    [InlineData(SettingLevel.SponsorPolicy, PlanConfigurationPermissions.Sponsor)]
    [InlineData(SettingLevel.BenefitPlan, PlanConfigurationPermissions.Product)]
    [InlineData(SettingLevel.LineOfBusiness, PlanConfigurationPermissions.Enterprise)]
    [InlineData(SettingLevel.Tenant, PlanConfigurationPermissions.Enterprise)]
    public void Each_level_has_its_write_permission(SettingLevel level, string permission) =>
        SettingValidator.WritePermissionFor(level).Should().Be(permission);

    [Fact]
    public void The_catalog_defines_the_orphan_grace_period_as_agreed()
    {
        var d = PlanSettingCatalog.Require("enrollment.orphan.graceDays");
        d.DefaultFor("Commercial").Should().Be("7");
        d.DefaultFor("Exchange").Should().Be("0");
        d.DefaultFor("Medicaid").Should().Be("0");
        d.DefaultFor("Medicare").Should().Be("0");
        d.AllowedLevels.Should().BeEquivalentTo([SettingLevel.Tenant, SettingLevel.LineOfBusiness, SettingLevel.Sponsor]);
        d.ChangeControl.Should().Be(ChangeControl.ReasonRequired);
    }

    /// <summary>
    /// The two controls moved out of the tenant document in §12.5. Both decide
    /// whether one person can act alone, so both default to on, sit only at
    /// tenant level, and need a second person to change.
    /// </summary>
    [Theory]
    [InlineData("payment.enforceSeparationOfDuties")]
    [InlineData("personalRepresentative.requireSecondUserActivation")]
    public void The_two_person_controls_are_tenant_level_and_default_to_on(string key)
    {
        var d = PlanSettingCatalog.Require(key);

        d.Type.Should().Be(SettingType.Boolean);
        d.Default.Should().Be("true");
        d.DefaultsByLineOfBusiness.Should().BeEmpty("a control over the tenant's own staff does not vary by line of business");
        d.AllowedLevels.Should().BeEquivalentTo([SettingLevel.Tenant]);
        d.ChangeControl.Should().Be(ChangeControl.TwoPersonApproval);
    }

    [Theory]
    [InlineData("payment.enforceSeparationOfDuties")]
    [InlineData("personalRepresentative.requireSecondUserActivation")]
    public void A_two_person_control_cannot_be_set_below_the_tenant_or_without_a_reason(string key)
    {
        var d = PlanSettingCatalog.Require(key);

        // Not negotiable per sponsor or per program, so tenant-service refuses the write.
        SettingValidator.Validate(d, SettingLevel.Sponsor, "ACME", "false", false, "they asked")
            .Should().ContainSingle().Which.Should().Contain("cannot be set at Sponsor");
        SettingValidator.Validate(d, SettingLevel.Tenant, string.Empty, "false", false, null)
            .Should().ContainSingle().Which.Should().Contain("reason");
        SettingValidator.Validate(d, SettingLevel.Tenant, string.Empty, "\"false\"", false, "typed as a string")
            .Should().ContainSingle();
        SettingValidator.Validate(d, SettingLevel.Tenant, string.Empty, "false", false, "the plan has one finance user")
            .Should().BeEmpty();
    }

    /// <summary>
    /// Both default to the enforced answer, which is what the client returns
    /// when tenant-service cannot be reached, so an outage cannot remove either
    /// control (§12.5).
    /// </summary>
    [Theory]
    [InlineData("payment.enforceSeparationOfDuties")]
    [InlineData("personalRepresentative.requireSecondUserActivation")]
    public void An_unreachable_tenant_service_resolves_a_two_person_control_to_on(string key)
    {
        var fallback = SettingResolver.Resolve(PlanSettingCatalog.Require(key), SettingScope.Empty, Jan1, [], isFallback: true);

        fallback.IsFallback.Should().BeTrue();
        fallback.AsBool().Should().BeTrue();
        fallback.SourceLevel.Should().Be(SettingLevel.Platform);
    }
}
