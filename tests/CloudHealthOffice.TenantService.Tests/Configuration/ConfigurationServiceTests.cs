using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PlanConfiguration;
using TenantService.Configuration;
using TenantService.Controllers;

namespace CloudHealthOffice.TenantService.Tests.Configuration;

public class ConfigurationServiceTests
{
    private readonly InMemoryConfigurationStore _store = new();
    private readonly RecordingPublisher _publisher = new();
    private readonly FixedClock _clock = new(new DateTime(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc));
    private ConfigurationService Service => TestSettings.Service(_store, _publisher, _clock);

    private static TestActor As(string user, string role, string tenant = "t1") => new(user, tenant, role);
    private static readonly string Grace = PlanSettings.OrphanGraceDays.Key;
    private static readonly SettingScope Acme = new() { LineOfBusiness = "Commercial", Sponsor = "ACME" };

    [Theory]
    [InlineData(ChoRolePermissions.AccountManager, SettingLevel.Sponsor, "ACME", ConfigurationOutcome.Applied)]
    [InlineData(ChoRolePermissions.AccountManager, SettingLevel.LineOfBusiness, "Commercial", ConfigurationOutcome.Forbidden)]
    [InlineData(ChoRolePermissions.AccountManager, SettingLevel.Tenant, "", ConfigurationOutcome.Forbidden)]
    [InlineData(ChoRolePermissions.ConfigurationAnalyst, SettingLevel.Sponsor, "ACME", ConfigurationOutcome.Applied)]
    [InlineData(ChoRolePermissions.ConfigurationAnalyst, SettingLevel.LineOfBusiness, "Commercial", ConfigurationOutcome.Forbidden)]
    [InlineData(ChoRolePermissions.ConfigurationManager, SettingLevel.LineOfBusiness, "Commercial", ConfigurationOutcome.Applied)]
    [InlineData(ChoRolePermissions.ConfigurationManager, SettingLevel.Tenant, "", ConfigurationOutcome.Applied)]
    [InlineData(ChoRolePermissions.TenantAdmin, SettingLevel.Tenant, "", ConfigurationOutcome.Applied)]
    [InlineData(ChoRolePermissions.EnrollmentSpecialist, SettingLevel.Sponsor, "ACME", ConfigurationOutcome.Forbidden)]
    [InlineData(ChoRolePermissions.ClaimsSupervisor, SettingLevel.Sponsor, "ACME", ConfigurationOutcome.Forbidden)]
    [InlineData(ChoRolePermissions.Underwriter, SettingLevel.Sponsor, "ACME", ConfigurationOutcome.Forbidden)]
    public async Task Writes_need_the_permission_for_their_level(string role, SettingLevel level, string scope, ConfigurationOutcome expected)
    {
        var result = await Service.WriteAsync(As("u1", role), Grace, level, scope, new SettingWrite { Value = "3", Reason = "Contract" }, clear: false);
        result.Outcome.Should().Be(expected, string.Join("; ", result.Errors));
    }

