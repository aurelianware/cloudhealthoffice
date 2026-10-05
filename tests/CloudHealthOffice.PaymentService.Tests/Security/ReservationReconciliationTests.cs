using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;
using PaymentService.Models;
using PaymentService.Repositories;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests.Security;

/// <summary>
/// Reconciliation of claim reservations left reserved but unpaid, on the real
/// pipeline (stand-in claims-service and trading-partner-service, in-memory
/// stores, a movable clock). The hosted job's timer is off; each test runs one
/// pass with <see cref="ReservationReconciliationJob.RunOnceAsync"/>.
/// </summary>
public sealed class ReservationReconciliationTests : IDisposable
{
    private const string Maker = "finance-maker-1";
    private const string Executor = "finance-approver-2";
    private const string SecondApprover = "finance-approver-3";
    private const string Tenant = RunExecutionHost.Tenant;

    private static readonly TimeSpan PastGrace = TimeSpan.FromMinutes(31);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly RunExecutionHost _host = new();

    public void Dispose() => _host.Dispose();

    private HttpClient Approver(string subject) => _host.As(subject, ChoRolePermissions.FinanceApprover);

    private Task<IReadOnlyList<ReconciliationPassResult>> RunJobAsync() => _host.ReconciliationJob.RunOnceAsync();

