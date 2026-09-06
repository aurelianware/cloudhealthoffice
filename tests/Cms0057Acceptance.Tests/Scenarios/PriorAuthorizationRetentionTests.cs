using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services.Retention;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// PAT-03 — durable prior-authorization data retention, executed against the
/// REAL PriorAuthorizationRetentionPolicy and the REAL
/// PriorAuthorizationRetentionWorker over the acceptance suite's in-memory
/// authorization repository.
///
/// The rule under test: a prior authorization is retained for the configured
/// period after its LAST STATUS CHANGE, and becomes purgeable only when it is
/// both operationally terminal and past that boundary. CMS-0057-F states a
/// MINIMUM of one year; the configured default is longer, and the floor is
/// enforced rather than trusted.
///
/// Traceability:
///   policy   src/services/authorization-service/Services/Retention/PriorAuthorizationRetentionPolicy.cs
///   worker   src/services/authorization-service/Services/Retention/PriorAuthorizationRetentionWorker.cs
///   store    src/services/authorization-service/Repositories/AuthorizationRepository.cs
///   inquiry  src/services/fhir-service/Services/PriorAuthorizationInquiryService.cs (PAS-04)
/// </summary>
[Trait("Backend", "Replace")]
public class PriorAuthorizationRetentionTests
{
    private const string TenantA = AcceptanceContext.TenantId;
    private const string TenantB = "other-tenant";

    /// <summary>
    /// Evaluated per use, NOT a fixed date. The worker decides eligibility from
    /// DateTime.UtcNow, so records seeded relative to a hard-coded "now" would
    /// drift across the retention boundary as real time passed and these tests
    /// would start failing on a date nobody chose.
    /// </summary>
    private static DateTime Now => DateTime.UtcNow;

    private static PriorAuthorizationRetentionOptions Options(
        TimeSpan? period = null, bool dryRun = false, int max = 500) => new()
    {
        Enabled = true,
        RetentionPeriod = period ?? TimeSpan.FromDays(365 * 6),
        MaxRecordsPerTenantPerSweep = max,
        DryRun = dryRun,
    };

    private static IPriorAuthorizationRetentionPolicy Policy(PriorAuthorizationRetentionOptions options)
        => new PriorAuthorizationRetentionPolicy(Microsoft.Extensions.Options.Options.Create(options));

    private static Authorization Auth(
        AuthorizationStatus status,
        DateTime lastStatusChange,
        string tenant = TenantA,
        string? id = null,
        string? authNumber = null,
        bool withHistory = true)
    {
        var auth = new Authorization
        {
            Id = id ?? Guid.NewGuid().ToString(),
            TenantId = tenant,
            AuthorizationNumber = authNumber ?? $"PAS-{Guid.NewGuid().ToString("N")[..8]}",
            MemberId = "pat-001",
            RequestingProviderNPI = "1234567890",
            Status = status,
            SubmittedDate = lastStatusChange.AddDays(-30),
            ReviewedDate = lastStatusChange,
            LastUpdatedDate = lastStatusChange,
        };

        if (withHistory)
        {
            auth.StatusHistory.Add(new AuthorizationStatusChange
            {
                Status = status,
                ChangedAt = lastStatusChange,
            });
        }

        return auth;
    }

    private static (PriorAuthorizationRetentionWorker Worker, InMemoryAuthorizationRepository Store)
        WorkerOver(PriorAuthorizationRetentionOptions options, params Authorization[] seed)
    {
        var store = new InMemoryAuthorizationRepository();
        foreach (var a in seed)
            store.CreateAsync(a).GetAwaiter().GetResult();

        var services = new ServiceCollection();
        services.AddSingleton<IAuthorizationRepository>(store);
        services.AddSingleton<IPriorAuthorizationRetentionPolicy>(Policy(options));

        var provider = services.BuildServiceProvider();

        var worker = new PriorAuthorizationRetentionWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor(options),
            AcceptanceContext.Logger<PriorAuthorizationRetentionWorker>());