    [Fact]
    public async Task A_write_is_resolved_explained_versioned_audited_and_published()
    {
        var admin = As("cm", ChoRolePermissions.ConfigurationManager);
        await Service.WriteAsync(admin, Grace, SettingLevel.LineOfBusiness, "commercial", new SettingWrite { Value = "10", Reason = "Weekly files" }, false);
        await Service.WriteAsync(admin, Grace, SettingLevel.Sponsor, " ACME ", new SettingWrite { Value = "3", Reason = "Daily files" }, false);
        await Service.WriteAsync(admin, Grace, SettingLevel.Sponsor, "ACME", new SettingWrite { Value = "4", Reason = "Corrected" }, false);

        var r = (await Service.ResolveAsync("t1", Grace, Acme, null))!;
        r.Value.Should().Be("4");
        r.SourceLevel.Should().Be(SettingLevel.Sponsor);
        r.Trace.Should().HaveCount(4);
        (await Service.ResolveAsync("t1", Grace, Acme with { Sponsor = "OTHER" }, null))!.Value.Should().Be("10");

        var history = (await Service.HistoryAsync("t1", Grace, SettingLevel.Sponsor, "ACME"))!;
        history.Select(v => (v.Version, v.Value, v.Reason)).Should().Equal((1, "3", "Daily files"), (2, "4", "Corrected"));
        _store.Changes.Should().HaveCount(3);
        _store.Changes.Last().Should().Match<ConfigurationChangeRecord>(c => c.Before == "3" && c.After == "4" && c.Actor == "cm" && c.Action == "Set");
        _publisher.Published.Select(e => (e.Key, e.Level, e.ScopeId, e.Version)).Should()
            .Equal((Grace, SettingLevel.LineOfBusiness, "Commercial", 1), (Grace, SettingLevel.Sponsor, "ACME", 1), (Grace, SettingLevel.Sponsor, "ACME", 2));
    }