    private async Task<string> CreateRunAsync(string path = "/api/paymentruns")
    {
        var response = await _host.As(Maker, ChoRolePermissions.Finance).PostAsJsonAsync(path, new { criteria = new { } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private async Task<HttpResponseMessage> ExecuteAsync(string runId, string by = Executor)
        => await Approver(by).PostAsync($"/api/paymentruns/{runId}/execute", null);

    /// <summary>A run that reserved clm and tried to pay it, but the payment insert failed: Failed, reservation kept.</summary>
    private async Task<string> FailedRunHoldingAsync(string claimId)
    {
        _host.Claims.Add(claimId);
        var runId = await CreateRunAsync();
        _host.Payments.FailNextCreate = true;
        var response = await ExecuteAsync(runId);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PaymentRunStatus.Failed, Run(runId).Status);
        Assert.Contains(_host.Reservations.All, r => r.ClaimId == claimId && r.RunId == runId);
        return runId;
    }

    /// <summary>A run in a given state that holds a reservation of <paramref name="claimId"/> (seeded directly).</summary>
    private async Task<PaymentRun> SeedRunHoldingAsync(
        string claimId, PaymentRunStatus status, DateTime? startedAt = null, DateTime? endedAt = null,
        ClaimReservationKind kind = ClaimReservationKind.Payment)
    {
        var now = DateTime.UtcNow;
        var run = new PaymentRun
        {
            PaymentRunNumber = "PR-SEED-" + claimId,
            Status = status,
            CreatedBy = Maker,
            ExecutedBy = status == PaymentRunStatus.Cancelled ? null : Executor,
            ExecutionStartedAt = startedAt ?? now,
            ExecutionCompletedAt = status is PaymentRunStatus.Failed or PaymentRunStatus.Completed ? endedAt ?? now : null,
            CancelledAt = status == PaymentRunStatus.Cancelled ? endedAt ?? now : null,
        };
        await _host.Runs.CreateAsync(run);
        Assert.True(await _host.Reservations.TryReserveAsync(new ClaimReservation
        {
            TenantId = Tenant,
            Kind = kind,
            ClaimId = claimId,
            RunId = run.Id,
            RunNumber = run.PaymentRunNumber,
            ReservedBy = Executor,
            ReservedAt = startedAt ?? now,
        }));
        return run;
    }

    private PaymentRun Run(string runId) => _host.Runs.GetByIdAsync(runId).GetAwaiter().GetResult()!;

    private ClaimReservation? Reservation(string claimId, ClaimReservationKind kind = ClaimReservationKind.Payment)
        => _host.Reservations.All.SingleOrDefault(r => r.ClaimId == claimId && r.Kind == kind);

    private List<Payment> PaymentsFor(string claimId)
        => _host.Payments.All.Where(p => !p.IsReversal && p.ClaimPayments.Any(cp => cp.ClaimId == claimId)).ToList();

    private Task<Payment> SeedPaymentAsync(string claimId, string runId, PaymentStatus status)
        => _host.Payments.CreateAsync(new Payment
        {
            CheckNumber = "0000001234",
            RunId = runId,
            Status = status,
            ClaimPayments = new List<ClaimPayment> { new() { ClaimId = claimId } },
        });

    private static Task<HttpResponseMessage> ReleaseAsync(HttpClient client, string runId, string claimId, string? reason, string runs = "paymentruns")
        => client.PostAsJsonAsync($"/api/{runs}/{runId}/reservations/{claimId}/release", new { reason });

    /// <summary>Runs work outside the job's flow (its request context names the tenant being reconciled).</summary>
    private static async Task Detached(Func<Task> work)
    {
        Task task;
        using (ExecutionContext.SuppressFlow())
            task = Task.Run(work);
        await task;
    }

    // ── automatic release ─────────────────────────────────────────────

    [Fact]
    public async Task FailedRun_PastGrace_NoPaymentNo835_AutoReleased_Audited_ListedOnRun_NoOutboundCalls()
    {
        var runId = await FailedRunHoldingAsync("clm-1");
        _host.Clock.Advance(PastGrace);
        var claimsCalls = _host.Claims.Calls.Count;
        var partnerCalls = _host.TradingPartners.Calls.Count;

        var results = await RunJobAsync();

        Assert.Null(Reservation("clm-1"));
        Assert.Equal(new[] { "clm-1" }, Assert.Single(results).AutoReleased);
        var run = Run(runId);
        Assert.Equal(new[] { "clm-1" }, run.ReleasedReservationClaimIds);
        Assert.Contains(run.Warnings, w => w.Contains("clm-1") && w.Contains(ReservationReconciliationService.AutoReleaseReason));
        Assert.Equal(PaymentRunStatus.Failed, run.Status);

        var entry = Assert.Single(_host.Audit.All);
        Assert.Equal(ReservationAuditAction.AutoReleased, entry.Action);
        Assert.Equal("auto-release: run failed before payment", entry.Reason);
        Assert.Equal((runId, "clm-1", Tenant), (entry.RunId, entry.ClaimId, entry.TenantId));
        Assert.Equal(ReservationReconciliationService.JobActor, entry.Actor);
        Assert.True(entry.ActorIsService);
        Assert.False(entry.PaymentFound);
        Assert.False(entry.EnvelopeFound);

        // Internal state only: no claims-service or trading-partner call.
        Assert.Equal(claimsCalls, _host.Claims.Calls.Count);
        Assert.Equal(partnerCalls, _host.TradingPartners.Calls.Count);
    }

    [Fact]
    public async Task ReleasedClaim_IsPaidByTheNextRun_Once()
    {
        var failedRunId = await FailedRunHoldingAsync("clm-1");

        // Held: the next run cannot pay it.
        var blocked = (await (await ExecuteAsync(await CreateRunAsync())).Content.ReadFromJsonAsync<PaymentRun>(Json))!;
        Assert.Contains("clm-1", blocked.AlreadyPaidClaimIds);
        Assert.Empty(PaymentsFor("clm-1"));

        _host.Clock.Advance(PastGrace);
        await RunJobAsync();

        var next = (await (await ExecuteAsync(await CreateRunAsync())).Content.ReadFromJsonAsync<PaymentRun>(Json))!;
        Assert.Equal(new[] { "clm-1" }, next.ClaimIds);
        Assert.Single(PaymentsFor("clm-1"));
        Assert.Equal("Paid", _host.Claims.Get("clm-1").Status);
        Assert.Equal(next.Id, Reservation("clm-1")!.RunId);
        Assert.Equal(new[] { "clm-1" }, Run(failedRunId).ReleasedReservationClaimIds);
    }

    [Fact]
    public async Task FailedRun_BeforeGracePeriod_NotReleased()
    {
        var runId = await FailedRunHoldingAsync("clm-1");
        _host.Clock.Advance(TimeSpan.FromMinutes(29));

        var results = await RunJobAsync();

        var held = Reservation("clm-1");
        Assert.NotNull(held);
        Assert.Equal(runId, held!.RunId);
        Assert.False(held.NeedsAttention);
        Assert.Empty(Assert.Single(results).AutoReleased);
        Assert.Empty(Run(runId).ReleasedReservationClaimIds);
        Assert.Empty(_host.Audit.All);
    }

    [Fact]
    public async Task GracePeriod_CountsFromTheRunsEnd_NotOnlyTheReservation()
    {
        // Reserved long ago, but the run only failed a minute ago.
        var run = await SeedRunHoldingAsync("clm-1", PaymentRunStatus.Failed,
            startedAt: DateTime.UtcNow.AddHours(-3), endedAt: DateTime.UtcNow.AddMinutes(-1));

        await RunJobAsync();

        Assert.Equal(run.Id, Reservation("clm-1")!.RunId);
    }

    [Fact]
    public async Task CancelledRun_PastGrace_NoPaymentNo835_AutoReleased()
    {
        var run = await SeedRunHoldingAsync("clm-1", PaymentRunStatus.Cancelled);
        _host.Clock.Advance(PastGrace);

        await RunJobAsync();

        Assert.Null(Reservation("clm-1"));
        Assert.Equal(new[] { "clm-1" }, Run(run.Id).ReleasedReservationClaimIds);
        Assert.Equal(ReservationAuditAction.AutoReleased, Assert.Single(_host.Audit.All).Action);
    }

    [Fact]
    public async Task PaymentExists_NotReleased_FlaggedNeedsAttention_ListedOnRunAndEndpoint()
    {
        var runId = await FailedRunHoldingAsync("clm-1");
        await SeedPaymentAsync("clm-1", runId, PaymentStatus.Exception);
        _host.Clock.Advance(PastGrace);

        var results = await RunJobAsync();

        var held = Reservation("clm-1")!;
        Assert.Equal(runId, held.RunId);
        Assert.True(held.NeedsAttention);
        Assert.Contains("payment record", held.AttentionReason);
        Assert.Equal(new[] { "clm-1" }, Assert.Single(results).Flagged);

        var run = Run(runId);
        Assert.Empty(run.ReleasedReservationClaimIds);
        var attention = Assert.Single(run.ReservationsNeedingAttention);
        Assert.Equal("clm-1", attention.ClaimId);
        Assert.Equal(held.AttentionReason, attention.Reason);

        var entry = Assert.Single(_host.Audit.All);
        Assert.Equal(ReservationAuditAction.FlaggedNeedsAttention, entry.Action);
        Assert.True(entry.PaymentFound);

        var listed = await _host.As("finance-reader", ChoRolePermissions.Finance)
            .GetFromJsonAsync<List<ReservationNeedingAttentionView>>("/api/claimreservations/needs-attention", Json);
        var view = Assert.Single(listed!);
        Assert.Equal(("clm-1", runId, "Payment"), (view.ClaimId, view.RunId, view.Kind));
        Assert.Equal($"/api/paymentruns/{runId}/reservations/clm-1/release", view.ReleasePath);

        // A second pass does not flag (or audit) it again.
        await RunJobAsync();
        Assert.Single(_host.Audit.All);
        Assert.Single(Run(runId).ReservationsNeedingAttention);
    }

    [Fact]
    public async Task EnvelopeExists_NotReleased_FlaggedNeedsAttention()
    {
        var runId = await FailedRunHoldingAsync("clm-1");
        await _host.Envelopes.CreateAsync(new EraEnvelopeRecord
        {
            PaymentRunId = runId,
            TradingPartnerId = "TP-1",
            ClaimIds = new List<string> { "clm-1" },
        });
        _host.Clock.Advance(PastGrace);

        await RunJobAsync();

        var held = Reservation("clm-1")!;
        Assert.Equal(runId, held.RunId);
        Assert.True(held.NeedsAttention);
        Assert.Contains("835", held.AttentionReason);
        Assert.Empty(Run(runId).ReleasedReservationClaimIds);
        Assert.Single(Run(runId).ReservationsNeedingAttention);
        Assert.True(Assert.Single(_host.Audit.All).EnvelopeFound);
    }

    [Fact]
    public async Task StuckRunningRun_Flagged_NotReleased()
    {
        var run = await SeedRunHoldingAsync("clm-1", PaymentRunStatus.Running, startedAt: DateTime.UtcNow);
        _host.Clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));

        await RunJobAsync();

        var held = Reservation("clm-1")!;
        Assert.Equal(run.Id, held.RunId);
        Assert.True(held.NeedsAttention);
        Assert.Contains("still Running", held.AttentionReason);
        var stored = Run(run.Id);
        Assert.Equal(PaymentRunStatus.Running, stored.Status);
        Assert.Empty(stored.ReleasedReservationClaimIds);
        Assert.Single(stored.ReservationsNeedingAttention);
    }

