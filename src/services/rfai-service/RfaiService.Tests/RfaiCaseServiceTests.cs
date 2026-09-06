using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RfaiService.Models;
using RfaiService.Repositories;
using RfaiService.Services;

namespace RfaiService.Tests;

/// <summary>
/// Orchestration over the aggregate: idempotent creation, the one-open-cycle
/// rule, concurrency on the conditional insert, and the single resume-review
/// announcement.
/// </summary>
public class RfaiCaseServiceTests
{
    private const string Tenant = "tenant-a";
    private const string AuthNumber = "PAS-20260906-ABCD1234";

    private static RfaiCreationRequest Request(string? correlationKey = "decision-1") => new()
    {
        TenantId = Tenant,
        AuthNumber = AuthNumber,
        CorrelationKey = correlationKey,
        ReviewDecision = "A4",
        RequestSource = RfaiRequestSources.ReviewDecisionA4,
        RequestedItems = [new RequestedItem { Code = "AS", Description = "Discharge summary" }],
    };

    private static RfaiResponseArtifact Artifact(string submissionId = "sub-1") => new()
    {
        SubmissionId = submissionId,
        ContentType = "application/pdf",
        SizeBytes = 512,
        Channel = RfaiResponseChannels.CdexSubmitAttachment,
    };

    private static (RfaiCaseService Service, FakeRepository Repository, FakeProducer Kafka) Build()
    {
        var repository = new FakeRepository();
        var kafka = new FakeProducer();

        var service = new RfaiCaseService(
            repository,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            NullLogger<RfaiCaseService>.Instance,
            kafka);

        return (service, repository, kafka);
    }

    // ── Creation ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARequestIsCreatedOnceAndReplayedThereafter()
    {
        var (service, repository, _) = Build();

        var first = await service.EnsureRequestAsync(Request());
        var replay = await service.EnsureRequestAsync(Request());

        first.Created.Should().BeTrue();
        replay.Created.Should().BeFalse();
        replay.Case.Id.Should().Be(first.Case.Id);
        replay.Case.TrackingId.Should().Be(first.Case.TrackingId,
            "a replay must not re-issue the handle the provider already holds");
        repository.All.Should().ContainSingle();
    }

    [Fact]
    public async Task AReplayIsRecognisedEvenAfterTheCycleHasClosed()
    {
        // Otherwise a redelivered A4 event would open a SECOND cycle simply
        // because the first has since been answered and closed.
        var (service, repository, _) = Build();

        var created = await service.EnsureRequestAsync(Request());
        var stored = (await repository.GetByIdAsync(Tenant, created.Case.Id))!;
        RfaiCaseLifecycle.Close(stored, "reviewer-1", "done", DateTime.UtcNow);
        await repository.UpdateAsync(stored);

        var replay = await service.EnsureRequestAsync(Request());

        replay.Created.Should().BeFalse();
        replay.Case.Id.Should().Be(created.Case.Id);
        repository.All.Should().ContainSingle();
    }

    [Fact]
    public async Task ADifferentDecisionWhileACycleIsOpenReusesThatCycle()
    {
        // Two concurrently open requests would leave the provider guessing which
        // one their documents answer.
        var (service, repository, _) = Build();

        await service.EnsureRequestAsync(Request("decision-1"));
        var second = await service.EnsureRequestAsync(Request("decision-2"));

        second.Created.Should().BeFalse();
        second.ReusedOpenCycle.Should().BeTrue();
        repository.All.Should().ContainSingle();
    }

    [Fact]
    public async Task ANewCycleIsOnlyOpenedOnceTheLastOneIsFinished()
    {
        var (service, repository, _) = Build();

        var first = await service.EnsureRequestAsync(Request("decision-1"));
        var stored = (await repository.GetByIdAsync(Tenant, first.Case.Id))!;
        RfaiCaseLifecycle.Close(stored, "reviewer-1", "satisfied", DateTime.UtcNow);
        await repository.UpdateAsync(stored);

        var second = await service.EnsureRequestAsync(Request("decision-2"));

        second.Created.Should().BeTrue();
        second.Case.Sequence.Should().Be(2);
        second.Case.TrackingId.Should().NotBe(first.Case.TrackingId);
        repository.All.Should().HaveCount(2, "the first cycle's evidence is retained");
    }

    [Fact]
    public async Task TwoWorkersOnOneDecisionProduceOneRequest()
    {
        var (service, repository, _) = Build();

        var raced = false;
        repository.OnBeforeCreate = candidate =>
        {
            if (raced) return;
            raced = true;
            repository.CreateIfAbsentAsync(candidate).GetAwaiter().GetResult();
        };

        var result = await service.EnsureRequestAsync(Request());

        result.Created.Should().BeFalse("the other worker's insert won");
        repository.All.Should().ContainSingle();
    }

    [Fact]
    public async Task ARequestThatNamesNothingIsRefusedBeforeAnyWrite()
    {
        var (service, repository, _) = Build();

        var act = async () => await service.EnsureRequestAsync(
            Request() with { RequestedItems = [] });

        await act.Should().ThrowAsync<ArgumentException>();
        repository.All.Should().BeEmpty();
    }

    // ── Response intake ──────────────────────────────────────────────────────