    [Fact]
    public async Task Reads_and_writes_are_confined_to_the_callers_tenant()
    {
        await Service.WriteAsync(As("cm", ChoRolePermissions.ConfigurationManager, "t1"), Grace, SettingLevel.Sponsor, "ACME",
            new SettingWrite { Value = "3", Reason = "x" }, false);

        (await Service.ResolveAsync("t2", Grace, Acme, null))!.Value.Should().Be("7", "t2 sees only the default");
        (await Service.HistoryAsync("t2", Grace, SettingLevel.Sponsor, "ACME"))!.Should().BeEmpty();
        (await Service.OverridesAsync("t2", Grace, SettingLevel.Tenant, null, null))!.Overrides.Should().BeEmpty();
        (await Service.ChangesAsync("t2", null, 10)).Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_writes_are_refused_with_reasons()
    {
        var cm = As("cm", ChoRolePermissions.ConfigurationManager);
        (await Service.WriteAsync(cm, Grace, SettingLevel.Sponsor, "ACME", new SettingWrite { Value = "3" }, false)).Errors.Should().Contain(e => e.Contains("reason"));
        (await Service.WriteAsync(cm, Grace, SettingLevel.Sponsor, "ACME", new SettingWrite { Value = "120", Reason = "x" }, false)).Outcome.Should().Be(ConfigurationOutcome.Invalid);
        (await Service.WriteAsync(cm, "test.days", SettingLevel.LineOfBusiness, "Medicaid", new SettingWrite { Value = "5" }, false)).Errors.Should()
            .ContainSingle(e => e.Contains("State rule X"));
        (await Service.WriteAsync(cm, Grace, SettingLevel.Sponsor, "ACME",
            new SettingWrite { Value = "3", Reason = "x", EffectiveFrom = new DateTime(2026, 12, 1), EffectiveTo = new DateTime(2026, 11, 1) }, false))
            .Outcome.Should().Be(ConfigurationOutcome.Invalid);
        (await Service.WriteAsync(cm, "no.such.key", SettingLevel.Tenant, "", new SettingWrite { Value = "1" }, false)).Outcome.Should().Be(ConfigurationOutcome.NotFound);
        _store.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task A_future_value_and_a_clear_take_effect_on_their_dates()
    {
        var cm = As("cm", ChoRolePermissions.ConfigurationManager);
        var jan1 = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await Service.WriteAsync(cm, Grace, SettingLevel.Sponsor, "ACME", new SettingWrite { Value = "3", Reason = "now" }, false);
        await Service.WriteAsync(cm, Grace, SettingLevel.Sponsor, "ACME", new SettingWrite { Value = "14", Reason = "renewal", EffectiveFrom = jan1 }, false);
        await Service.WriteAsync(cm, Grace, SettingLevel.Sponsor, "ACME", new SettingWrite { Reason = "back to the line", EffectiveFrom = jan1.AddMonths(6) }, clear: true);

        (await Service.ResolveAsync("t1", Grace, Acme, null))!.Value.Should().Be("3");
        (await Service.ResolveAsync("t1", Grace, Acme, jan1))!.Value.Should().Be("14");
        (await Service.ResolveAsync("t1", Grace, Acme, jan1.AddMonths(7)))!.Value.Should().Be("7");
        var overrides = (await Service.OverridesAsync("t1", Grace, SettingLevel.LineOfBusiness, "Commercial", null))!.Overrides;
        overrides.Should().ContainSingle().Which.Should().Match<SettingOverride>(o => o.Value == "3" && o.NextChangeOn == jan1 && o.NextValue == "14");
    }

    [Fact]
    public async Task A_lock_holds_against_a_sponsor_override()
    {
        var cm = As("cm", ChoRolePermissions.ConfigurationManager);
        await Service.WriteAsync(cm, Grace, SettingLevel.LineOfBusiness, "Medicaid", new SettingWrite { Value = "0", Reason = "State file is complete", Locked = true }, false);
        await Service.WriteAsync(As("am", ChoRolePermissions.AccountManager), Grace, SettingLevel.Sponsor, "STATE", new SettingWrite { Value = "5", Reason = "x" }, false);

        var r = (await Service.ResolveAsync("t1", Grace, new SettingScope { LineOfBusiness = "Medicaid", Sponsor = "STATE" }, null))!;
        r.Value.Should().Be("0");
        r.Locked.Should().BeTrue();
    }

    [Fact]
    public async Task Two_person_changes_wait_and_cannot_be_approved_by_their_requester()
    {
        var requester = As("alice", ChoRolePermissions.ConfigurationManager);
        var pending = await Service.WriteAsync(requester, "test.control", SettingLevel.Tenant, "_", new SettingWrite { Value = "false", Reason = "Small team" }, false);
        pending.Outcome.Should().Be(ConfigurationOutcome.Pending);
        _store.Values.Should().BeEmpty("nothing applies until approved");
        (await Service.ResolveAsync("t1", "test.control", SettingScope.Empty, null))!.Value.Should().Be("true");

        var id = pending.Pending!.Id;
        (await Service.ApproveAsync(requester, id, null)).Outcome.Should().Be(ConfigurationOutcome.Forbidden);
        (await Service.ApproveAsync(As("bob", ChoRolePermissions.AccountManager), id, null)).Outcome.Should().Be(ConfigurationOutcome.Forbidden, "no config:approve");
        (await Service.ApproveAsync(As("carol", ChoRolePermissions.Underwriter, "t2"), id, null)).Outcome.Should().Be(ConfigurationOutcome.NotFound, "another tenant's change");

        var approved = await Service.ApproveAsync(As("dave", ChoRolePermissions.Underwriter), id, "ok");
        approved.Outcome.Should().Be(ConfigurationOutcome.Applied);
        approved.Value!.Should().Match<SettingValue>(v => v.SetBy == "alice" && v.ApprovedBy == "dave" && v.ApprovedFromPendingId == id);
        (await Service.ResolveAsync("t1", "test.control", SettingScope.Empty, null))!.AsBool().Should().BeFalse();
        (await Service.ApproveAsync(As("erin", ChoRolePermissions.ConfigurationManager), id, null)).Outcome.Should().Be(ConfigurationOutcome.Conflict);
        _store.Changes.Select(c => c.Action).Should().Equal("Requested", "Approved");
    }

    [Fact]
    public async Task A_rejected_change_never_applies()
    {
        var pending = await Service.WriteAsync(As("alice", ChoRolePermissions.ConfigurationManager), "test.control", SettingLevel.Tenant, "", new SettingWrite { Value = "false", Reason = "r" }, false);
        (await Service.RejectAsync(As("dave", ChoRolePermissions.Underwriter), pending.Pending!.Id, "Not without a second reviewer")).Outcome.Should().Be(ConfigurationOutcome.Applied);
        (await Service.ApproveAsync(As("erin", ChoRolePermissions.Underwriter), pending.Pending.Id, null)).Outcome.Should().Be(ConfigurationOutcome.Conflict);
        _store.Values.Should().BeEmpty();
        (await Service.PendingAsync("t1")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_publish_failure_does_not_undo_the_change()
    {
        _publisher.Fail = true;
        var result = await Service.WriteAsync(As("cm", ChoRolePermissions.ConfigurationManager), Grace, SettingLevel.Sponsor, "ACME", new SettingWrite { Value = "3", Reason = "x" }, false);
        result.Outcome.Should().Be(ConfigurationOutcome.Applied);
        (await Service.ResolveAsync("t1", Grace, Acme, null))!.Value.Should().Be("3");
    }

    [Theory]
    [InlineData("tenant", SettingLevel.Tenant)]
    [InlineData("lineOfBusiness", SettingLevel.LineOfBusiness)]
    [InlineData("sponsor", SettingLevel.Sponsor)]
    [InlineData("policy", SettingLevel.SponsorPolicy)]
    [InlineData("nope", null)]
    public void Route_levels_parse(string text, SettingLevel? expected) => ConfigurationController.ParseLevel(text).Should().Be(expected);

    // ── Approvals that stop part-way (review fix) ─────────────────────────

    private async Task<string> RequestControlChangeAsync()
    {
        var pending = await Service.WriteAsync(As("alice", ChoRolePermissions.ConfigurationManager), "test.control", SettingLevel.Tenant, "",
            new SettingWrite { Value = "false", Reason = "Small team" }, false);
        return pending.Pending!.Id;
    }

    [Fact]
    public async Task An_approval_that_stops_before_writing_its_version_is_finished_by_approving_again()
    {
        var id = await RequestControlChangeAsync();
        _store.FailNextAppend = true;

        var first = () => Service.ApproveAsync(As("dave", ChoRolePermissions.Underwriter), id, "ok");
        await first.Should().ThrowAsync<InvalidOperationException>();
        _store.Pending.Single().Status.Should().Be(PendingChangeStatus.Approving, "claimed, never shown as Approved without a version");
        _store.Values.Should().BeEmpty();
        (await Service.PendingAsync("t1")).Should().ContainSingle("an unfinished approval stays in the queue");
        (await Service.RejectAsync(As("erin", ChoRolePermissions.Underwriter), id, "no")).Outcome
            .Should().Be(ConfigurationOutcome.Conflict, "an approval under way can only be finished");
        (await Service.ApproveAsync(As("alice", ChoRolePermissions.ConfigurationManager), id, null)).Outcome
            .Should().Be(ConfigurationOutcome.Forbidden, "the requester still cannot finish it");

        var finished = await Service.ApproveAsync(As("erin", ChoRolePermissions.Underwriter), id, null);

        finished.Outcome.Should().Be(ConfigurationOutcome.Applied);
        finished.Value!.ApprovedBy.Should().Be("dave", "the approver of record is the one who claimed it");
        _store.Values.Should().ContainSingle();
        var pending = _store.Pending.Single();
        pending.Status.Should().Be(PendingChangeStatus.Approved);
        pending.AppliedVersionId.Should().Be(finished.Value.Id);
        _store.Changes.Count(c => c.Action == "Approved").Should().Be(1);
    }

    [Fact]
    public async Task An_approval_that_stops_after_writing_its_version_completes_without_a_second_version()
    {
        var id = await RequestControlChangeAsync();
        _store.FailNextComplete = true;

        var first = () => Service.ApproveAsync(As("dave", ChoRolePermissions.Underwriter), id, "ok");
        await first.Should().ThrowAsync<InvalidOperationException>();
        _store.Values.Should().ContainSingle("the version was written");
        _store.Pending.Single().Status.Should().Be(PendingChangeStatus.Approving);

        var finished = await Service.ApproveAsync(As("dave", ChoRolePermissions.Underwriter), id, null);

        finished.Outcome.Should().Be(ConfigurationOutcome.Applied);
        _store.Values.Should().ContainSingle("finishing reuses the version already written");
        _store.Pending.Single().Status.Should().Be(PendingChangeStatus.Approved);
        _store.Pending.Single().AppliedVersion.Should().Be(1);
        _store.Changes.Count(c => c.Action == "Approved").Should().Be(1, "the approval's change record has a fixed id");
        _publisher.Published.Should().ContainSingle();
        (await Service.ApproveAsync(As("erin", ChoRolePermissions.Underwriter), id, null)).Outcome.Should().Be(ConfigurationOutcome.Conflict);
    }

    // ── Sponsor writes checked against the sponsor's line (review fix) ─────

    [Fact]
    public async Task A_sponsor_write_below_its_lines_floor_is_refused_when_the_line_is_known()
    {
        var lines = new FakeSponsorLines { Lines = { ["STATE1"] = LinesOfBusiness.Medicaid, ["ACME"] = LinesOfBusiness.Commercial } };
        var service = TestSettings.Service(_store, _publisher, _clock, lines);
        var am = As("am", ChoRolePermissions.AccountManager);

        var refused = await service.WriteAsync(am, "test.days", SettingLevel.Sponsor, "STATE1", new SettingWrite { Value = "5", Reason = "x" }, false);
        refused.Outcome.Should().Be(ConfigurationOutcome.Invalid);
        refused.FloorCheck.Should().Be(FloorCheck.CheckedAgainstSponsorLine);
        refused.Errors.Should().Contain(e => e.Contains("State rule X"));

        var commercial = await service.WriteAsync(am, "test.days", SettingLevel.Sponsor, "ACME", new SettingWrite { Value = "5", Reason = "x" }, false);
        commercial.Outcome.Should().Be(ConfigurationOutcome.Applied, "the Medicaid floor does not apply to a commercial sponsor");
        commercial.FloorCheck.Should().Be(FloorCheck.CheckedAgainstSponsorLine);
    }

    [Fact]
    public async Task When_the_sponsors_line_cannot_be_found_the_write_is_accepted_and_clamped_at_resolution()
    {
        var lines = new FakeSponsorLines { Fail = true };
        var service = TestSettings.Service(_store, _publisher, _clock, lines);
        var am = As("am", ChoRolePermissions.AccountManager);

        var accepted = await service.WriteAsync(am, "test.days", SettingLevel.Sponsor, "STATE1", new SettingWrite { Value = "5", Reason = "x" }, false);
        accepted.Outcome.Should().Be(ConfigurationOutcome.Applied);
        accepted.FloorCheck.Should().Be(FloorCheck.LookupFailed);
        var resolved = await service.ResolveAsync("t1", "test.days", new SettingScope { LineOfBusiness = LinesOfBusiness.Medicaid, Sponsor = "STATE1" }, null);
        resolved!.Value.Should().Be("10", "clamped to the Medicaid floor");
        resolved.ClampedBy.Should().NotBeNull();

        lines.Fail = false;
        (await service.WriteAsync(am, "test.days", SettingLevel.Sponsor, "UNKNOWN", new SettingWrite { Value = "5", Reason = "x" }, false))
            .FloorCheck.Should().Be(FloorCheck.SponsorNotFound);
        var calls = lines.Calls;
        (await service.WriteAsync(am, Grace, SettingLevel.Sponsor, "ACME", new SettingWrite { Value = "3", Reason = "x" }, false))
            .FloorCheck.Should().Be(FloorCheck.NotApplicable, "the orphan grace setting has no line-specific floors");
        lines.Calls.Should().Be(calls, "no lookup when no floor depends on the line");
    }
}