    [Fact]
    public async Task RunningRun_WithinStuckThreshold_LeftAlone()
    {
        var run = await SeedRunHoldingAsync("clm-1", PaymentRunStatus.Running, startedAt: DateTime.UtcNow);
        _host.Clock.Advance(TimeSpan.FromMinutes(90));

        await RunJobAsync();

        var held = Reservation("clm-1")!;
        Assert.Equal(run.Id, held.RunId);
        Assert.False(held.NeedsAttention);
        Assert.Empty(_host.Audit.All);
    }

    [Fact]
    public async Task UnclassifiableReservations_Flagged_NotReleased()
    {
        // The run holding it does not exist; a Completed run holds one it never paid.
        Assert.True(await _host.Reservations.TryReserveAsync(new ClaimReservation
        {
            TenantId = Tenant, Kind = ClaimReservationKind.Payment, ClaimId = "clm-orphan",
            RunId = "run-gone", ReservedAt = DateTime.UtcNow,
        }));
        await SeedRunHoldingAsync("clm-unpaid", PaymentRunStatus.Completed);
        _host.Clock.Advance(PastGrace);

        await RunJobAsync();

        Assert.True(Reservation("clm-orphan")!.NeedsAttention);
        Assert.Contains("not found", Reservation("clm-orphan")!.AttentionReason);
        Assert.True(Reservation("clm-unpaid")!.NeedsAttention);
        Assert.DoesNotContain(_host.Audit.All, e => e.Action == ReservationAuditAction.AutoReleased);
    }

