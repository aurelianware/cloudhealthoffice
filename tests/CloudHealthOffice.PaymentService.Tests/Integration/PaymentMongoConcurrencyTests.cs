using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using PaymentService.Models;
using PaymentService.Repositories;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests.Integration;

/// <summary>
/// Optimistic concurrency on the MongoDB payment, payment run and reversal run
/// repositories (EphemeralMongo). <c>UpdateAsync</c> replaced the document by
/// id + tenant only, so of two concurrent saves the later silently erased the
/// earlier, and a whole-run save from a stale copy undid targeted writes made
/// meanwhile (Pending -> Running, reservation outcomes, the EFT file pin, a
/// remainder reissue). Every write now bumps <c>Revision</c>, and a replace is
/// conditional on the revision it read.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class PaymentMongoConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-concurrency";
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("payment_concurrency");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private static HttpContextAccessor Context()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = Tenant;
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private PaymentRepositoryMongo Payments() => new(_db, Context(), NullLogger<PaymentRepositoryMongo>.Instance);
    private PaymentRunRepositoryMongo Runs() => new(_db, Context(), NullLogger<PaymentRunRepositoryMongo>.Instance);
    private ReversalRunRepositoryMongo Reversals() => new(_db, Context(), NullLogger<ReversalRunRepositoryMongo>.Instance);

    private static async Task<Exception?> Try(Func<Task> write)
    {
        try
        {
            await write();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>Makes a stored document look like one written before Revision existed.</summary>
    private Task UnsetRevisionAsync(string collection, string id) =>
        _db.GetCollection<BsonDocument>(collection).UpdateOneAsync(
            new BsonDocument("_id", id), new BsonDocument("$unset", new BsonDocument("Revision", "")));

    // ── Payments ──────────────────────────────────────────────────────

    private Task<Payment> SeedPaymentAsync(string id) => Payments().CreateAsync(new Payment
    {
        Id = id, CheckNumber = "CHK-" + id, PayerId = "P1", PayerName = "Payer", Status = PaymentStatus.PaidPendingFinalize,
        ClaimPayments = { new ClaimPayment { ClaimId = "c1" }, new ClaimPayment { ClaimId = "c2" } },
    });

    [Fact]
    public async Task Payment_two_concurrent_updates_from_the_same_read_exactly_one_wins()
    {
        await SeedPaymentAsync("pay-1");
        var a = (await Payments().GetByIdAsync("pay-1"))!;
        var b = (await Payments().GetByIdAsync("pay-1"))!;
        a.Notes = "posted by A";
        a.Status = PaymentStatus.Posted;
        b.Notes = "reconciled by B";
        b.Status = PaymentStatus.Reconciled;

        var outcomes = await Task.WhenAll(
            Task.Run(() => Try(() => Payments().UpdateAsync(a))),
            Task.Run(() => Try(() => Payments().UpdateAsync(b))));

        Assert.Equal(1, outcomes.Count(o => o is null));
        var conflict = Assert.Single(outcomes.OfType<PaymentConcurrencyException>());
        Assert.IsAssignableFrom<ConcurrencyConflictException>(conflict);
        var winner = outcomes[0] is null ? a : b;
        var stored = (await Payments().GetByIdAsync("pay-1"))!;
        Assert.Equal((winner.Notes, winner.Status, 1L), (stored.Notes, stored.Status, stored.Revision));
    }

    [Fact]
    public async Task Payment_written_before_revision_existed_updates_once_then_a_stale_copy_conflicts()
    {
        await SeedPaymentAsync("pay-legacy");
        await UnsetRevisionAsync("Payments", "pay-legacy");
        var first = (await Payments().GetByIdAsync("pay-legacy"))!;
        var stale = (await Payments().GetByIdAsync("pay-legacy"))!;
        Assert.Equal(0, first.Revision);

        first.Notes = "first";
        await Payments().UpdateAsync(first);
        stale.Notes = "stale";
        await Assert.ThrowsAsync<PaymentConcurrencyException>(() => Payments().UpdateAsync(stale));

        var stored = (await Payments().GetByIdAsync("pay-legacy"))!;
        Assert.Equal(("first", 1L), (stored.Notes, stored.Revision));
        Assert.Equal(0, stale.Revision); // restored on failure
    }

    [Fact]
    public async Task Payment_update_of_a_missing_payment_still_writes_nothing_and_does_not_throw()
    {
        var ghost = new Payment { Id = "pay-ghost", TenantId = Tenant, CheckNumber = "CHK-G" };
        var returned = await Payments().UpdateAsync(ghost);
        Assert.Same(ghost, returned);
        Assert.Equal(0, ghost.Revision);
        Assert.Null(await Payments().GetByIdAsync("pay-ghost"));
    }

    [Fact]
    public async Task Run_finalize_save_from_a_stale_copy_keeps_a_concurrent_request_write_and_its_own_finalize()
    {
        await SeedPaymentAsync("pay-fin");
        var runCopy = (await Payments().GetByIdAsync("pay-fin"))!;

        // A request saves the payment meanwhile (e.g. POST {id}/post adds notes).
        var request = (await Payments().GetByIdAsync("pay-fin"))!;
        request.Notes = "operator note";
        await Payments().UpdateAsync(request);

        // The run finalized both claims on its (now stale) copy.
        foreach (var cp in runCopy.ClaimPayments)
            cp.FinalizedAt = DateTime.UtcNow;
        runCopy.EraEnvelopeId = "env-1";
        await ConcurrentSaves.SaveFinalizeOutcomeAsync(Payments(), runCopy);

        var stored = (await Payments().GetByIdAsync("pay-fin"))!;
        Assert.Equal("operator note", stored.Notes);
        Assert.All(stored.ClaimPayments, cp => Assert.NotNull(cp.FinalizedAt));
        Assert.Equal((PaymentStatus.Posted, "env-1", 2L), (stored.Status, stored.EraEnvelopeId, stored.Revision));
    }

    // ── Payment runs ──────────────────────────────────────────────────

    private Task<PaymentRun> SeedRunAsync(string id, PaymentRunStatus status = PaymentRunStatus.Running) =>
        Runs().CreateAsync(new PaymentRun { Id = id, PaymentRunNumber = "PR-" + id, Status = status });

    [Fact]
    public async Task Payment_run_two_concurrent_updates_from_the_same_read_exactly_one_wins()
    {
        await SeedRunAsync("run-1");
        var a = (await Runs().GetByIdAsync("run-1"))!;
        var b = (await Runs().GetByIdAsync("run-1"))!;
        a.Description = "A";
        b.Description = "B";

        var outcomes = await Task.WhenAll(
            Task.Run(() => Try(() => Runs().UpdateAsync(a))),
            Task.Run(() => Try(() => Runs().UpdateAsync(b))));

        Assert.Equal(1, outcomes.Count(o => o is null));
        Assert.Single(outcomes.OfType<PaymentConcurrencyException>());
        var stored = (await Runs().GetByIdAsync("run-1"))!;
        Assert.Equal(outcomes[0] is null ? "A" : "B", stored.Description);
        Assert.Equal(1, stored.Revision);
    }

    [Fact]
    public async Task Payment_run_replace_from_a_read_before_reservation_outcomes_fails_instead_of_erasing_them()
    {
        await SeedRunAsync("run-res");
        var executing = (await Runs().GetByIdAsync("run-res"))!;

        var outcomes = new ReservationOutcomes();
        outcomes.Released.Add("claim-r");
        outcomes.Attention.Add(new ReservationAttention { ClaimId = "claim-a", Reason = "stuck", FlaggedAt = DateTime.UtcNow });
        outcomes.Warnings.Add("reconciliation warning");
        Assert.True(await Runs().RecordReservationOutcomesAsync("run-res", outcomes));

        executing.Status = PaymentRunStatus.Completed;
        executing.Warnings.Add("run warning");
        await Assert.ThrowsAsync<PaymentConcurrencyException>(() => Runs().UpdateAsync(executing));

        var stored = (await Runs().GetByIdAsync("run-res"))!;
        Assert.Equal(new[] { "claim-r" }, stored.ReleasedReservationClaimIds);
        Assert.Equal("claim-a", Assert.Single(stored.ReservationsNeedingAttention).ClaimId);
        Assert.Equal(PaymentRunStatus.Running, stored.Status);

        // The executing run's re-read-and-retry: keep the outcomes, save again.
        ConcurrentSaves.KeepReservationOutcomes(executing, stored);
        await Runs().UpdateAsync(executing);
        stored = (await Runs().GetByIdAsync("run-res"))!;
        Assert.Equal(PaymentRunStatus.Completed, stored.Status);
        Assert.Equal(new[] { "claim-r" }, stored.ReleasedReservationClaimIds);
        Assert.Equal("claim-a", Assert.Single(stored.ReservationsNeedingAttention).ClaimId);
        Assert.Equal(new[] { "run warning", "reconciliation warning" }, stored.Warnings);
    }

    [Fact]
    public async Task Payment_run_replace_from_a_read_before_the_eft_file_pin_fails_instead_of_erasing_it()
    {
        await SeedRunAsync("run-eft", PaymentRunStatus.Completed);
        var stale = (await Runs().GetByIdAsync("run-eft"))!;

        var file = new PaymentRunEftFile { FileReference = "FFS-PR-EFT", Sha256 = new string('e', 64) };
        Assert.True(await Runs().TrySaveEftFileAsync("run-eft", file, null, Array.Empty<CheckFallbackPayment>(), new[] { "eft warning" }));

        stale.Warnings.Add("finalize retried");
        await Assert.ThrowsAsync<PaymentConcurrencyException>(() => Runs().UpdateAsync(stale));

        var stored = (await Runs().GetByIdAsync("run-eft"))!;
        Assert.Equal(file.Sha256, stored.EftFile?.Sha256);
        Assert.Equal(new[] { "eft warning" }, stored.Warnings);
    }

    [Fact]
    public async Task Payment_run_replace_from_a_read_before_a_remainder_reissue_fails_instead_of_erasing_it()
    {
        await Runs().CreateAsync(new PaymentRun
        {
            Id = "run-rem", PaymentRunNumber = "PR-REM", Status = PaymentRunStatus.Completed,
            EftFile = new PaymentRunEftFile
            {
                FileReference = "FFS-PR-REM", Sha256 = new string('f', 64),
                VoidedPayments = { new VoidedPaymentNotice { PaymentId = "p-1", UnpaidRemainder = 5m } },
            },
        });
        var stale = (await Runs().GetByIdAsync("run-rem"))!;
        var message = new PaymentFileOutboxMessage { EventId = "ev-rem", Type = "VoidRemainderReissued", PayloadJson = "{}", CreatedAt = DateTime.UtcNow };
        Assert.True(await Runs().TryRecordRemainderReissueAsync("run-rem", "p-1", DateTime.UtcNow, "treasury", "Check", "CHK-1", message, "reissued"));

        await Assert.ThrowsAsync<PaymentConcurrencyException>(() => Runs().UpdateAsync(stale));

        var stored = (await Runs().GetByIdAsync("run-rem"))!;
        Assert.Equal("ev-rem", Assert.Single(stored.GlOutbox).EventId);
        Assert.NotNull(stored.EftFile!.VoidedPayments.Single().RemainderReissuedAt);
    }

    [Fact]
    public async Task Payment_run_cancel_from_a_read_taken_before_execution_started_conflicts()
    {
        await SeedRunAsync("run-start", PaymentRunStatus.Pending);
        var cancelling = (await Runs().GetByIdAsync("run-start"))!;
        Assert.Equal(PaymentRunStatus.Pending, cancelling.Status); // the cancel's in-memory check passes

        Assert.True(await Runs().TryStartAsync("run-start", "approver", DateTime.UtcNow));

        cancelling.Status = PaymentRunStatus.Cancelled;
        await Assert.ThrowsAsync<PaymentConcurrencyException>(() => Runs().UpdateAsync(cancelling));
        Assert.Equal(PaymentRunStatus.Running, (await Runs().GetByIdAsync("run-start"))!.Status);
    }

    [Fact]
    public async Task Payment_run_written_before_revision_existed_updates_once()
    {
        await SeedRunAsync("run-legacy");
        await UnsetRevisionAsync("PaymentRuns", "run-legacy");
        var legacy = (await Runs().GetByIdAsync("run-legacy"))!;
        var stale = (await Runs().GetByIdAsync("run-legacy"))!;

        legacy.Description = "updated";
        await Runs().UpdateAsync(legacy);
        await Assert.ThrowsAsync<PaymentConcurrencyException>(() => Runs().UpdateAsync(stale));

        var stored = (await Runs().GetByIdAsync("run-legacy"))!;
        Assert.Equal(("updated", 1L), (stored.Description, stored.Revision));
    }

    [Fact]
    public async Task Payment_run_update_of_a_missing_run_still_writes_nothing_and_does_not_throw()
    {
        var ghost = new PaymentRun { Id = "run-ghost", TenantId = Tenant, PaymentRunNumber = "PR-G" };
        Assert.Same(ghost, await Runs().UpdateAsync(ghost));
        Assert.Null(await Runs().GetByIdAsync("run-ghost"));
    }

    // ── Reversal runs ─────────────────────────────────────────────────

    private Task<ReversalRun> SeedReversalAsync(string id, ReversalRunStatus status = ReversalRunStatus.Running) =>
        Reversals().CreateAsync(new ReversalRun { Id = id, ReversalRunNumber = "RR-" + id, Status = status });

    [Fact]
    public async Task Reversal_run_two_concurrent_updates_from_the_same_read_exactly_one_wins()
    {
        await SeedReversalAsync("rr-1");
        var a = (await Reversals().GetByIdAsync("rr-1"))!;
        var b = (await Reversals().GetByIdAsync("rr-1"))!;
        a.Description = "A";
        b.Description = "B";

        var outcomes = await Task.WhenAll(
            Task.Run(() => Try(() => Reversals().UpdateAsync(a))),
            Task.Run(() => Try(() => Reversals().UpdateAsync(b))));

        Assert.Equal(1, outcomes.Count(o => o is null));
        Assert.Single(outcomes.OfType<PaymentConcurrencyException>());
        var stored = (await Reversals().GetByIdAsync("rr-1"))!;
        Assert.Equal((outcomes[0] is null ? "A" : "B", 1L), (stored.Description, stored.Revision));
    }

    [Fact]
    public async Task Reversal_run_replace_from_a_read_before_reservation_outcomes_fails_instead_of_erasing_them()
    {
        await SeedReversalAsync("rr-res");
        var executing = (await Reversals().GetByIdAsync("rr-res"))!;

        var outcomes = new ReservationOutcomes();
        outcomes.Released.Add("claim-r");
        outcomes.Warnings.Add("reconciliation warning");
        Assert.True(await Reversals().RecordReservationOutcomesAsync("rr-res", outcomes));

        executing.Status = ReversalRunStatus.Completed;
        await Assert.ThrowsAsync<PaymentConcurrencyException>(() => Reversals().UpdateAsync(executing));
        var stored = (await Reversals().GetByIdAsync("rr-res"))!;
        Assert.Equal(new[] { "claim-r" }, stored.ReleasedReservationClaimIds);
        Assert.Equal(ReversalRunStatus.Running, stored.Status);

        ConcurrentSaves.KeepReservationOutcomes(executing, stored);
        await Reversals().UpdateAsync(executing);
        stored = (await Reversals().GetByIdAsync("rr-res"))!;
        Assert.Equal(ReversalRunStatus.Completed, stored.Status);
        Assert.Equal(new[] { "claim-r" }, stored.ReleasedReservationClaimIds);
        Assert.Equal(new[] { "reconciliation warning" }, stored.Warnings);
    }

    [Fact]
    public async Task Reversal_run_cancel_from_a_read_taken_before_execution_started_conflicts()
    {
        await SeedReversalAsync("rr-start", ReversalRunStatus.Pending);
        var cancelling = (await Reversals().GetByIdAsync("rr-start"))!;
        Assert.True(await Reversals().TryStartAsync("rr-start", "approver", DateTime.UtcNow));

        cancelling.Status = ReversalRunStatus.Cancelled;
        await Assert.ThrowsAsync<PaymentConcurrencyException>(() => Reversals().UpdateAsync(cancelling));
        Assert.Equal(ReversalRunStatus.Running, (await Reversals().GetByIdAsync("rr-start"))!.Status);
    }

    [Fact]
    public async Task Reversal_run_written_before_revision_existed_updates_once()
    {
        await SeedReversalAsync("rr-legacy");
        await UnsetRevisionAsync("ReversalRuns", "rr-legacy");
        var legacy = (await Reversals().GetByIdAsync("rr-legacy"))!;
        var stale = (await Reversals().GetByIdAsync("rr-legacy"))!;

        legacy.Description = "updated";
        await Reversals().UpdateAsync(legacy);
        await Assert.ThrowsAsync<PaymentConcurrencyException>(() => Reversals().UpdateAsync(stale));

        var stored = (await Reversals().GetByIdAsync("rr-legacy"))!;
        Assert.Equal(("updated", 1L), (stored.Description, stored.Revision));
    }

    [Fact]
    public async Task Reversal_run_update_of_a_missing_run_still_writes_nothing_and_does_not_throw()
    {
        var ghost = new ReversalRun { Id = "rr-ghost", TenantId = Tenant, ReversalRunNumber = "RR-G" };
        Assert.Same(ghost, await Reversals().UpdateAsync(ghost));
        Assert.Null(await Reversals().GetByIdAsync("rr-ghost"));
    }
}