    [Fact]
    public async Task TheResumeReviewAnnouncementIsRaisedOnTheTransitionOnly()
    {
        var (service, repository, kafka) = Build();
        var created = await service.EnsureRequestAsync(Request());

        await service.RecordResponseAsync(Tenant, created.Case.Id, [Artifact()]);
        await service.RecordResponseAsync(Tenant, created.Case.Id, [Artifact()]);

        kafka.Messages.Should().ContainSingle(
            "a replay must not tell the authorization to resume review twice");

        var stored = (await repository.GetByIdAsync(Tenant, created.Case.Id))!;
        stored.Status.Should().Be(RfaiStatus.DocsReceived);
        stored.ReceivedAttachments.Should().ContainSingle();
    }

    [Fact]
    public async Task AnAnnouncementCarriesIdentifiersOnlyNeverContent()
    {
        var (service, _, kafka) = Build();
        var created = await service.EnsureRequestAsync(Request());

        await service.RecordResponseAsync(Tenant, created.Case.Id,
        [
            Artifact() with { Title = "Discharge summary for Jane Doe", AttachmentControlNumber = "ACN-1" },
        ]);

        var payload = System.Text.Json.JsonSerializer.Serialize(kafka.Messages.Single());

        payload.Should().Contain(created.Case.AuthNumber);
        payload.Should().NotContain("Jane Doe", "a document title is not an identifier");
        payload.Should().NotContain("application/pdf");
    }

    [Fact]
    public async Task AResponseToAnUnknownRequestReportsNothingFound()
        => (await Build().Service.RecordResponseAsync(Tenant, "rfai-missing", [Artifact()]))
            .Should().BeNull();

    [Fact]
    public async Task ARequestBelongingToAnotherTenantIsInvisible()
    {
        var (service, _, _) = Build();
        var created = await service.EnsureRequestAsync(Request());

        (await service.RecordResponseAsync("tenant-b", created.Case.Id, [Artifact()]))
            .Should().BeNull("every lookup is tenant-scoped");

        (await service.MarkDeliveredAsync("tenant-b", created.Case.Id)).Should().BeNull();
    }

    [Fact]
    public async Task AFailedAnnouncementLeavesTheResponseDurable()
    {
        // The documents are already stored and recorded. Losing the announcement
        // delays the authorization returning to review; it must not fail the
        // caller and invite a retry that re-uploads content already held.
        var repository = new FakeRepository();
        var service = new RfaiCaseService(
            repository,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            NullLogger<RfaiCaseService>.Instance,
            new ThrowingProducer());

        var created = await service.EnsureRequestAsync(Request());

        var result = await service.RecordResponseAsync(Tenant, created.Case.Id, [Artifact()]);

        result!.Outcome.Should().Be(RfaiIntakeOutcome.Accepted);
        (await repository.GetByIdAsync(Tenant, created.Case.Id))!
            .ReceivedAttachments.Should().ContainSingle();
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private sealed class FakeRepository : IRfaiRepository
    {
        private readonly Dictionary<string, RfaiCase> _store = new(StringComparer.Ordinal);

        public Action<RfaiCase>? OnBeforeCreate { get; set; }

        public IReadOnlyList<RfaiCase> All => _store.Values.Select(Clone).ToList();

        private static string Key(string tenantId, string id) => $"{tenantId}|{id}";

        private static RfaiCase Clone(RfaiCase c) =>
            System.Text.Json.JsonSerializer.Deserialize<RfaiCase>(
                System.Text.Json.JsonSerializer.Serialize(c))!;

        public Task<RfaiCase?> GetByIdAsync(string tenantId, string id)
            => Task.FromResult(_store.TryGetValue(Key(tenantId, id), out var c) ? Clone(c) : null);

        public Task<List<RfaiCase>> GetByAuthNumberAsync(string tenantId, string authNumber)
            => Task.FromResult(_store.Values
                .Where(c => c.TenantId == tenantId && c.AuthNumber == authNumber)
                .OrderByDescending(c => c.Sequence)
                .Select(Clone)
                .ToList());

        public Task<RfaiCase?> GetByTrackingIdAsync(string tenantId, string trackingId)
            => Task.FromResult(_store.Values
                .Where(c => c.TenantId == tenantId && c.TrackingId == trackingId)
                .Select(Clone)
                .FirstOrDefault());

        public Task<RfaiCase> CreateAsync(RfaiCase rfaiCase)
        {
            _store[Key(rfaiCase.TenantId, rfaiCase.Id)] = Clone(rfaiCase);
            return Task.FromResult(Clone(rfaiCase));
        }

        public Task<(RfaiCase Case, bool Created)> CreateIfAbsentAsync(RfaiCase rfaiCase)
        {
            OnBeforeCreate?.Invoke(rfaiCase);

            var key = Key(rfaiCase.TenantId, rfaiCase.Id);
            if (_store.TryGetValue(key, out var existing))
                return Task.FromResult((Clone(existing), false));

            _store[key] = Clone(rfaiCase);
            return Task.FromResult((Clone(rfaiCase), true));
        }

        public Task<RfaiCase> UpdateAsync(RfaiCase rfaiCase)
        {
            _store[Key(rfaiCase.TenantId, rfaiCase.Id)] = Clone(rfaiCase);
            return Task.FromResult(Clone(rfaiCase));
        }
    }

    private sealed class FakeProducer : IKafkaProducerService
    {
        public List<object> Messages { get; } = new();

        public Task SendAsync(
            string topic, string key, object value, Dictionary<string, string>? headers = null)
        {
            Messages.Add(value);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingProducer : IKafkaProducerService
    {
        public Task SendAsync(
            string topic, string key, object value, Dictionary<string, string>? headers = null)
            => throw new InvalidOperationException("broker unavailable");
    }
}
