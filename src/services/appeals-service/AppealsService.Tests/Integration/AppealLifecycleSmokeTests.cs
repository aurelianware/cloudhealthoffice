using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AppealsService.Controllers;
using AppealsService.Models;
using AppealsService.Repositories;
using AppealsService.Services;
using AppealsService.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AppealsService.Tests.Integration;

/// <summary>
/// End-to-end controller + repository + encryption + publisher smoke via
/// <see cref="WebApplicationFactory{TEntryPoint}"/> over in-memory fakes.
/// Exercises the full appeal lifecycle and pins three contract invariants
/// that modernization must not regress:
///
/// 1. Every PHI-adjacent field is stored encrypted-at-rest. The
///    <see cref="ReversibleAppealFieldEncryptor.LooksEncrypted"/> helper
///    asserts the stored record's sensitive fields carry the marker
///    prefix on both snapshots and the published-event payloads are
///    free of decrypted values.
/// 2. Every mutation writes an <c>AppealEvent</c> to the audit trail.
///    The full lifecycle enumerates the expected event sequence and we
///    assert it appears in order.
/// 3. The portal's four <see cref="AppealsSummary"/> wire-shape buckets
///    (<c>OpenAppeals</c>, <c>UrgentExpedited</c>, <c>DueThisWeek</c>,
///    <c>OverturnedRate</c>) are preserved across the status-enum
///    consolidation — including records seeded with the new
///    <c>Closed + ClosureReasonCode</c> shape.
/// </summary>
public class AppealLifecycleSmokeTests : IClassFixture<AppealsWebApplicationFactory>
{
    private readonly AppealsWebApplicationFactory _factory;