        return (worker, store);
    }

    // ── The rule itself ────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public void PAT03_Replace_RetentionIsAnchoredOnTheLastStatusChange()
    {
        var policy = Policy(Options(period: TimeSpan.FromDays(365)));
        var lastChange = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        var auth = Auth(AuthorizationStatus.Approved, lastChange);

        policy.RetentionAnchorUtc(auth).Should().Be(lastChange);
        policy.RetentionUntilUtc(auth).Should().Be(lastChange.AddDays(365));
    }

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public void PAT03_Replace_ConfiguredPeriodCannotGoBelowTheRegulatoryFloor()
    {
        // CMS-0057-F states a MINIMUM. A deployment that misconfigures a shorter
        // period gets the floor, not the shorter value — configuration cannot
        // shorten retention past the regulation.
        var options = Options(period: TimeSpan.FromDays(30));

        options.EffectiveRetentionPeriod.Should().Be(PriorAuthorizationRetentionOptions.RegulatoryFloor);
        PriorAuthorizationRetentionOptions.RegulatoryFloor.Should().Be(TimeSpan.FromDays(365));

        var policy = Policy(options);
        var auth = Auth(AuthorizationStatus.Approved, Now.AddDays(-200));

        // 200 days old, floor is 365: still retained despite the 30-day config.
        policy.IsPurgeEligible(auth, Now).Should().BeFalse();
    }

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public void PAT03_Replace_DefaultRetentionExceedsTheRegulatoryMinimum()
    {
        // The default is not the bare minimum: CHO retains other regulated
        // records for six years, and prior-auth data should not become the
        // shortest-lived regulated data in the platform by default.
        new PriorAuthorizationRetentionOptions().EffectiveRetentionPeriod
            .Should().BeGreaterThan(PriorAuthorizationRetentionOptions.RegulatoryFloor);
    }

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public void PAT03_Replace_MissingStatusHistoryFallsBackToALifecycleDateNotAWriteTimestamp()
    {
        // Records written outside the CHO-native backend can carry no history.
        // The fallback is the decision date, never LastUpdatedDate — every write
        // touches that, so an unrelated edit would silently move the boundary.
        var policy = Policy(Options());
        var reviewed = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        var auth = Auth(AuthorizationStatus.Denied, reviewed, withHistory: false);
        auth.LastUpdatedDate = Now; // an unrelated later write

        policy.RetentionAnchorUtc(auth).Should().Be(reviewed,
            "the anchor is a lifecycle fact, not the last time a row was touched");
    }

    // ── Retained before the deadline ───────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public async Task PAT03_Replace_TerminalRecordInsideRetention_IsNotPurged()
    {
        var options = Options(period: TimeSpan.FromDays(365));
        var auth = Auth(AuthorizationStatus.Approved, Now.AddDays(-100));
        var (worker, store) = WorkerOver(options, auth);

        var summary = await worker.SweepAsync(options, CancellationToken.None);

        summary.Purged.Should().Be(0);
        (await store.GetByIdAsync(auth.Id)).Should().NotBeNull();
    }

    // ── Purged after the deadline ──────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public async Task PAT03_Replace_TerminalRecordBeyondRetention_IsPurged()
    {
        var options = Options(period: TimeSpan.FromDays(365));
        var auth = Auth(AuthorizationStatus.Approved, Now.AddDays(-800));
        var (worker, store) = WorkerOver(options, auth);

        var summary = await worker.SweepAsync(options, CancellationToken.None);

        summary.Purged.Should().Be(1);
        (await store.GetByIdAsync(auth.Id)).Should().BeNull();
    }

    [Theory]
    [Trait("Scenario", "PAT-03")]
    [InlineData(AuthorizationStatus.Approved)]
    [InlineData(AuthorizationStatus.Modified)]
    [InlineData(AuthorizationStatus.Denied)]
    [InlineData(AuthorizationStatus.Expired)]
    [InlineData(AuthorizationStatus.Cancelled)]
    public async Task PAT03_Replace_EveryTerminalStatusIsEventuallyPurgeable(AuthorizationStatus status)
    {
        var options = Options(period: TimeSpan.FromDays(365));
        var auth = Auth(status, Now.AddDays(-800));
        var (worker, store) = WorkerOver(options, auth);

        await worker.SweepAsync(options, CancellationToken.None);

        (await store.GetByIdAsync(auth.Id)).Should().BeNull();
    }

    // ── Non-terminal safety ────────────────────────────────────────────────────

    [Theory]
    [Trait("Scenario", "PAT-03")]
    [InlineData(AuthorizationStatus.Submitted)]
    [InlineData(AuthorizationStatus.InReview)]
    [InlineData(AuthorizationStatus.Pended)]
    public async Task PAT03_Replace_OpenAuthorizationsAreNeverPurgedHoweverOld(AuthorizationStatus status)
    {
        // An ancient timestamp is not permission to delete an authorization that
        // is still operationally live — a pended decision may still be waiting on
        // information no matter how long it has waited.
        var options = Options(period: TimeSpan.FromDays(365));
        var auth = Auth(status, Now.AddDays(-5000));
        var (worker, store) = WorkerOver(options, auth);

        var summary = await worker.SweepAsync(options, CancellationToken.None);

        summary.Purged.Should().Be(0);
        (await store.GetByIdAsync(auth.Id)).Should().NotBeNull();
        Policy(options).IsPurgeEligible(auth, Now).Should().BeFalse();
    }

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public void PAT03_Replace_TheOpenTerminalSplitIsDefinedOnceAndCoversEveryStatus()
    {
        // Retention is destructive and keyed entirely on this split, so it is
        // total over the enum and defined in one place.
        foreach (var status in Enum.GetValues<AuthorizationStatus>())
            status.IsOpen().Should().Be(!status.IsTerminal());

        AuthorizationStatus.Submitted.IsOpen().Should().BeTrue();
        AuthorizationStatus.InReview.IsOpen().Should().BeTrue();
        AuthorizationStatus.Pended.IsOpen().Should().BeTrue();
        AuthorizationStatus.Approved.IsTerminal().Should().BeTrue();
        AuthorizationStatus.Cancelled.IsTerminal().Should().BeTrue();
    }

    // ── Reading does not extend retention ──────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public async Task PAT03_Replace_ReadingARecordDoesNotMoveItsRetentionBoundary()
    {
        // Inquiry is not a lifecycle event. A record read repeatedly right up to
        // its boundary is still purged exactly when it was originally due.
        var options = Options(period: TimeSpan.FromDays(365));
        var auth = Auth(AuthorizationStatus.Approved, Now.AddDays(-800));
        var (worker, store) = WorkerOver(options, auth);
        var policy = Policy(options);

        var before = policy.RetentionUntilUtc(auth);

        for (var i = 0; i < 5; i++)
            (await store.GetByAuthorizationNumberAsync(auth.AuthorizationNumber)).Should().NotBeNull();

        var afterReads = await store.GetByIdAsync(auth.Id);
        policy.RetentionUntilUtc(afterReads!).Should().Be(before,
            "reading a record must never extend its regulatory retention period");

        await worker.SweepAsync(options, CancellationToken.None);
        (await store.GetByIdAsync(auth.Id)).Should().BeNull("the purge still happens when originally due");
    }

    // ── Tenant isolation ───────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public async Task PAT03_Replace_ASweepNeverPurgesAcrossTenantBoundaries()
    {
        var options = Options(period: TimeSpan.FromDays(365));
        var mine = Auth(AuthorizationStatus.Approved, Now.AddDays(-800), tenant: TenantA);
        var theirs = Auth(AuthorizationStatus.Approved, Now.AddDays(-800), tenant: TenantB);
        var (_, store) = WorkerOver(options, mine, theirs);

        // Sweeping ONE tenant must leave the other's record alone.
        var policy = Policy(options);
        var candidates = await store.FindRetentionCandidatesAsync(
            TenantA, policy.CandidateCutoffUtc(Now), 500);

        candidates.Should().ContainSingle().Which.TenantId.Should().Be(TenantA);

        // And a purge naming the wrong tenant refuses outright.
        (await store.PurgeIfStillEligibleAsync(TenantA, theirs.Id, AuthorizationStatus.Approved))
            .Should().BeFalse("a record may only be purged within its own tenant");
        (await store.GetByIdAsync(theirs.Id)).Should().NotBeNull();
    }

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public async Task PAT03_Replace_ASweepCoversEveryTenantThatHasData()
    {
        var options = Options(period: TimeSpan.FromDays(365));
        var mine = Auth(AuthorizationStatus.Approved, Now.AddDays(-800), tenant: TenantA);
        var theirs = Auth(AuthorizationStatus.Denied, Now.AddDays(-800), tenant: TenantB);
        var (worker, store) = WorkerOver(options, mine, theirs);

        // Both tenants are discoverable before the sweep...
        (await store.ListTenantIdsAsync()).Should().BeEquivalentTo([TenantA, TenantB]);

        var summary = await worker.SweepAsync(options, CancellationToken.None);

        // ...and both were visited, each under its own scope.
        summary.Purged.Should().Be(2);
        (await store.GetByIdAsync(mine.Id)).Should().BeNull();
        (await store.GetByIdAsync(theirs.Id)).Should().BeNull();
    }

    // ── Idempotency and retry ──────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public async Task PAT03_Replace_RepeatedSweepsAreSafe()
    {
        var options = Options(period: TimeSpan.FromDays(365));
        var auth = Auth(AuthorizationStatus.Approved, Now.AddDays(-800));
        var (worker, store) = WorkerOver(options, auth);

        var first = await worker.SweepAsync(options, CancellationToken.None);
        var second = await worker.SweepAsync(options, CancellationToken.None);
        var third = await worker.SweepAsync(options, CancellationToken.None);

        first.Purged.Should().Be(1);
        second.Purged.Should().Be(0, "a second sweep has nothing left to do");
        second.Failed.Should().Be(0, "an already-purged record is not an error");
        third.Failed.Should().Be(0);
    }

    // ── Concurrency ────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public async Task PAT03_Replace_ARecordThatReopensBetweenListingAndPurgeSurvives()
    {
        // The race that matters: the sweep lists a terminal record, then the
        // record moves back to a live state before the delete lands. The delete
        // is conditional on the status it decided against, so it refuses.
        var options = Options(period: TimeSpan.FromDays(365));
        var auth = Auth(AuthorizationStatus.Denied, Now.AddDays(-800));
        var (worker, store) = WorkerOver(options, auth);

        store.OnBeforePurge = id =>
        {
            var current = store.GetByIdAsync(id).GetAwaiter().GetResult();
            if (current is null) return;
            current.Status = AuthorizationStatus.InReview; // appeal reopened it
            store.UpdateAsync(current).GetAwaiter().GetResult();
        };

        var summary = await worker.SweepAsync(options, CancellationToken.None);

        summary.Purged.Should().Be(0);
        summary.Skipped.Should().Be(1);
        store.RefusedPurgeCount.Should().Be(1);
        (await store.GetByIdAsync(auth.Id)).Should().NotBeNull(
            "a record that became live again must survive the sweep that listed it");
    }

    // ── Bounded work and dry run ───────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public async Task PAT03_Replace_WorkIsBoundedPerTenantPerSweep()
    {
        var options = Options(period: TimeSpan.FromDays(365), max: 3);
        var seed = Enumerable.Range(0, 10)
            .Select(_ => Auth(AuthorizationStatus.Approved, Now.AddDays(-800)))
            .ToArray();
        var (worker, _) = WorkerOver(options, seed);

        var summary = await worker.SweepAsync(options, CancellationToken.None);

        summary.Scanned.Should().Be(3, "a sweep never loads an unbounded result set");
        summary.Purged.Should().Be(3);
    }

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public async Task PAT03_Replace_SuccessiveSweepsAdvanceThroughABacklogOldestFirst()
    {
        // A bounded query without an ordering can return whichever subset the
        // store happens to hand back, so a large backlog could be re-scanned
        // forever while the oldest records are never reached. Oldest-first means
        // each sweep makes progress and the longest-expired records go first.
        var options = Options(period: TimeSpan.FromDays(365), max: 2);

        // Six eligible records, distinguishable by age.
        var seed = Enumerable.Range(1, 6)
            .Select(i => Auth(AuthorizationStatus.Approved, Now.AddDays(-400 - (i * 100)),
                authNumber: $"PAS-AGE-{i:D2}"))
            .ToArray();
        var (worker, store) = WorkerOver(options, seed);

        // The oldest is i=6 (-1000d); the youngest eligible is i=1 (-500d).
        var oldestTwo = new[] { "PAS-AGE-06", "PAS-AGE-05" };

        await worker.SweepAsync(options, CancellationToken.None);

        foreach (var number in oldestTwo)
            (await store.GetByAuthorizationNumberAsync(number)).Should().BeNull(
                "the oldest records are purged first");
        (await store.GetByAuthorizationNumberAsync("PAS-AGE-01")).Should().NotBeNull(
            "a bounded sweep leaves the youngest for a later pass");

        // Three bounded sweeps clear all six — progress, not repetition.
        await worker.SweepAsync(options, CancellationToken.None);
        await worker.SweepAsync(options, CancellationToken.None);

        foreach (var a in seed)
            (await store.GetByAuthorizationNumberAsync(a.AuthorizationNumber)).Should().BeNull();
    }

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public async Task PAT03_Replace_AnOpenStatusIsRefusedEvenIfNamedAsExpected()
    {
        // Defence in depth at the store: a caller that names an open status as
        // the expected one is refused rather than trusted, in every repository.
        var options = Options();
        var auth = Auth(AuthorizationStatus.Pended, Now.AddDays(-5000));
        var (_, store) = WorkerOver(options, auth);

        var purged = await store.PurgeIfStillEligibleAsync(
            TenantA, auth.Id, AuthorizationStatus.Pended);

        purged.Should().BeFalse();
        (await store.GetByIdAsync(auth.Id)).Should().NotBeNull();
    }

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public async Task PAT03_Replace_DryRunReportsWithoutDeleting()
    {
        var options = Options(period: TimeSpan.FromDays(365), dryRun: true);
        var auth = Auth(AuthorizationStatus.Approved, Now.AddDays(-800));
        var (worker, store) = WorkerOver(options, auth);

        var summary = await worker.SweepAsync(options, CancellationToken.None);

        summary.WouldPurge.Should().Be(1);
        summary.Purged.Should().Be(0);
        (await store.GetByIdAsync(auth.Id)).Should().NotBeNull();
    }

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public void PAT03_Replace_TheSweepIsDisabledUntilADeploymentOptsIn()
    {
        // A destructive job defaults to off.
        new PriorAuthorizationRetentionOptions().Enabled.Should().BeFalse();
    }

    // ── Audit hygiene ──────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public void PAT03_Replace_TheSweepSummaryCarriesCountsNotIdentities()
    {
        // Normal operation reports aggregates. Nothing on the summary could hold
        // a member, a payload, or a narrative.
        var properties = typeof(RetentionSweepSummary).GetProperties().Select(p => p.Name).ToList();

        properties.Should().Contain(["Scanned", "Purged", "Skipped", "Failed"]);
        properties.Should().NotContain(n =>
            n.Contains("Member", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Patient", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Name", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Reason", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Payload", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public void PAT03_Replace_PurgeDecisionsAreAttributableToAPolicyVersion()
    {
        // A past purge stays explicable when the rule later changes.
        Policy(Options()).PolicyVersion.Should().NotBeNullOrWhiteSpace();
    }

    // ── Test doubles ───────────────────────────────────────────────────────────

    private sealed class StaticOptionsMonitor : IOptionsMonitor<PriorAuthorizationRetentionOptions>
    {
        public StaticOptionsMonitor(PriorAuthorizationRetentionOptions value) => CurrentValue = value;
        public PriorAuthorizationRetentionOptions CurrentValue { get; }
        public PriorAuthorizationRetentionOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<PriorAuthorizationRetentionOptions, string?> listener) => null;
    }
}
