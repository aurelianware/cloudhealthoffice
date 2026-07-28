using ClaimsService.Models;
using ClaimsService.Repositories;
using EphemeralMongo;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using NSubstitute;

namespace CloudHealthOffice.ClaimsService.Tests.Repositories;

public class MassAdjudicationRunRepositoryTests
{
    [Fact]
    public async Task SaveAsync_generates_server_side_ids_for_claim_results()
    {
        var repo = new InMemoryMassAdjudicationRunRepository();
        var summary = CreateSummary();
        summary.ClaimResults.Add(new MassAdjudicationClaimResult
        {
            Id = "client-supplied-id",
            GeneratedClaimId = "GEN-001",
            ClaimType = "Professional",
            ValidationScenario = "TxStarInpatientNoAuth",
            ExpectedOutcome = "BusinessDenial",
            ExpectedBusinessDenialCode = "PRIOR_AUTH_REQUIRED",
            ValidationStatus = "Matched",
            Outcome = "Paid",
            AdjudicationSuccess = true,
            ServiceBusObservationTimedOut = true,
            ReconciledAfterObservationTimeout = true
        });

        var saved = await repo.SaveAsync(summary);

        saved.Id.Should().NotBeNullOrWhiteSpace();
        saved.ClaimResults.Should().ContainSingle();
        saved.ClaimResults[0].Id.Should().NotBe("client-supplied-id");
        saved.ClaimResults[0].RunId.Should().Be(saved.Id);
        saved.ClaimResults[0].TenantId.Should().Be(saved.Run.TenantId);
        saved.ClaimResults[0].CreatedAtUtc.Should().Be(saved.CreatedAtUtc);
        saved.ClaimResults[0].ValidationScenario.Should().Be("TxStarInpatientNoAuth");
        saved.ClaimResults[0].ValidationStatus.Should().Be("Matched");
        saved.ClaimResults[0].ServiceBusObservationTimedOut.Should().BeTrue();
        saved.ClaimResults[0].ReconciledAfterObservationTimeout.Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_preserves_mcc_evidence_summary_fields()
    {
        var repo = new InMemoryMassAdjudicationRunRepository();
        var summary = CreateSummary();
        summary.Pended = 46;
        summary.ObservationTimeouts = 1;
        summary.ServiceBusObservationTimeouts = 122;
        summary.ServiceBusLateCompletions = 121;
        summary.ServiceBusUnreconciledClaims = 1;
        summary.WorkflowUnsupported = 73;
        summary.WorkflowObservationTimeouts = 2;
        summary.AveragePaymentDelta = 59.36m;
        summary.PaymentDeltaDistribution.Add(new MassAdjudicationPaymentDeltaBucket
        {
            Label = "<= $1",
            LowerBoundExclusive = 0.01m,
            UpperBoundInclusive = 1m,
            Count = 3
        });
        summary.Status = "Running";
        summary.Progress = new MassAdjudicationRunProgress
        {
            Phase = "Processing claims",
            RequestedClaims = 5000,
            CompletedClaims = 2500,
            ProcessedClaims = 2499,
            PlatformFailures = 1,
            PercentComplete = 50,
            CurrentThroughputClaimsPerSecond = 75.5,
            RollingP95LatencyMilliseconds = 250,
            RollingP99LatencyMilliseconds = 320,
            PendingExpectedPendObservations = 46,
            PendingTerminalStatusObservations = 12,
            PendingWorkflowObservations = 58,
            LastPublishedAtUtc = DateTimeOffset.Parse("2026-07-09T19:00:00Z")
        };
        summary.Run.MemberUrl = "https://member";
        summary.Run.CoverageUrl = "https://coverage";
        summary.Run.SeedMembers = true;
        summary.Run.LineOfBusiness = 3;
        summary.WorkflowScenarioBreakdown.Add(new MassAdjudicationWorkflowScenarioSummary
        {
            Scenario = "EdgeCase:CobSecondaryPayer",
            Total = 10,
            Matches = 9,
            Mismatches = 0,
            Unsupported = 0,
            ObservationTimeouts = 1,
            Unspecified = 0
        });

        var saved = await repo.SaveAsync(summary);
        var persisted = saved.ToBson();
        var reread = BsonSerializer.Deserialize<MassAdjudicationRunSummary>(persisted);

        reread.Should().NotBeNull();
        reread.Pended.Should().Be(46);
        reread.ObservationTimeouts.Should().Be(1);
        reread.ServiceBusObservationTimeouts.Should().Be(122);
        reread.ServiceBusLateCompletions.Should().Be(121);
        reread.ServiceBusUnreconciledClaims.Should().Be(1);
        reread.WorkflowUnsupported.Should().Be(73);
        reread.WorkflowObservationTimeouts.Should().Be(2);
        reread.AveragePaymentDelta.Should().Be(59.36m);
        reread.PaymentDeltaDistribution.Should().ContainSingle(b =>
            b.Label == "<= $1"
            && b.LowerBoundExclusive == 0.01m
            && b.UpperBoundInclusive == 1m
            && b.Count == 3);
        reread.Status.Should().Be("Running");
        reread.Progress.Should().NotBeNull();
        reread.Progress!.CompletedClaims.Should().Be(2500);
        reread.Progress.CurrentThroughputClaimsPerSecond.Should().Be(75.5);
        reread.Progress.PendingExpectedPendObservations.Should().Be(46);
        reread.Progress.PendingTerminalStatusObservations.Should().Be(12);
        reread.Progress.PendingWorkflowObservations.Should().Be(58);
        reread.Run.MemberUrl.Should().Be("https://member");
        reread.Run.CoverageUrl.Should().Be("https://coverage");
        reread.Run.SeedMembers.Should().BeTrue();
        reread.Run.LineOfBusiness.Should().Be(3);
        reread.WorkflowScenarioBreakdown.Should().ContainSingle(s =>
            s.Scenario == "EdgeCase:CobSecondaryPayer"
            && s.Total == 10
            && s.Matches == 9
            && s.ObservationTimeouts == 1);
    }

    [Fact]
    public async Task SaveAsync_upserts_progress_updates_without_creating_duplicate_runs()
    {
        var repo = new InMemoryMassAdjudicationRunRepository();
        var runId = Guid.NewGuid().ToString("N");
        var initial = CreateSummary();
        initial.Id = runId;
        initial.Status = "Running";
        initial.TotalClaims = 1000;
        initial.Processed = 100;
        initial.Progress = new MassAdjudicationRunProgress
        {
            Phase = "Processing claims",
            RequestedClaims = 1000,
            CompletedClaims = 100,
            ProcessedClaims = 100,
            PercentComplete = 10,
            LastPublishedAtUtc = DateTimeOffset.UtcNow
        };

        var update = CreateSummary();
        update.Id = runId;
        update.Status = "Running";
        update.TotalClaims = 1000;
        update.Processed = 400;
        update.Progress = new MassAdjudicationRunProgress
        {
            Phase = "Processing claims",
            RequestedClaims = 1000,
            CompletedClaims = 400,
            ProcessedClaims = 400,
            PercentComplete = 40,
            LastPublishedAtUtc = DateTimeOffset.UtcNow
        };

        await repo.SaveAsync(initial);
        await repo.SaveAsync(update);

        var runs = await repo.ListAsync(initial.Run.TenantId, 10);

        runs.Should().ContainSingle();
        runs[0].Id.Should().Be(runId);
        runs[0].Processed.Should().Be(400);
        runs[0].Progress!.CompletedClaims.Should().Be(400);
    }

    [Fact]
    public async Task SaveAsync_progress_only_update_preserves_existing_claim_results()
    {
        var repo = new InMemoryMassAdjudicationRunRepository();
        var runId = Guid.NewGuid().ToString("N");
        var completed = CreateSummary();
        completed.Id = runId;
        completed.Status = "Completed";
        completed.ClaimResults.Add(new MassAdjudicationClaimResult
        {
            GeneratedClaimId = "GEN-EVIDENCE",
            SubmittedClaimId = "claim-001",
            ClaimType = "Professional",
            Outcome = "Paid",
            ValidationStatus = "Matched",
            ElapsedMilliseconds = 100
        });

        var progressOnly = CreateSummary();
        progressOnly.Id = runId;
        progressOnly.Status = "Running";
        progressOnly.Progress = new MassAdjudicationRunProgress
        {
            Phase = "Processing claims",
            RequestedClaims = 1000,
            CompletedClaims = 500,
            ProcessedClaims = 500,
            PercentComplete = 50,
            LastPublishedAtUtc = DateTimeOffset.UtcNow
        };

        var saved = await repo.SaveAsync(completed);
        await repo.SaveAsync(progressOnly);

        var claimResults = await repo.ListClaimResultsAsync(saved.Run.TenantId, runId, null, null, null, 0.01m, 10);

        claimResults.Should().ContainSingle()
            .Which.GeneratedClaimId.Should().Be("GEN-EVIDENCE");
    }

    [Fact]
    public async Task ListClaimResultsAsync_filters_by_outcome_and_validation_status()
    {
        var repo = new InMemoryMassAdjudicationRunRepository();
        var summary = CreateSummary();
        summary.ClaimResults.AddRange(new[]
        {
            new MassAdjudicationClaimResult
            {
                GeneratedClaimId = "GEN-PAID-MATCHED",
                ClaimType = "Professional",
                Outcome = "Paid",
                ValidationStatus = "Matched",
                ElapsedMilliseconds = 10
            },
            new MassAdjudicationClaimResult
            {
                GeneratedClaimId = "GEN-PAID-UNSUPPORTED",
                ClaimType = "Professional",
                Outcome = "Paid",
                ValidationStatus = "Unsupported",
                ElapsedMilliseconds = 30
            },
            new MassAdjudicationClaimResult
            {
                GeneratedClaimId = "GEN-DENIAL-UNSUPPORTED",
                ClaimType = "Professional",
                Outcome = "BusinessDenial",
                ValidationStatus = "Unsupported",
                ElapsedMilliseconds = 20
            }
        });

        var saved = await repo.SaveAsync(summary);

        var unsupported = await repo.ListClaimResultsAsync(
            saved.Run.TenantId,
            saved.Id,
            outcome: null,
            validationStatus: "Unsupported",
            paymentStatus: null,
            paymentTolerance: 0.01m,
            limit: 10);
        var unsupportedPaid = await repo.ListClaimResultsAsync(
            saved.Run.TenantId,
            saved.Id,
            outcome: "Paid",
            validationStatus: "Unsupported",
            paymentStatus: null,
            paymentTolerance: 0.01m,
            limit: 10);

        unsupported.Select(x => x.GeneratedClaimId)
            .Should().Equal("GEN-PAID-UNSUPPORTED", "GEN-DENIAL-UNSUPPORTED");
        unsupportedPaid.Should().ContainSingle()
            .Which.GeneratedClaimId.Should().Be("GEN-PAID-UNSUPPORTED");
    }

    [Fact]
    public async Task ListClaimResultsAsync_filters_by_payment_status_using_run_tolerance()
    {
        var repo = new InMemoryMassAdjudicationRunRepository();
        var summary = CreateSummary();
        summary.ClaimResults.AddRange(new[]
        {
            new MassAdjudicationClaimResult
            {
                GeneratedClaimId = "GEN-PAYMENT-EXACT",
                ClaimType = "Professional",
                Outcome = "Paid",
                ValidationStatus = "Matched",
                PaymentDelta = 0m,
                ElapsedMilliseconds = 10
            },
            new MassAdjudicationClaimResult
            {
                GeneratedClaimId = "GEN-PAYMENT-ROUNDING",
                ClaimType = "Professional",
                Outcome = "Paid",
                ValidationStatus = "Matched",
                PaymentDelta = 0.05m,
                ElapsedMilliseconds = 20
            },
            new MassAdjudicationClaimResult
            {
                GeneratedClaimId = "GEN-PAYMENT-MISMATCH",
                ClaimType = "Professional",
                Outcome = "Paid",
                ValidationStatus = "Matched",
                PaymentDelta = 0.06m,
                ElapsedMilliseconds = 30
            },
            new MassAdjudicationClaimResult
            {
                GeneratedClaimId = "GEN-PAYMENT-UNSCORED",
                ClaimType = "Professional",
                Outcome = "BusinessDenial",
                ValidationStatus = "Matched",
                PaymentDelta = null,
                ElapsedMilliseconds = 40
            }
        });

        var saved = await repo.SaveAsync(summary);

        var mismatched = await repo.ListClaimResultsAsync(
            saved.Run.TenantId,
            saved.Id,
            outcome: null,
            validationStatus: null,
            paymentStatus: "Mismatched",
            paymentTolerance: 0.05m,
            limit: 10);
        var matched = await repo.ListClaimResultsAsync(
            saved.Run.TenantId,
            saved.Id,
            outcome: null,
            validationStatus: null,
            paymentStatus: "Matched",
            paymentTolerance: 0.05m,
            limit: 10);
        var scored = await repo.ListClaimResultsAsync(
            saved.Run.TenantId,
            saved.Id,
            outcome: null,
            validationStatus: null,
            paymentStatus: "Scored",
            paymentTolerance: 0.05m,
            limit: 10);
        var unscored = await repo.ListClaimResultsAsync(
            saved.Run.TenantId,
            saved.Id,
            outcome: null,
            validationStatus: null,
            paymentStatus: "Unscored",
            paymentTolerance: 0.05m,
            limit: 10);

        mismatched.Should().ContainSingle()
            .Which.GeneratedClaimId.Should().Be("GEN-PAYMENT-MISMATCH");
        matched.Select(x => x.GeneratedClaimId)
            .Should().Equal("GEN-PAYMENT-ROUNDING", "GEN-PAYMENT-EXACT");
        scored.Select(x => x.GeneratedClaimId)
            .Should().Equal("GEN-PAYMENT-MISMATCH", "GEN-PAYMENT-ROUNDING", "GEN-PAYMENT-EXACT");
        unscored.Should().ContainSingle()
            .Which.GeneratedClaimId.Should().Be("GEN-PAYMENT-UNSCORED");
    }

    [Fact]
    public async Task ListClaimResultsAsync_mongo_payment_status_excludes_missing_payment_delta_from_scored_filters()
    {
        var runner = MongoRunner.Run(new MongoRunnerOptions { ConnectionTimeout = TimeSpan.FromSeconds(30) });
        try
        {
            var client = new MongoClient(runner.ConnectionString);
            var database = client.GetDatabase($"mass_run_repo_test_{Guid.NewGuid():N}");
            var repo = new MassAdjudicationRunRepositoryMongo(database);

            var summary = CreateSummary();
            summary.Id = "run-payment-filter";
            summary.ClaimResults.AddRange(new[]
            {
                new MassAdjudicationClaimResult
                {
                    GeneratedClaimId = "GEN-PAYMENT-MATCHED",
                    ClaimType = "Professional",
                    Outcome = "Paid",
                    ValidationStatus = "Matched",
                    PaymentDelta = 0.01m,
                    ElapsedMilliseconds = 10
                },
                new MassAdjudicationClaimResult
                {
                    GeneratedClaimId = "GEN-PAYMENT-MISMATCHED",
                    ClaimType = "Professional",
                    Outcome = "Paid",
                    ValidationStatus = "Matched",
                    PaymentDelta = 0.02m,
                    ElapsedMilliseconds = 20
                }
            });

            var saved = await repo.SaveAsync(summary);
            var rawClaimResults = database.GetCollection<BsonDocument>(
                MassAdjudicationRunRepositoryMongo.ClaimResultsCollectionName);
            await rawClaimResults.InsertOneAsync(new BsonDocument
            {
                ["_id"] = "missing-payment-delta",
                ["RunId"] = saved.Id,
                ["TenantId"] = saved.Run.TenantId,
                ["GeneratedClaimId"] = "GEN-PAYMENT-MISSING",
                ["ClaimType"] = "Professional",
                ["ValidationStatus"] = "Matched",
                ["Outcome"] = "Paid",
                ["AdjudicationSuccess"] = true,
                ["ElapsedMilliseconds"] = 30d,
                ["CreatedAtUtc"] = DateTime.UtcNow
            });

            var matched = await repo.ListClaimResultsAsync(
                saved.Run.TenantId,
                saved.Id,
                outcome: null,
                validationStatus: null,
                paymentStatus: "Matched",
                paymentTolerance: 0.01m,
                limit: 10);
            var scored = await repo.ListClaimResultsAsync(
                saved.Run.TenantId,
                saved.Id,
                outcome: null,
                validationStatus: null,
                paymentStatus: "Scored",
                paymentTolerance: 0.01m,
                limit: 10);
            var unscored = await repo.ListClaimResultsAsync(
                saved.Run.TenantId,
                saved.Id,
                outcome: null,
                validationStatus: null,
                paymentStatus: "Unscored",
                paymentTolerance: 0.01m,
                limit: 10);

            matched.Select(x => x.GeneratedClaimId).Should().Equal("GEN-PAYMENT-MATCHED");
            scored.Select(x => x.GeneratedClaimId)
                .Should().Equal("GEN-PAYMENT-MISMATCHED", "GEN-PAYMENT-MATCHED");
            unscored.Should().ContainSingle()
                .Which.GeneratedClaimId.Should().Be("GEN-PAYMENT-MISSING");
        }
        finally
        {
            try { runner.Dispose(); }
            catch (TypeLoadException) { /* see ProviderVersionEventPublisherTests note */ }
        }
    }

    [Fact]
    public async Task SaveAsync_when_claim_result_insert_fails_preserves_existing_summary_and_claim_results()
    {
        var database = Substitute.For<IMongoDatabase>();
        var runs = Substitute.For<IMongoCollection<MassAdjudicationRunSummary>>();
        var claimResults = Substitute.For<IMongoCollection<MassAdjudicationClaimResult>>();

        database.GetCollection<MassAdjudicationRunSummary>(
                MassAdjudicationRunRepositoryMongo.CollectionName,
                Arg.Any<MongoCollectionSettings?>())
            .Returns(runs);
        database.GetCollection<MassAdjudicationClaimResult>(
                MassAdjudicationRunRepositoryMongo.ClaimResultsCollectionName,
                Arg.Any<MongoCollectionSettings?>())
            .Returns(claimResults);

        runs
            .ReplaceOneAsync(
                Arg.Any<FilterDefinition<MassAdjudicationRunSummary>>(),
                Arg.Any<MassAdjudicationRunSummary>(),
                Arg.Any<ReplaceOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<ReplaceOneResult>()));
        claimResults
            .InsertManyAsync(
                Arg.Any<IEnumerable<MassAdjudicationClaimResult>>(),
                Arg.Any<InsertManyOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("insert failed")));