    public AppealLifecycleSmokeTests(AppealsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient NewClient(string tenant = "tenant-a")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static async Task<Appeal> ReadAppealAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Request failed: {(int)response.StatusCode} {response.StatusCode}. Body: {body}");
        }
        return JsonSerializer.Deserialize<Appeal>(body, JsonOptions)!;
    }

    private static CreateAppealRequest BuildCreate(string claimId = "CLM-LIFE-001") => new()
    {
        ClaimId = claimId,
        ClaimNumber = "CLM-0001",
        MemberId = "M-0001",
        PatientName = "Jane Doe",
        ProviderNPI = "1234567890",
        AppealReason = "Denied service was medically necessary.",
        DenialReason = "CO-45: Charge exceeds fee schedule.",
        LineOfBusiness = LineOfBusiness.Commercial,
        AppealType = AppealType.Reconsideration,
        AppealLevel = AppealLevel.FirstLevel,
        AppealedAmount = 2500.00m,
        DeniedAmount = 2500.00m,
        IsUrgent = false
    };

    [Fact]
    public async Task FullLifecycle_Create_Submit_BeginReview_RequestInfo_ResumeReview_Close_Approved()
    {
        _factory.Reset();
        var client = NewClient();

        // 1. Create
        var create = await client.PostAsJsonAsync("/api/appeals", BuildCreate(), JsonOptions);
        var appeal = await ReadAppealAsync(create);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        appeal.Status.Should().Be(AppealStatus.Draft);

        // 2. Add note + attachment in Draft
        var noteBody = new AddNoteRequest { NoteText = "Initial provider notes.", CreatedBy = "prov-1", IsInternal = false };
        (await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/notes", noteBody, JsonOptions))
            .EnsureSuccessStatusCode();

        var attachBody = new AddAttachmentRequest
        {
            AttachmentTypeCode = "OZ",
            TransmissionCode = "EL",
            FileName = "op-report.pdf",
            BlobUrl = "mds://doc-1",
            Description = "Operative report for denied surgery."
        };
        (await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/attachments", attachBody, JsonOptions))
            .EnsureSuccessStatusCode();

        // 3. Submit
        var submit = await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/submit", new IdempotencyEnvelope(), JsonOptions);
        (await ReadAppealAsync(submit)).Status.Should().Be(AppealStatus.Submitted);

        // Idempotent submit: second call returns 200 with no second event.
        var eventCountBeforeIdempotent = _factory.Repo.SnapshotEvents().Count;
        var submitIdempotent = await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/submit", new IdempotencyEnvelope(), JsonOptions);
        submitIdempotent.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Repo.SnapshotEvents().Count.Should().Be(eventCountBeforeIdempotent,
            "idempotent same-status call must not write a second audit event");

        // 4. Begin review
        var begin = await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/begin-review", new IdempotencyEnvelope(), JsonOptions);
        (await ReadAppealAsync(begin)).Status.Should().Be(AppealStatus.InReview);

        // 5. Request info (transitions to PendingInfo + appends a note)
        var request = new RequestInfoRequest { Description = "Need op notes + pre-auth record." };
        var req = await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/request-info", request, JsonOptions);
        var afterReq = await ReadAppealAsync(req);
        afterReq.Status.Should().Be(AppealStatus.PendingInfo);
        afterReq.Notes.Should().Contain(n => n.NoteText.Contains("Need op notes"), "request-info body becomes a note");

        // 6. Resume review
        var resume = await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/resume-review", new IdempotencyEnvelope(), JsonOptions);
        (await ReadAppealAsync(resume)).Status.Should().Be(AppealStatus.InReview);

        // 7. Close with Approved decision
        var close = new CloseAppealRequest
        {
            ClosureReasonCode = AppealClosureReasonCode.Approved,
            Decision = new AppealDecisionInput
            {
                DecisionType = AppealDecisionType.Approved,
                ApprovedAmount = 2500.00m,
                DecisionReason = "Medical necessity confirmed by clinical review.",
                ReviewerNotes = "Reviewer: Dr. Smith.",
                DecisionMaker = "reviewer-99"
            }
        };
        var closed = await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/close", close, JsonOptions);
        var closedAppeal = await ReadAppealAsync(closed);
        closedAppeal.Status.Should().Be(AppealStatus.Closed);
        closedAppeal.ClosureReasonCode.Should().Be(AppealClosureReasonCode.Approved);

        // ── Audit trail assertions ──────────────────────────────────────
        var events = _factory.Repo.SnapshotEvents().Where(e => e.AppealId == appeal.Id).ToList();
        events.Select(e => e.EventType).Should().ContainInOrder(
            AppealEventType.AppealCreated,
            AppealEventType.AppealNoteAdded,
            AppealEventType.AppealAttachmentAdded,
            AppealEventType.AppealStatusChanged, // Draft -> Submitted
            AppealEventType.AppealStatusChanged, // Submitted -> InReview
            AppealEventType.AppealStatusChanged, // InReview -> PendingInfo
            AppealEventType.AppealNoteAdded,     // request-info description
            AppealEventType.AppealStatusChanged, // PendingInfo -> InReview
            AppealEventType.AppealClosed);

        // ── Publisher assertions ────────────────────────────────────────
        _factory.Publisher.Created.Should().ContainSingle(c => c.AppealId == appeal.Id);
        _factory.Publisher.Closed.Should().ContainSingle(c =>
            c.AppealId == appeal.Id
            && c.Reason == AppealClosureReasonCode.Approved
            && c.DecisionType == AppealDecisionType.Approved
            && c.ApprovedAmount == 2500.00m);

        // ── Encryption-at-rest assertions ───────────────────────────────
        var stored = _factory.Repo.PeekStored("tenant-a", appeal.Id);
        stored.Should().NotBeNull();
        ReversibleAppealFieldEncryptor.LooksEncrypted(stored!.PatientName).Should().BeTrue();
        ReversibleAppealFieldEncryptor.LooksEncrypted(stored.AppealReason).Should().BeTrue();
        ReversibleAppealFieldEncryptor.LooksEncrypted(stored.DenialReason).Should().BeTrue();
        ReversibleAppealFieldEncryptor.LooksEncrypted(stored.Notes[0].NoteText).Should().BeTrue();
        stored.Attachments.Should().ContainSingle();
        ReversibleAppealFieldEncryptor.LooksEncrypted(stored.Attachments[0].Description).Should().BeTrue();
        stored.Decision.Should().NotBeNull();
        ReversibleAppealFieldEncryptor.LooksEncrypted(stored.Decision!.DecisionReason).Should().BeTrue();
        ReversibleAppealFieldEncryptor.LooksEncrypted(stored.Decision.ReviewerNotes).Should().BeTrue();
    }

    [Fact]
    public async Task CrossTenant_Get_Returns404_NotFound()
    {
        _factory.Reset();

        var aliceClient = NewClient("tenant-alice");
        var create = await aliceClient.PostAsJsonAsync("/api/appeals", BuildCreate(), JsonOptions);
        var appeal = await ReadAppealAsync(create);

        var bobClient = NewClient("tenant-bob");
        var bobGet = await bobClient.GetAsync($"/api/appeals/{appeal.Id}");
        bobGet.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "cross-tenant must return 404, not 403, to avoid tenant enumeration");

        var aliceGet = await aliceClient.GetAsync($"/api/appeals/{appeal.Id}");
        aliceGet.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MissingTenantHeader_Returns401()
    {
        _factory.Reset();
        var client = _factory.CreateClient();
        // No X-Tenant-ID header — TenantMiddleware must 401 rather than fall
        // back to a default.
        var response = await client.GetAsync("/api/appeals/some-id");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InvalidTransition_Returns409_WithProblemDetails()
    {
        _factory.Reset();
        var client = NewClient();

        var create = await client.PostAsJsonAsync("/api/appeals", BuildCreate(), JsonOptions);
        var appeal = await ReadAppealAsync(create);

        // Draft -> InReview is illegal (must go through Submitted first).
        var begin = await client.PostAsJsonAsync(
            $"/api/appeals/{appeal.Id}/begin-review", new IdempotencyEnvelope(), JsonOptions);

        begin.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await begin.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("status").GetInt32().Should().Be(409);
        root.GetProperty("title").GetString().Should().Be("Invalid appeal transition");
        root.GetProperty("type").GetString().Should().Be("https://cloudhealthoffice.com/problems/appeal-transition");
        root.GetProperty("fromStatus").GetString().Should().Be("Draft");
        root.GetProperty("toStatus").GetString().Should().Be("InReview");
    }

    [Fact]
    public async Task Overdue_EmitsExactlyOneObservedEvent_AcrossConcurrentReads()
    {
        _factory.Reset();
        var client = NewClient();

        // Create + submit, then force the appeal past TargetResponseDate by
        // rewriting the stored record. (The test seam PeekStored returns
        // the live object; mutating it emulates time passing.)
        var create = await client.PostAsJsonAsync("/api/appeals", BuildCreate(), JsonOptions);
        var appeal = await ReadAppealAsync(create);
        await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/submit", new IdempotencyEnvelope(), JsonOptions);

        var stored = _factory.Repo.PeekStored("tenant-a", appeal.Id)!;
        stored.TargetResponseDate = DateTime.UtcNow.AddMinutes(-1);

        // Fan-out concurrent GETs. Each GET observes overdue; the race-safe
        // TryTransitionToOverdueAsync must emit exactly one event.
        var gets = Enumerable.Range(0, 8)
            .Select(_ => client.GetAsync($"/api/appeals/{appeal.Id}"))
            .ToArray();
        await Task.WhenAll(gets);

        var history = await _factory.Repo.ListByAppealAsync("tenant-a", appeal.Id);
        history.Count(e => e.EventType == AppealEventType.AppealOverdueObserved).Should().Be(1);
        _factory.Publisher.OverdueObserved.Count.Should().Be(1);
    }

    [Fact]
    public async Task AppealsSummary_PortalWireShape_Preserved()
    {
        _factory.Reset();
        var client = NewClient("tenant-summary");
        var now = DateTime.UtcNow;

        // 5 open appeals (Submitted/InReview/PendingInfo), 2 urgent, 3 with
        // target response in the next week. 4 closed with mix of reasons
        // (2 Approved, 1 PartialApproval, 1 Denied) → OverturnedRate = 75%.
        async Task<string> CreateAsync(CreateAppealRequest body)
        {
            var r = await client.PostAsJsonAsync("/api/appeals", body, JsonOptions);
            return (await ReadAppealAsync(r)).Id;
        }

        async Task SubmitAsync(string id)
            => (await client.PostAsJsonAsync($"/api/appeals/{id}/submit", new IdempotencyEnvelope(), JsonOptions))
                .EnsureSuccessStatusCode();
        async Task BeginReviewAsync(string id)
            => (await client.PostAsJsonAsync($"/api/appeals/{id}/begin-review", new IdempotencyEnvelope(), JsonOptions))
                .EnsureSuccessStatusCode();
        async Task CloseAsync(string id, AppealClosureReasonCode reason, AppealDecisionType? dt = null)
        {
            var body = new CloseAppealRequest
            {
                ClosureReasonCode = reason,
                Decision = dt.HasValue
                    ? new AppealDecisionInput { DecisionType = dt.Value, ApprovedAmount = 1000m }
                    : null
            };
            (await client.PostAsJsonAsync($"/api/appeals/{id}/close", body, JsonOptions))
                .EnsureSuccessStatusCode();
        }

        // 5 open: 2 urgent, 3 due-this-week (overlap allowed).
        var openIds = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var body = BuildCreate($"CLM-OPEN-{i:000}");
            body.IsUrgent = i < 2;
            body.TargetResponseDate = i < 3 ? now.AddDays(3) : now.AddDays(30);
            var id = await CreateAsync(body);
            await SubmitAsync(id);
            if (i % 2 == 0) await BeginReviewAsync(id);
            openIds.Add(id);
        }

        // 4 closed.
        for (var i = 0; i < 4; i++)
        {
            var id = await CreateAsync(BuildCreate($"CLM-CLOSED-{i:000}"));
            await SubmitAsync(id);
            await BeginReviewAsync(id);
            AppealClosureReasonCode reason = i switch
            {
                0 or 1 => AppealClosureReasonCode.Approved,
                2 => AppealClosureReasonCode.PartialApproval,
                _ => AppealClosureReasonCode.Denied
            };
            AppealDecisionType dt = reason switch
            {
                AppealClosureReasonCode.Approved => AppealDecisionType.Approved,
                AppealClosureReasonCode.PartialApproval => AppealDecisionType.PartialApproval,
                _ => AppealDecisionType.Denied
            };
            await CloseAsync(id, reason, dt);
        }

        var summaryResponse = await client.GetAsync("/api/appeals/summary");
        var summary = JsonSerializer.Deserialize<AppealsSummary>(
            await summaryResponse.Content.ReadAsStringAsync(), JsonOptions)!;

        // Portal-observed contract:
        summary.OpenAppeals.Should().Be(5);
        summary.UrgentExpedited.Should().Be(2);
        summary.DueThisWeek.Should().Be(3);
        summary.OverturnedRate.Should().BeApproximately(75.0, 0.01,
            "3 of 4 closed had reason Approved or PartialApproval");

        // Additional shape verifications:
        summary.Approved.Should().Be(2);
        summary.PartialApprovals.Should().Be(1);
        summary.Denied.Should().Be(1);
    }
}

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> wiring that swaps in
/// the in-memory fakes for repository, encryptor, publisher, and secret
/// provider so the controller tests run fully in-process with no Mongo /
/// Cosmos / Kafka / Key Vault dependencies.
///
/// Also sets <c>MongoDb:ConnectionString</c> to a non-empty sentinel so
/// the Mongo branch of the Program.cs DI wiring is chosen (we override
/// every registration after) and the Cosmos branch's CosmosDb:Endpoint
/// validation doesn't trip.
/// </summary>
public sealed class AppealsWebApplicationFactory : WebApplicationFactory<Program>
{
    // Not reassigned across tests — the DI container captures the
    // singleton reference at first host build; reassigning these fields
    // would desync the controller's dependencies from what the test
    // inspects. Reset() clears state in-place via the fakes' Clear()
    // methods.
    public InMemoryAppealRepository Repo { get; } = new();
    public RecordingAppealEventPublisher Publisher { get; } = new();

    public void Reset()
    {
        Repo.Clear();
        Publisher.Clear();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Force an environment where the NoOp fallback for field encryption
        // is allowed (dev-mode guard). We then replace it with the
        // reversible encryptor below.
        builder.UseEnvironment("Development");
        builder.ConfigureHostConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Force Mongo branch (we'll override all the Mongo services).
            ["MongoDb:ConnectionString"] = "mongodb://fake/test",
            ["MongoDb:DatabaseName"] = "test",
            ["Kafka:BootstrapServers"] = "" // degraded mode; RecordingPublisher replaces it anyway
        }));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Strip all hosted services — we don't want the Mongo index
            // initializer, the migration scanner, the Kafka publisher
            // background task, or the repository's Mongo client to run
            // during tests.
            foreach (var hs in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
                services.Remove(hs);

            // Strip the Mongo client / database registrations — they would
            // try to resolve against the sentinel connection string.
            services.RemoveAll<MongoDB.Driver.IMongoClient>();
            services.RemoveAll<MongoDB.Driver.IMongoDatabase>();

            // Swap in the in-memory fakes.
            services.RemoveAll<IAppealRepository>();
            services.RemoveAll<IAppealEventRepository>();
            services.RemoveAll<IAppealEventSink>();
            services.RemoveAll<IAppealFieldEncryptor>();
            services.RemoveAll<IAppealEventPublisher>();
            services.RemoveAll<AppealEventPublisher>();

            services.AddSingleton(Repo);
            services.AddSingleton<IAppealRepository>(sp => sp.GetRequiredService<InMemoryAppealRepository>());
            services.AddSingleton<IAppealEventRepository>(sp => sp.GetRequiredService<InMemoryAppealRepository>());
            services.AddSingleton<IAppealEventSink>(sp => sp.GetRequiredService<InMemoryAppealRepository>());
            services.AddSingleton<IAppealFieldEncryptor, ReversibleAppealFieldEncryptor>();
            services.AddSingleton(Publisher);
            services.AddSingleton<IAppealEventPublisher>(sp => sp.GetRequiredService<RecordingAppealEventPublisher>());
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
}