    [Fact]
    public async Task CompletedRun_PaidClaim_LeftAlone()
    {
        _host.Claims.Add("clm-1");
        var runId = await CreateRunAsync();
        Assert.Equal(HttpStatusCode.OK, (await ExecuteAsync(runId)).StatusCode);
        _host.Clock.Advance(TimeSpan.FromDays(1));

        await RunJobAsync();

        Assert.Equal(runId, Reservation("clm-1")!.RunId);
        Assert.False(Reservation("clm-1")!.NeedsAttention);
        Assert.Empty(_host.Audit.All);
    }

    [Fact]
    public async Task FailedReversalRun_PastGrace_NoReversalPaymentNo835_AutoReleased()
    {
        var run = new ReversalRun
        {
            ReversalRunNumber = "RR-SEED-1",
            Status = ReversalRunStatus.Failed,
            CreatedBy = Maker,
            ExecutedBy = Executor,
            ExecutionStartedAt = DateTime.UtcNow,
            ExecutionCompletedAt = DateTime.UtcNow,
        };
        await _host.ReversalRuns.CreateAsync(run);
        await _host.Reservations.TryReserveAsync(new ClaimReservation
        {
            TenantId = Tenant, Kind = ClaimReservationKind.Reversal, ClaimId = "clm-r1",
            RunId = run.Id, RunNumber = run.ReversalRunNumber, ReservedAt = DateTime.UtcNow,
        });
        // An ordinary (non-reversal) payment of the claim does not block its reversal release.
        await SeedPaymentAsync("clm-r1", "some-payment-run", PaymentStatus.Posted);
        _host.Clock.Advance(PastGrace);

        await RunJobAsync();

        Assert.Null(Reservation("clm-r1", ClaimReservationKind.Reversal));
        var stored = (await _host.ReversalRuns.GetByIdAsync(run.Id))!;
        Assert.Equal(new[] { "clm-r1" }, stored.ReleasedReservationClaimIds);
        Assert.Equal("Reversal", Assert.Single(_host.Audit.All).Kind);
    }