        var repo = new MassAdjudicationRunRepositoryMongo(database);
        var summary = CreateSummary();
        summary.ClaimResults.Add(new MassAdjudicationClaimResult
        {
            GeneratedClaimId = "GEN-002",
            ClaimType = "Institutional",
            Outcome = "PlatformFailure"
        });

        var act = () => repo.SaveAsync(summary);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("insert failed");
        await runs.DidNotReceive().DeleteOneAsync(
            Arg.Any<FilterDefinition<MassAdjudicationRunSummary>>(),
            Arg.Any<CancellationToken>());
        await claimResults.DidNotReceive().DeleteManyAsync(
            Arg.Any<FilterDefinition<MassAdjudicationClaimResult>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAsync_inserts_large_claim_result_sets_in_cosmos_safe_batches()
    {
        var database = Substitute.For<IMongoDatabase>();
        var runs = Substitute.For<IMongoCollection<MassAdjudicationRunSummary>>();
        var claimResults = Substitute.For<IMongoCollection<MassAdjudicationClaimResult>>();

        database.GetCollection<MassAdjudicationRunSummary>(
                MassAdjudicationRunRepositoryMongo.CollectionName,
                Arg.Any<MongoCollectionSettings?>())
            .Returns(runs);
        database.GetCollection<MassAdjudicationClaimResult>(
                MassAdjudicationRunRepositoryMongo.ClaimResultsCollectionName,
                Arg.Any<MongoCollectionSettings?>())
            .Returns(claimResults);
        runs.ReplaceOneAsync(
                Arg.Any<FilterDefinition<MassAdjudicationRunSummary>>(),
                Arg.Any<MassAdjudicationRunSummary>(),
                Arg.Any<ReplaceOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<ReplaceOneResult>()));
        claimResults.InsertManyAsync(
                Arg.Any<IEnumerable<MassAdjudicationClaimResult>>(),
                Arg.Any<InsertManyOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        claimResults.DeleteManyAsync(
                Arg.Any<FilterDefinition<MassAdjudicationClaimResult>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Substitute.For<DeleteResult>()));

        var summary = CreateSummary();
        summary.ClaimResults.AddRange(
            Enumerable.Range(1, 125).Select(index => new MassAdjudicationClaimResult
            {
                GeneratedClaimId = $"GEN-{index:D3}",
                ClaimType = "Professional",
                Outcome = "Paid"
            }));

        await new MassAdjudicationRunRepositoryMongo(database).SaveAsync(summary);

        var insertedBatches = claimResults.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IMongoCollection<MassAdjudicationClaimResult>.InsertManyAsync))
            .Select(call => ((IEnumerable<MassAdjudicationClaimResult>)call.GetArguments()[0]!).Count())
            .ToArray();
        insertedBatches.Should().Equal(50, 50, 25);
    }

    private static MassAdjudicationRunSummary CreateSummary() => new()
    {
        Run = new MassAdjudicationRunMetadata
        {
            TenantId = "tenant-a",
            RequestedClaims = 1,
            Seed = 42,
            Parallelism = 1,
            ClaimsUrl = "https://claims",
            BenefitUrl = "https://benefit",
            ProviderUrl = "https://provider",
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow
        },
        TotalClaims = 1,
        Processed = 1,
        Paid = 1
    };
}
