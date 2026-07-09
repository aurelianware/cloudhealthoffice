using ClaimsService.Models;
using ClaimsService.Repositories;
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
            AdjudicationSuccess = true
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
    }

    [Fact]
    public async Task SaveAsync_preserves_mcc_evidence_summary_fields()
    {
        var repo = new InMemoryMassAdjudicationRunRepository();
        var summary = CreateSummary();
        summary.Pended = 46;
        summary.ObservationTimeouts = 1;
        summary.WorkflowUnsupported = 73;
        summary.WorkflowObservationTimeouts = 2;
        summary.AveragePaymentDelta = 59.36m;
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
        reread.WorkflowUnsupported.Should().Be(73);
        reread.WorkflowObservationTimeouts.Should().Be(2);
        reread.AveragePaymentDelta.Should().Be(59.36m);
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
    public async Task SaveAsync_when_claim_result_insert_fails_deletes_inserted_summary()
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
        await runs.Received(1).DeleteOneAsync(
            Arg.Any<FilterDefinition<MassAdjudicationRunSummary>>(),
            Arg.Any<CancellationToken>());
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