    // ── manual release ────────────────────────────────────────────────

    [Fact]
    public async Task ManualRelease_BySecondApprover_WithReason_Released_Audited_ClearsAttention_NextRunPays()
    {
        var runId = await FailedRunHoldingAsync("clm-1");
        await SeedPaymentAsync("clm-1", runId, PaymentStatus.Exception); // flagged: a payment record exists
        _host.Clock.Advance(PastGrace);
        await RunJobAsync();
        Assert.True(Reservation("clm-1")!.NeedsAttention);
        await _host.Payments.DeleteAsync(_host.Payments.All.Single().Id); // the person removed the bad record

        var response = await ReleaseAsync(Approver(SecondApprover), runId, "clm-1", "  payment record was a failed write; checked bank file  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<ReservationReleaseResult>(Json))!;
        Assert.Equal((SecondApprover, "payment record was a failed write; checked bank file"), (result.ReleasedBy, result.Reason));
        Assert.Null(Reservation("clm-1"));

        var run = Run(runId);
        Assert.Equal(new[] { "clm-1" }, run.ReleasedReservationClaimIds);
        Assert.Empty(run.ReservationsNeedingAttention);
        Assert.Contains(run.Warnings, w => w.Contains("clm-1") && w.Contains(SecondApprover));

        var entry = Assert.Single(_host.Audit.All, e => e.Action == ReservationAuditAction.Released);
        Assert.Equal((SecondApprover, false, runId, "clm-1", Tenant), (entry.Actor, entry.ActorIsService, entry.RunId, entry.ClaimId, entry.TenantId));
        Assert.Equal("payment record was a failed write; checked bank file", entry.Reason);
        Assert.Equal(Executor, entry.RunExecutedBy);

        var listed = await _host.As("finance-reader", ChoRolePermissions.Finance)
            .GetFromJsonAsync<List<ReservationNeedingAttentionView>>("/api/claimreservations/needs-attention", Json);
        Assert.Empty(listed!);

        var next = (await (await ExecuteAsync(await CreateRunAsync())).Content.ReadFromJsonAsync<PaymentRun>(Json))!;
        Assert.Equal(new[] { "clm-1" }, next.ClaimIds);
        Assert.Single(PaymentsFor("clm-1"));
    }

    [Fact]
    public async Task ManualRelease_WithoutPaymentsApprove_Is403()
    {
        var runId = await FailedRunHoldingAsync("clm-1");

        var response = await ReleaseAsync(_host.As("finance-preparer", ChoRolePermissions.Finance), runId, "clm-1", "checked");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(Reservation("clm-1"));
        Assert.Empty(_host.Audit.All);
    }

    [Fact]
    public async Task ManualRelease_ByTheRunsExecutor_Is403SeparationOfDuties()
    {
        var runId = await FailedRunHoldingAsync("clm-1");

        var response = await ReleaseAsync(Approver(Executor), runId, "clm-1", "my own run, trust me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Separation of duties", await response.Content.ReadAsStringAsync());
        Assert.NotNull(Reservation("clm-1"));
        Assert.Empty(_host.Audit.All);
        Assert.Empty(Run(runId).ReleasedReservationClaimIds);
    }

    [Fact]
    public async Task ManualRelease_WithServiceToken_Is403()
    {
        var runId = await FailedRunHoldingAsync("clm-1");

        var response = await ReleaseAsync(_host.AsService(), runId, "clm-1", "automation");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(Reservation("clm-1"));
        Assert.Empty(_host.Audit.All);
    }

    [Theory]
    [InlineData(PaymentStatus.Posted)]
    [InlineData(PaymentStatus.PaidPendingFinalize)]
    public async Task ManualRelease_WhenTheClaimIsPaid_Is409_PointsToFinalizeRetry(PaymentStatus status)
    {
        var runId = await FailedRunHoldingAsync("clm-1");
        await SeedPaymentAsync("clm-1", runId, status);

        var response = await ReleaseAsync(Approver(SecondApprover), runId, "clm-1", "looks unpaid to me");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains($"/api/paymentruns/{runId}/finalize", body);
        Assert.Contains(status.ToString(), body);
        Assert.NotNull(Reservation("clm-1"));
        Assert.Empty(_host.Audit.All);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ManualRelease_WithoutAReason_Is400(string? reason)
    {
        var runId = await FailedRunHoldingAsync("clm-1");

        var response = await ReleaseAsync(Approver(SecondApprover), runId, "clm-1", reason);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(Reservation("clm-1"));
        Assert.Empty(_host.Audit.All);
    }

    [Fact]
    public async Task ManualRelease_WhileTheRunIsStillExecuting_Is409()
    {
        var run = await SeedRunHoldingAsync("clm-1", PaymentRunStatus.Running, startedAt: DateTime.UtcNow);

        var response = await ReleaseAsync(Approver(SecondApprover), run.Id, "clm-1", "seems slow");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.NotNull(Reservation("clm-1"));
    }

    [Fact]
    public async Task ManualRelease_OfAClaimTheRunDoesNotHold_Is404()
    {
        var runId = await FailedRunHoldingAsync("clm-1");

        var response = await ReleaseAsync(Approver(SecondApprover), runId, "clm-other", "checked");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ManualRelease_ReversalRun_SameRules()
    {
        var run = new ReversalRun
        {
            ReversalRunNumber = "RR-SEED-2", Status = ReversalRunStatus.Failed, CreatedBy = Maker,
            ExecutedBy = Executor, ExecutionStartedAt = DateTime.UtcNow, ExecutionCompletedAt = DateTime.UtcNow,
        };
        await _host.ReversalRuns.CreateAsync(run);
        await _host.Reservations.TryReserveAsync(new ClaimReservation
        {
            TenantId = Tenant, Kind = ClaimReservationKind.Reversal, ClaimId = "clm-r1", RunId = run.Id, ReservedAt = DateTime.UtcNow,
        });

        Assert.Equal(HttpStatusCode.Forbidden,
            (await ReleaseAsync(Approver(Executor), run.Id, "clm-r1", "mine", "reversalruns")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await ReleaseAsync(Approver(SecondApprover), run.Id, "clm-r1", null, "reversalruns")).StatusCode);

        var ok = await ReleaseAsync(Approver(SecondApprover), run.Id, "clm-r1", "reversal never sent", "reversalruns");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Null(Reservation("clm-r1", ClaimReservationKind.Reversal));
        Assert.Equal(new[] { "clm-r1" }, (await _host.ReversalRuns.GetByIdAsync(run.Id))!.ReleasedReservationClaimIds);
    }

    [Fact]
    public async Task NeedsAttention_ListsOnlyTheCallersTenant_AndNeedsPaymentsRead()
    {
        await _host.Reservations.TryReserveAsync(new ClaimReservation
        {
            TenantId = "tenant-other", Kind = ClaimReservationKind.Payment, ClaimId = "clm-x",
            RunId = "run-x", ReservedAt = DateTime.UtcNow,
        });
        var other = (await _host.Reservations.ListByTenantAsync("tenant-other")).Single();
        await _host.Reservations.TrySetAttentionIfUnchangedAsync(other, "flagged elsewhere", DateTime.UtcNow);

        var listed = await _host.As("finance-reader", ChoRolePermissions.Finance)
            .GetFromJsonAsync<List<ReservationNeedingAttentionView>>("/api/claimreservations/needs-attention", Json);
        Assert.Empty(listed!);

        var noRead = await _host.As("examiner-1", ChoRolePermissions.ClaimsExaminer).GetAsync("/api/claimreservations/needs-attention");
        Assert.Equal(HttpStatusCode.Forbidden, noRead.StatusCode);
    }

    // ── concurrency ───────────────────────────────────────────────────

    [Fact]
    public async Task AutoRelease_AndANewRunReservingTheClaim_DoNotBothWin_NewRunFirstIsRefused()
    {
        var failedRunId = await FailedRunHoldingAsync("clm-1");
        _host.Clock.Advance(PastGrace);
        string? racingRunId = null;

        // After the job classified the reservation as safe, before it deletes it,
        // a new run tries to reserve and pay the claim.
        _host.Reservations.BeforeConditionalDelete = () => Detached(async () =>
        {
            racingRunId = await CreateRunAsync();
            Assert.Equal(HttpStatusCode.OK, (await ExecuteAsync(racingRunId)).StatusCode);
        });

        await RunJobAsync();

        var racing = Run(racingRunId!);
        Assert.Contains("clm-1", racing.AlreadyPaidClaimIds); // the reservation still held: refused
        Assert.Empty(PaymentsFor("clm-1"));
        Assert.Null(Reservation("clm-1"));                    // then the job released it
        Assert.Equal(new[] { "clm-1" }, Run(failedRunId).ReleasedReservationClaimIds);

        var next = (await (await ExecuteAsync(await CreateRunAsync())).Content.ReadFromJsonAsync<PaymentRun>(Json))!;
        Assert.Equal(new[] { "clm-1" }, next.ClaimIds);
        Assert.Single(PaymentsFor("clm-1"));
    }

    [Fact]
    public async Task AutoRelease_DoesNotDeleteTheReservationOfARunThatTookTheClaimMeanwhile()
    {
        var failedRunId = await FailedRunHoldingAsync("clm-1");
        _host.Clock.Advance(PastGrace);
        string? racingRunId = null;

        // Between the job's read and its delete: a second approver releases the
        // reservation by hand, and a new run reserves and pays the claim.
        _host.Reservations.BeforeConditionalDelete = () => Detached(async () =>
        {
            Assert.Equal(HttpStatusCode.OK,
                (await ReleaseAsync(Approver(SecondApprover), failedRunId, "clm-1", "checked; unpaid")).StatusCode);
            racingRunId = await CreateRunAsync();
            Assert.Equal(HttpStatusCode.OK, (await ExecuteAsync(racingRunId)).StatusCode);
        });

        var results = await RunJobAsync();

        // The new run's reservation stays: no later run can pay the claim again.
        Assert.Equal(racingRunId, Reservation("clm-1")!.RunId);
        Assert.Equal(new[] { "clm-1" }, Assert.Single(results).SkippedChanged);
        Assert.Single(PaymentsFor("clm-1"));
        Assert.DoesNotContain(_host.Audit.All, e => e.Action == ReservationAuditAction.AutoReleased);
        Assert.Equal(new[] { "clm-1" }, Run(failedRunId).ReleasedReservationClaimIds);
    }
}
