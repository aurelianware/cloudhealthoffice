using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using EnrollmentImportService.Services.Orphans;
using EnrollmentImportService.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EnrollmentImportService.Tests.Services;

/// <summary>
/// Dependents whose subscriber is not on file (enrollment-operations.md §10,
/// decision 5): held for the grace period, retried when the subscriber
/// arrives, opened to the Matching queue when the grace period ends.
/// </summary>
public partial class EnrollmentImportServiceDependentTests
{
    private const string Commercial = "1";
    private const string Medicaid = "3";

    private static Dependent Orphan(string firstName = "BABY", string subscriberId = "SUB1")
    {
        var d = Child(firstName, "20260115", "021");
        d.SubscriberId = subscriberId;
        d.GroupNumber = "GRP0001";
        return d;
    }

    private static Enrollment834 DependentFile(string batchId, params Dependent[] dependents)
    {
        var batch = new Enrollment834 { BatchId = batchId, FileName = $"{batchId}.834" };
        batch.DependentEnrollments.AddRange(dependents);
        return batch;
    }

    private static Enrollment834 SubscriberFile(string batchId, string subscriberId = "SUB1") =>
        new() { BatchId = batchId, FileName = $"{batchId}.834", Enrollments = { Subscriber(subscriberId, "021") } };

    // ── Hold on arrival ──────────────────────────────────────────────────

