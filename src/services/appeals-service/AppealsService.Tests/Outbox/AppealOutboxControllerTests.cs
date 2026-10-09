using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppealsService.Controllers;
using AppealsService.Models;
using AppealsService.Services;
using AppealsService.Tests.Integration;
using CloudHealthOffice.Infrastructure.Security;

namespace AppealsService.Tests.Outbox;

/// <summary>
/// Outbox behavior through the HTTP surface: a request whose inline publish
/// fails (or never happens) still commits its event, and the relay
/// delivers it later; dead letters are replayed through an admin endpoint.
/// </summary>
public sealed class AppealOutboxControllerTests : IClassFixture<AppealsWebApplicationFactory>
{
    private const string Tenant = "tenant-outbox";
    private readonly AppealsWebApplicationFactory _factory;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public AppealOutboxControllerTests(AppealsWebApplicationFactory factory) => _factory = factory;

    private HttpClient NewClient(params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("outbox-user", roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private async Task<Appeal> CreateAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/appeals", new CreateAppealRequest
        {
            ClaimId = "CLM-OUT-1",
            ClaimNumber = "CLM-0001",
            MemberId = "M-0001",
            PatientName = "Jane Doe",
            ProviderNPI = "1234567890",
            AppealReason = "Medically necessary.",
            LineOfBusiness = LineOfBusiness.Commercial,
            AppealType = AppealType.Reconsideration,
            AppealLevel = AppealLevel.FirstLevel
        }, JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("\"outbox\"", "outbox bookkeeping is never part of an API response")
            .And.NotContain("outboxLease");
        return JsonSerializer.Deserialize<Appeal>(body, JsonOptions)!;
    }

    private Task<int> RelayAsync() => _factory.Publisher.DispatcherFor(_factory.Repo).DispatchPendingAsync();

    [Fact]
    public async Task Kafka_Down_During_The_Request_Then_The_Relay_Publishes_The_Event()
    {
        _factory.Reset();
        var client = NewClient();
        _factory.Publisher.Down = true;

        var appeal = await CreateAsync(client);
        (await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/submit", new IdempotencyEnvelope(), JsonOptions))
            .StatusCode.Should().Be(HttpStatusCode.OK, "a Kafka outage never fails or rolls back the change");

        _factory.Publisher.Produced.Should().BeEmpty();
        _factory.Repo.OutboxOf(Tenant, appeal.Id).Should().HaveCount(2)
            .And.OnlyContain(m => m.Status == AppealOutboxStatus.Pending);

        _factory.Publisher.Down = false;
        (await RelayAsync()).Should().Be(2);
        _factory.Publisher.Created.Should().ContainSingle(c => c.AppealId == appeal.Id);
        _factory.Publisher.StatusChanged.Should().ContainSingle(c =>
            c.AppealId == appeal.Id && c.From == AppealStatus.Draft && c.To == AppealStatus.Submitted);
        _factory.Publisher.Produced.Select(m => m.EventType).Should().Equal(
            AppealEventPublisher.AppealCreatedType, AppealEventPublisher.AppealStatusChangedType);
    }

    [Fact]
    public async Task Failure_After_The_State_Write_Does_Not_Lose_The_Event()
    {
        _factory.Reset();
        var client = NewClient();
        var appeal = await CreateAsync(client);

        // The transition commits, then the request dies before its publish.
        _factory.Repo.FailAuditAppendOnce();
        var failed = await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/submit", new IdempotencyEnvelope(), JsonOptions);
        failed.IsSuccessStatusCode.Should().BeFalse();
        _factory.Repo.PeekStored(Tenant, appeal.Id)!.Status.Should().Be(AppealStatus.Submitted);
        _factory.Publisher.StatusChanged.Should().BeEmpty();

        (await RelayAsync()).Should().Be(1);
        _factory.Publisher.StatusChanged.Should().ContainSingle(c => c.AppealId == appeal.Id && c.To == AppealStatus.Submitted);
    }

    [Fact]
    public async Task Client_Supplied_EventId_Is_The_Kafka_Event_Id()
    {
        _factory.Reset();
        var client = NewClient();
        var appeal = await CreateAsync(client);
        var eventId = Guid.NewGuid().ToString();

        (await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/submit",
            new IdempotencyEnvelope { EventId = eventId }, JsonOptions)).EnsureSuccessStatusCode();

        _factory.Publisher.Produced.Should().ContainSingle(m => m.EventId == eventId)
            .Which.PayloadJson.Should().Contain($"\"eventId\":\"{eventId}\"");
        _factory.Repo.SnapshotEvents().Should().ContainSingle(e => e.EventId == eventId);
    }

    [Fact]
    public async Task Replay_Endpoint_Requeues_Dead_Letters_For_Admins_Only()
    {
        _factory.Reset();
        var admin = NewClient();
        var appeal = await CreateAsync(admin);
        var entry = _factory.Repo.OutboxOf(Tenant, appeal.Id).Single();
        entry.Status = AppealOutboxStatus.DeadLettered;
        entry.Attempts = 10;
        await _factory.Repo.UpdateMessageAsync(Tenant, appeal.Id, entry);
        while (_factory.Publisher.Produced.TryDequeue(out _)) { }

        var reviewer = NewClient(ChoRolePermissions.UMCoordinator); // appeals:read + appeals:write
        (await reviewer.PostAsync($"/api/appeals/{appeal.Id}/outbox/replay", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var response = await admin.PostAsync($"/api/appeals/{appeal.Id}/outbox/replay?eventId={entry.EventId}", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<OutboxReplayResponse>(JsonOptions))!.Requeued.Should().Be(1);

        _factory.Publisher.Produced.Should().ContainSingle(m => m.EventId == entry.EventId);
        _factory.Repo.OutboxOf(Tenant, appeal.Id).Single().Status.Should().Be(AppealOutboxStatus.Sent);

        (await admin.PostAsync($"/api/appeals/{Guid.NewGuid()}/outbox/replay", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