    [Fact]
    public async Task ACommercialOrphan_IsHeldForSevenDays_AndIsNotWorkableYet()
    {
        var h = new Harness { LineOfBusiness = Commercial };

        await h.ImportViaHostAsync(DependentFile("D1", Orphan()));

        var e = h.Exceptions.All.Should().ContainSingle().Subject;
        e.ReasonCode.Should().Be(EnrollmentRejectionReasons.SubscriberNotFound);
        e.Status.Should().Be(EnrollmentExceptionStatus.Held);
        e.AutoHeld.Should().BeTrue();
        e.HeldUntil.Should().Be(h.Clock.Now.AddDays(7));
        e.Actions.Should().ContainSingle(a => a.Action == OrphanDependents.AutoHeldAction && a.Actor == OrphanDependents.Actor)
            .Which.Note.Should().Contain("7 day(s)").And.Contain("platform default for Commercial");
        (await h.Exceptions.ListWorkableAsync("t1", h.Clock.Now, 1, 50)).Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData(Medicaid)]
    [InlineData("4")]   // Exchange
    [InlineData(null)]  // sponsor not on file: no line of business, no default
    public async Task WithNoGracePeriod_TheOrphanOpensAtOnce(string? lineOfBusiness)
    {
        var h = new Harness { LineOfBusiness = lineOfBusiness };

        await h.ImportViaHostAsync(DependentFile("D1", Orphan()));

        var e = h.Exceptions.All.Should().ContainSingle().Subject;
        e.Status.Should().Be(EnrollmentExceptionStatus.Open);
        e.AutoHeld.Should().BeFalse();
        e.HeldUntil.Should().BeNull();
        e.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task ASponsorOverride_BeatsThePlanOverride_WhichBeatsTheLineOfBusiness()
    {
        var h = new Harness { LineOfBusiness = Commercial };
        await h.Settings.UpsertAsync(Override("t1", EnrollmentSettingLevel.LineOfBusiness, LinesOfBusiness.Commercial, 10));
        await h.Settings.UpsertAsync(Override("t1", EnrollmentSettingLevel.BenefitPlan, "resolved-plan-id", 4));
        var withPlan = Orphan("ANNA");
        withPlan.Coverage = [new CoverageDetail { InsuranceLineCode = "HLT", PlanCoverageDescription = "PPO" }];

        await h.ImportViaHostAsync(DependentFile("D1", withPlan));
        h.Exceptions.All.Single().HeldUntil.Should().Be(h.Clock.Now.AddDays(4), "the plan's setting is more specific than the line of business");

        await h.Settings.UpsertAsync(Override("t1", EnrollmentSettingLevel.Sponsor, "GRP0001", 2));
        var again = Orphan("BEN");
        again.Coverage = withPlan.Coverage;
        await h.ImportViaHostAsync(DependentFile("D2", again));
        h.Exceptions.All.Single(e => e.MemberName!.StartsWith("BEN")).HeldUntil.Should().Be(h.Clock.Now.AddDays(2));
    }

    [Fact]
    public async Task ASponsorSettingOfZero_OpensTheOrphanEvenOnACommercialLine()
    {
        var h = new Harness { LineOfBusiness = Commercial };
        await h.Settings.UpsertAsync(Override("t1", EnrollmentSettingLevel.Sponsor, "GRP0001", 0));

        await h.ImportViaHostAsync(DependentFile("D1", Orphan()));

        h.Exceptions.All.Single().Status.Should().Be(EnrollmentExceptionStatus.Open);
    }

    // ── Retry when the subscriber arrives ────────────────────────────────

    [Fact]
    public async Task WhenTheSubscriberArrivesInALaterFile_TheOrphanResolvesByItself()
    {
        var h = new Harness { LineOfBusiness = Commercial };
        await h.ImportViaHostAsync(DependentFile("D1", Orphan()));
        h.Clock.Advance(TimeSpan.FromDays(2));

        var result = await h.ImportViaHostAsync(SubscriberFile("S1"));

        result.SuccessCount.Should().Be(1);
        var e = h.Exceptions.All.Single();
        e.Status.Should().Be(EnrollmentExceptionStatus.Resolved);
        e.Resolution.Should().Be(EnrollmentExceptionResolution.Applied);
        e.ResolvedBy.Should().Be(OrphanDependents.Actor);
        e.HeldUntil.Should().BeNull();
        e.ReprocessLeaseUntil.Should().BeNull();
        e.Actions.Should().Contain(a => a.Action == OrphanDependents.AutoRetriedAction && a.Note!.Contains("S1.834"));
        h.Created.Should().ContainSingle(c => c.FirstName == "BABY");
    }

    [Fact]
    public async Task AnOrphanWithNoGracePeriod_IsAlsoRetried_WhenItsSubscriberArrives()
    {
        var h = new Harness { LineOfBusiness = Medicaid };
        await h.ImportViaHostAsync(DependentFile("D1", Orphan()));

        await h.ImportViaHostAsync(SubscriberFile("S1"));

        h.Exceptions.All.Single().Status.Should().Be(EnrollmentExceptionStatus.Resolved);
    }

    [Fact]
    public async Task ASubscriberTermination_IsNotAnArrival()
    {
        var h = new Harness { LineOfBusiness = Commercial };
        await h.ImportViaHostAsync(DependentFile("D1", Orphan()));
        h.Existing.Add("SUB1");

        var term = await h.ImportViaHostAsync(new Enrollment834 { BatchId = "T1", Enrollments = { Subscriber("SUB1", "024") } });

        term.SubscribersOnFile.Should().BeEmpty();
        h.Exceptions.All.Single().Status.Should().Be(EnrollmentExceptionStatus.Held);
    }

    [Fact]
    public async Task TwoTriggersAtOnce_ApplyTheDependentExactlyOnce()
    {
        var h = new Harness { LineOfBusiness = Commercial };
        await h.ImportViaHostAsync(DependentFile("D1", Orphan()));
        h.Existing.Add("SUB1"); // the subscriber is on file now
        var gate = new TaskCompletionSource();
        h.CreateGate = gate.Task;

        var first = h.Retrier.RetryForSubscribersAsync("t1", ["SUB1"], "S1.834");
        var second = await h.Retrier.RetryForSubscribersAsync("t1", ["SUB1"], "S2.834");
        second.Should().Be(0, "the first retry holds the claim while it imports");
        h.Exceptions.All.Single().ReprocessLeaseUntil.Should().NotBeNull();

        gate.SetResult();
        (await first).Should().Be(1);
        h.Created.Count(c => c.FirstName == "BABY").Should().Be(1);
        var e = h.Exceptions.All.Single();
        e.Status.Should().Be(EnrollmentExceptionStatus.Resolved);
        e.ReprocessLeaseUntil.Should().BeNull();
        e.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task ASpecialistCannotActWhileAnAutomaticRetryIsInFlight()
    {
        var h = new Harness { LineOfBusiness = Commercial };
        await h.ImportViaHostAsync(DependentFile("D1", Orphan()));
        h.Existing.Add("SUB1");
        var gate = new TaskCompletionSource();
        h.CreateGate = gate.Task;
        var id = h.Exceptions.All.Single().Id;

        var retry = h.Retrier.RetryAsync("t1", id, "test");
        (await h.OrphanAwareExceptions.NoActionAsync("t1", id, "specialist", "duplicate")).Outcome.Should().Be(ExceptionActionOutcome.Conflict);
        (await h.OrphanAwareExceptions.ReprocessAsync("t1", id, "specialist", null, null, null)).Outcome.Should().Be(ExceptionActionOutcome.Conflict);

        gate.SetResult();
        (await retry).Should().BeTrue();
    }

    [Fact]
    public async Task ASubscriberArrivingInAnotherTenant_DoesNotTouchThisTenantsOrphan()
    {
        var h = new Harness { LineOfBusiness = Commercial };
        await h.ImportViaHostAsync(DependentFile("D1", Orphan()), "t1");

        await h.ImportViaHostAsync(SubscriberFile("S1"), "t2");

        var e = h.Exceptions.All.Single();
        e.TenantId.Should().Be("t1");
        e.Status.Should().Be(EnrollmentExceptionStatus.Held);
        e.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task ARetryThatStillFindsNoSubscriber_KeepsTheHold()
    {
        var h = new Harness { LineOfBusiness = Commercial };
        await h.ImportViaHostAsync(DependentFile("D1", Orphan()));
        var id = h.Exceptions.All.Single().Id;
        var heldUntil = h.Exceptions.All.Single().HeldUntil;

        (await h.Retrier.RetryAsync("t1", id, "test")).Should().BeFalse();

        var e = h.Exceptions.All.Single();
        e.Status.Should().Be(EnrollmentExceptionStatus.Held);
        e.HeldUntil.Should().Be(heldUntil);
        e.Attempts.Should().Be(1);
        e.ReprocessLeaseUntil.Should().BeNull();
    }

    [Fact]
    public async Task ARetryThatFailsForAnotherReason_OpensItForASpecialist()
    {
        var h = new Harness { LineOfBusiness = Commercial };
        await h.ImportViaHostAsync(DependentFile("D1", Orphan()));
        h.ThrowOnExistsFor = "SUB1"; // member-service is down now

        await h.Retrier.RetryAsync("t1", h.Exceptions.All.Single().Id, "test");

        var e = h.Exceptions.All.Single();
        e.ReasonCode.Should().Be(EnrollmentRejectionReasons.DownstreamUnavailable);
        e.Status.Should().Be(EnrollmentExceptionStatus.Open);
        e.HeldUntil.Should().BeNull();
    }

    [Fact]
    public async Task TheSubscribersImportSucceeds_EvenWhenTheOrphanRetryFails()
    {
        var h = new Harness { LineOfBusiness = Commercial };
        await h.ImportViaHostAsync(DependentFile("D1", Orphan()));
        h.Exceptions.FailOrphanLookup = true;

        var result = await h.ImportViaHostAsync(SubscriberFile("S1"));

        result.SuccessCount.Should().Be(1);
        result.Errors.Should().BeEmpty();
        h.Created.Should().ContainSingle(c => c.MemberId == "SUB1");
        h.Exceptions.FailOrphanLookup = false;
        h.Exceptions.All.Single().Status.Should().Be(EnrollmentExceptionStatus.Held, "the sweep is the backstop");
    }

    [Fact]
    public async Task ReprocessingASubscriberException_TriggersItsOrphans()
    {
        var h = new Harness { LineOfBusiness = Medicaid };
        var badSubscriber = Subscriber("SUB1", "021");
        badSubscriber.MaintenanceType = "";
        await h.ImportViaHostAsync(new Enrollment834 { BatchId = "S0", Enrollments = { badSubscriber } });
        await h.ImportViaHostAsync(DependentFile("D1", Orphan()));
        var subscriberException = h.Exceptions.All.Single(e => e.SourceKind == EnrollmentExceptionSourceKind.Subscriber);

        await h.OrphanAwareExceptions.ReprocessAsync("t1", subscriberException.Id, "specialist", Subscriber("SUB1", "021"), null, null);

        h.Exceptions.All.Should().OnlyContain(e => e.Status == EnrollmentExceptionStatus.Resolved);
    }

    // ── The sweep ────────────────────────────────────────────────────────

    private static OrphanSweepService Sweep(Harness h) =>
        new(h.Provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new OrphanSweepOptions()), h.Clock,
            NullLogger<OrphanSweepService>.Instance);

    [Fact]
    public async Task WhenTheGracePeriodEnds_TheSweepRetriesOnce_ThenOpensItWithItsHistory()
    {
        var h = new Harness { LineOfBusiness = Commercial };
        await h.ImportViaHostAsync(DependentFile("D1", Orphan()));
        var sweep = Sweep(h);

        (await sweep.RunOnceAsync(default)).Retried.Should().Be(0, "the grace period has not ended");
        h.Clock.Advance(TimeSpan.FromDays(8));
        var ended = await sweep.RunOnceAsync(default);
        var again = await sweep.RunOnceAsync(default);

        ended.Should().Be(new OrphanSweepResult(1, 1, 0));
        again.Retried.Should().Be(0, "an orphan is swept once");
        var e = h.Exceptions.All.Single();
        e.Status.Should().Be(EnrollmentExceptionStatus.Open);
        e.HeldUntil.Should().BeNull();
        e.Actions.Select(a => a.Action).Should().ContainInOrder(
            OrphanDependents.AutoHeldAction, OrphanDependents.AutoRetriedAction, OrphanDependents.GraceEndedAction);
        (await h.Exceptions.ListWorkableAsync("t1", h.Clock.Now, 1, 50)).Items.Should().ContainSingle();
    }

    [Fact]
    public async Task TheSweep_CatchesASubscriberWhoseArrivalWasMissed()
    {
        var h = new Harness { LineOfBusiness = Commercial };
        await h.ImportViaHostAsync(DependentFile("D1", Orphan()));
        h.Existing.Add("SUB1"); // arrived by a path that did not trigger the retry
        h.Clock.Advance(TimeSpan.FromDays(8));

        (await Sweep(h).RunOnceAsync(default)).Resolved.Should().Be(1);

        h.Exceptions.All.Single().Status.Should().Be(EnrollmentExceptionStatus.Resolved);
    }

    [Fact]
    public async Task ASpecialistsOwnHold_IsNotTreatedAsAGracePeriod()
    {
        var h = new Harness { LineOfBusiness = Commercial };
        await h.ImportViaHostAsync(DependentFile("D1", Orphan()));
        var id = h.Exceptions.All.Single().Id;
        (await h.OrphanAwareExceptions.HoldAsync("t1", id, "specialist", h.Clock.Now.AddDays(3), "asked the sponsor")).Outcome
            .Should().Be(ExceptionActionOutcome.Done);
        h.Clock.Advance(TimeSpan.FromDays(10));

        (await Sweep(h).RunOnceAsync(default)).Retried.Should().Be(0);
        h.Exceptions.All.Single().AutoHeld.Should().BeFalse();
    }

    // ── The settings chain ───────────────────────────────────────────────

    private static EnrollmentSettingOverride Override(string tenant, EnrollmentSettingLevel level, string key, int days) => new()
    {
        Id = EnrollmentSettingOverride.IdFor(tenant, level, key),
        TenantId = tenant,
        Level = level,
        Key = key,
        Values = { [EnrollmentSettingKeys.OrphanGraceDays] = days },
    };

    [Fact]
    public async Task TheChain_MostSpecificLevelWins_ThenTheBuiltInDefault()
    {
        var store = new InMemoryEnrollmentSettingsStore();
        var resolver = new EnrollmentSettingsResolver(store);
        const string key = EnrollmentSettingKeys.OrphanGraceDays;

        (await resolver.ResolveAsync("t1", key, LinesOfBusiness.Commercial, "P1", "G1")).Should()
            .Be(new ResolvedEnrollmentSetting(key, 7, "Default", LinesOfBusiness.Commercial));
        (await resolver.ResolveAsync("t1", key, LinesOfBusiness.Medicaid, null, null)).Value.Should().Be(0);
        (await resolver.ResolveAsync("t1", key, null, null, "G1")).Value.Should().Be(0, "no line of business, no default");

        await store.UpsertAsync(Override("t1", EnrollmentSettingLevel.LineOfBusiness, LinesOfBusiness.Commercial, 5));
        await store.UpsertAsync(Override("t1", EnrollmentSettingLevel.BenefitPlan, "P1", 3));
        await store.UpsertAsync(Override("t1", EnrollmentSettingLevel.Sponsor, "G1", 2));
        await store.UpsertAsync(Override("t2", EnrollmentSettingLevel.Sponsor, "G2", 9));

        (await resolver.ResolveAsync("t1", key, LinesOfBusiness.Commercial, "P1", "G1")).Should()
            .Be(new ResolvedEnrollmentSetting(key, 2, "Sponsor", "G1"));
        (await resolver.ResolveAsync("t1", key, LinesOfBusiness.Commercial, "P1", "OTHER")).Value.Should().Be(3);
        (await resolver.ResolveAsync("t1", key, LinesOfBusiness.Commercial, "P9", "OTHER")).Value.Should().Be(5);
        (await resolver.ResolveAsync("t1", key, LinesOfBusiness.Commercial, null, "G2")).Value.Should().Be(5, "another tenant's sponsor setting");
    }

    [Theory]
    [InlineData("1", LinesOfBusiness.Commercial)]
    [InlineData("3", LinesOfBusiness.Medicaid)]
    [InlineData("4", LinesOfBusiness.Exchange)]
    [InlineData("medicaid", LinesOfBusiness.Medicaid)]
    [InlineData("tricare", LinesOfBusiness.Tricare)]
    [InlineData("0", null)]
    [InlineData("9", null)]
    [InlineData("Dental", null)]
    [InlineData(null, null)]
    public void LinesOfBusiness_AreReadAsSponsorServiceSendsThem(string? value, string? expected) =>
        LinesOfBusiness.Normalize(value).Should().Be(expected);
}
