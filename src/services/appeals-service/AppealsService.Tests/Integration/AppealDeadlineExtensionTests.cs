using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppealsService.Controllers;
using AppealsService.Models;
using AppealsService.Tests.Fakes;

namespace AppealsService.Tests.Integration;

/// <summary>
/// Controller-level coverage for the one-time regulatory deadline
/// extension (<c>POST /api/appeals/{id}/extend</c>) and for create-time
/// validation of external-review / Part D clocks. Runs over the same
/// in-memory fakes as <see cref="AppealLifecycleSmokeTests"/>.
/// </summary>
public class AppealDeadlineExtensionTests : IClassFixture<AppealsWebApplicationFactory>
{
    private readonly AppealsWebApplicationFactory _factory;

    public AppealDeadlineExtensionTests(AppealsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private HttpClient NewClient(string tenant = "tenant-ext")
    {
        var client = _factory.CreateDefaultClient(new CloudHealthOffice.Infrastructure.Security.ChoDevelopmentTokenHandler());
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private static CreateAppealRequest BuildCreate(
        LineOfBusiness lob,
        bool urgent = false,
        AppealType type = AppealType.Reconsideration,
        AppealLevel level = AppealLevel.FirstLevel) => new()
    {
        ClaimId = "CLM-EXT-001",
        ClaimNumber = "CLM-0001",
        MemberId = "M-0001",
        PatientName = "Jane Doe",
        ProviderNPI = "1234567890",
        AppealReason = "Denied service was medically necessary.",
        LineOfBusiness = lob,
        AppealType = type,
        AppealLevel = level,
        IsUrgent = urgent
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

    private async Task<Appeal> CreateSubmittedAsync(HttpClient client, CreateAppealRequest body)
    {
        var created = await ReadAppealAsync(await client.PostAsJsonAsync("/api/appeals", body, JsonOptions));
        return await ReadAppealAsync(await client.PostAsJsonAsync(
            $"/api/appeals/{created.Id}/submit", new IdempotencyEnvelope(), JsonOptions));
    }

    private static ExtendDeadlineRequest Extend(
        AppealExtensionReason reason = AppealExtensionReason.EnrolleeRequested,
        int days = 14,
        string? justification = null,
        DateTime? noticeSentAt = null,
        string? eventId = null) => new()
    {
        Reason = reason,
        ExtensionDays = days,
        Justification = justification,
        WrittenNoticeSentAt = noticeSentAt ?? DateTime.UtcNow,
        EventId = eventId
    };

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    // ── Happy paths ─────────────────────────────────────────────────────

    [Fact]
    public async Task MedicareAdvantage_Standard_EnrolleeRequested_Extends_By_14_Days_And_Audits()
    {
        _factory.Reset();
        var client = NewClient();
        var appeal = await CreateSubmittedAsync(client, BuildCreate(LineOfBusiness.Medicare));
        var before = appeal.TargetResponseDate!.Value.ToUniversalTime();
        var notice = DateTime.UtcNow;

        var response = await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/extend",
            Extend(noticeSentAt: notice), JsonOptions);
        var extended = await ReadAppealAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        extended.Status.Should().Be(AppealStatus.Submitted, "an extension is not a status transition");
        extended.TargetResponseDate!.Value.ToUniversalTime().Should().BeCloseTo(before.AddDays(14), TimeSpan.FromMilliseconds(1));
        extended.DeadlineExtension.Should().NotBeNull();
        extended.DeadlineExtension!.Reason.Should().Be(AppealExtensionReason.EnrolleeRequested);
        extended.DeadlineExtension.ExtensionDays.Should().Be(14);
        extended.DeadlineExtension.PreviousTargetResponseDate.ToUniversalTime()
            .Should().BeCloseTo(before, TimeSpan.FromMilliseconds(1));
        extended.DeadlineExtension.WrittenNoticeSentAt.ToUniversalTime()
            .Should().BeCloseTo(notice, TimeSpan.FromMilliseconds(1));
        extended.DeadlineExtension.RegulatoryBasis.Should().Contain("422.590(f)");
        extended.DeadlineExtension.ExtendedBy.Should().NotBeNullOrEmpty();

        // History: one AppealDeadlineExtended row with a non-PHI payload.
        var history = await client.GetFromJsonAsync<JsonElement>($"/api/appeals/{appeal.Id}/history", JsonOptions);
        var rows = history.GetProperty("items").EnumerateArray()
            .Where(e => e.GetProperty("eventType").GetString()!.Equals(
                nameof(AppealEventType.AppealDeadlineExtended), StringComparison.OrdinalIgnoreCase))
            .ToList();
        rows.Should().ContainSingle();

        var stored = _factory.Repo.SnapshotEvents()
            .Single(e => e.AppealId == appeal.Id && e.EventType == AppealEventType.AppealDeadlineExtended);
        stored.FromStatus.Should().BeNull();
        stored.ToStatus.Should().BeNull();
        stored.Payload!["reason"]!.GetValue<string>().Should().Be("EnrolleeRequested");
        stored.Payload!["extensionDays"]!.GetValue<int>().Should().Be(14);
        stored.Payload!["regulatoryBasis"]!.GetValue<string>().Should().Contain("422.590(f)");
        stored.Payload!.Select(kv => kv.Key).Should().BeEquivalentTo(new[]
        {
            "currentStatus", "reason", "extensionDays", "previousTargetResponseDate",
            "newTargetResponseDate", "writtenNoticeSentAt", "regulatoryBasis"
        });

        _factory.Publisher.DeadlineExtended.Should().ContainSingle()
            .Which.ExtensionDays.Should().Be(14);
    }

    [Fact]
    public async Task MedicareAdvantage_Expedited_Extension_Is_Allowed()
    {
        // 42 CFR 422.590(f) applies to expedited reconsiderations too.
        _factory.Reset();
        var client = NewClient();
        var appeal = await CreateSubmittedAsync(client, BuildCreate(LineOfBusiness.Medicare, urgent: true));
        var before = appeal.TargetResponseDate!.Value.ToUniversalTime();

        var extended = await ReadAppealAsync(await client.PostAsJsonAsync(
            $"/api/appeals/{appeal.Id}/extend", Extend(days: 10), JsonOptions));

        extended.TargetResponseDate!.Value.ToUniversalTime().Should().BeCloseTo(before.AddDays(10), TimeSpan.FromMilliseconds(1));
        extended.DeadlineExtension!.ExtensionDays.Should().Be(10);
    }

    [Fact]
    public async Task Medicaid_PlanNeedsInfo_Extension_Stores_Justification_As_Encrypted_Internal_Note()
    {
        _factory.Reset();
        var client = NewClient();
        var appeal = await CreateSubmittedAsync(client, BuildCreate(LineOfBusiness.Medicaid, urgent: true));
        await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/begin-review", new IdempotencyEnvelope(), JsonOptions);

        var extended = await ReadAppealAsync(await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/extend",
            Extend(AppealExtensionReason.PlanNeedsInfo,
                justification: "Awaiting records from the out-of-network specialist; may reverse the denial."),
            JsonOptions));

        extended.Status.Should().Be(AppealStatus.InReview);
        extended.DeadlineExtension!.Reason.Should().Be(AppealExtensionReason.PlanNeedsInfo);
        extended.DeadlineExtension.RegulatoryBasis.Should().Contain("438.408(c)");
        extended.Notes.Should().ContainSingle(n => n.IsInternal && n.NoteText.StartsWith("Awaiting records"));

        var stored = _factory.Repo.PeekStored("tenant-ext", appeal.Id)!;
        stored.Notes.Should().ContainSingle()
            .Which.NoteText.Should().Match(t => ReversibleAppealFieldEncryptor.LooksEncrypted(t));

        var events = _factory.Repo.SnapshotEvents().Where(e => e.AppealId == appeal.Id).ToList();
        events.Select(e => e.EventType).Should().ContainInOrder(
            AppealEventType.AppealDeadlineExtended, AppealEventType.AppealNoteAdded);
        events.Single(e => e.EventType == AppealEventType.AppealNoteAdded)
            .Payload!["context"]!.GetValue<string>().Should().Be("deadline-extension");
        events.Single(e => e.EventType == AppealEventType.AppealDeadlineExtended)
            .Payload!.ToJsonString().Should().NotContain("Awaiting records", "justification is PHI-adjacent free text");
    }

    // ── Once only / idempotency ─────────────────────────────────────────

    [Fact]
    public async Task Second_Extension_Returns409_But_Same_EventId_Replay_Returns200()
    {
        _factory.Reset();
        var client = NewClient();
        var appeal = await CreateSubmittedAsync(client, BuildCreate(LineOfBusiness.Medicare));
        var eventId = Guid.NewGuid().ToString();

        var first = await ReadAppealAsync(await client.PostAsJsonAsync(
            $"/api/appeals/{appeal.Id}/extend", Extend(days: 7, eventId: eventId), JsonOptions));

        var replay = await client.PostAsJsonAsync(
            $"/api/appeals/{appeal.Id}/extend", Extend(days: 7, eventId: eventId), JsonOptions);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAppealAsync(replay)).TargetResponseDate.Should().Be(first.TargetResponseDate);

        var second = await client.PostAsJsonAsync(
            $"/api/appeals/{appeal.Id}/extend", Extend(days: 7), JsonOptions);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await ReadProblemAsync(second);
        problem.GetProperty("type").GetString().Should().Be(AppealsController.ExtensionProblemType);
        problem.GetProperty("extensionRefusal").GetString().Should().Be("already-extended");

        _factory.Repo.SnapshotEvents()
            .Count(e => e.AppealId == appeal.Id && e.EventType == AppealEventType.AppealDeadlineExtended)
            .Should().Be(1);
    }

    // ── Line of business / tier refusals ────────────────────────────────

    [Theory]
    [InlineData(LineOfBusiness.Commercial, AppealType.Reconsideration, AppealLevel.FirstLevel)]
    [InlineData(LineOfBusiness.Marketplace, AppealType.Reconsideration, AppealLevel.FirstLevel)]
    [InlineData(LineOfBusiness.MedicarePartD, AppealType.Reconsideration, AppealLevel.FirstLevel)]
    [InlineData(LineOfBusiness.Medicare, AppealType.Reconsideration, AppealLevel.ExternalReview)]
    [InlineData(LineOfBusiness.Medicaid, AppealType.ExternalReview, AppealLevel.ExternalReview)]
    public async Task Extension_Not_Permitted_Returns422(LineOfBusiness lob, AppealType type, AppealLevel level)
    {
        _factory.Reset();
        var client = NewClient();
        var appeal = await CreateSubmittedAsync(client, BuildCreate(lob, type: type, level: level));

        var response = await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/extend", Extend(), JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var problem = await ReadProblemAsync(response);
        problem.GetProperty("extensionRefusal").GetString().Should().Be("not-permitted");
        problem.GetProperty("regulatoryBasis").GetString().Should().NotBeNullOrWhiteSpace();
        _factory.Repo.PeekStored("tenant-ext", appeal.Id)!.DeadlineExtension.Should().BeNull();
        _factory.Repo.SnapshotEvents().Should().NotContain(e => e.EventType == AppealEventType.AppealDeadlineExtended);
    }

    // ── State refusals ──────────────────────────────────────────────────

    [Fact]
    public async Task Extension_From_Draft_Returns409()
    {
        _factory.Reset();
        var client = NewClient();
        var draft = await ReadAppealAsync(await client.PostAsJsonAsync(
            "/api/appeals", BuildCreate(LineOfBusiness.Medicare), JsonOptions));

        var response = await client.PostAsJsonAsync($"/api/appeals/{draft.Id}/extend", Extend(), JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadProblemAsync(response)).GetProperty("extensionRefusal").GetString().Should().Be("invalid-status");
    }

    [Fact]
    public async Task Extension_After_Close_Returns409()
    {
        _factory.Reset();
        var client = NewClient();
        var appeal = await CreateSubmittedAsync(client, BuildCreate(LineOfBusiness.Medicaid));
        (await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/withdraw", new WithdrawRequest(), JsonOptions))
            .EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/extend", Extend(), JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadProblemAsync(response)).GetProperty("extensionRefusal").GetString().Should().Be("invalid-status");
    }

    [Fact]
    public async Task Extension_After_Deadline_Lapsed_Returns409()
    {
        _factory.Reset();
        var client = NewClient();
        var body = BuildCreate(LineOfBusiness.Medicare);
        body.TargetResponseDate = DateTime.UtcNow.AddMinutes(-5);
        var appeal = await CreateSubmittedAsync(client, body);

        var response = await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/extend",
            Extend(noticeSentAt: DateTime.UtcNow), JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadProblemAsync(response)).GetProperty("extensionRefusal").GetString().Should().Be("deadline-lapsed");
    }

    // ── Request validation ──────────────────────────────────────────────

    [Fact]
    public async Task PlanNeedsInfo_Without_Justification_Returns400()
    {
        _factory.Reset();
        var client = NewClient();
        var appeal = await CreateSubmittedAsync(client, BuildCreate(LineOfBusiness.Medicare));

        var response = await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/extend",
            Extend(AppealExtensionReason.PlanNeedsInfo), JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(nameof(ExtendDeadlineRequest.Justification));
    }

    [Fact]
    public async Task Notice_Sent_In_The_Future_Returns400()
    {
        _factory.Reset();
        var client = NewClient();
        var appeal = await CreateSubmittedAsync(client, BuildCreate(LineOfBusiness.Medicare));

        var response = await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/extend",
            Extend(noticeSentAt: DateTime.UtcNow.AddDays(1)), JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(nameof(ExtendDeadlineRequest.WrittenNoticeSentAt));
    }

    [Fact]
    public async Task Missing_Notice_Timestamp_Returns400()
    {
        _factory.Reset();
        var client = NewClient();
        var appeal = await CreateSubmittedAsync(client, BuildCreate(LineOfBusiness.Medicare));

        var response = await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/extend",
            new { reason = "enrolleeRequested", extensionDays = 14 }, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public async Task ExtensionDays_Outside_1_To_14_Returns400(int days)
    {
        _factory.Reset();
        var client = NewClient();
        var appeal = await CreateSubmittedAsync(client, BuildCreate(LineOfBusiness.Medicare));

        var response = await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/extend",
            Extend(days: days), JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Extension_For_Unknown_Appeal_Returns404()
    {
        _factory.Reset();
        var client = NewClient();

        var response = await client.PostAsJsonAsync("/api/appeals/does-not-exist/extend", Extend(), JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Create-time clocks for external review and Part D ───────────────

    [Theory]
    [InlineData(LineOfBusiness.Commercial, AppealType.ExternalReview, AppealLevel.ExternalReview, 45)]
    [InlineData(LineOfBusiness.Marketplace, AppealType.Reconsideration, AppealLevel.ExternalReview, 45)]
    [InlineData(LineOfBusiness.Medicaid, AppealType.ExternalReview, AppealLevel.FirstLevel, 90)]
    [InlineData(LineOfBusiness.Medicare, AppealType.Reconsideration, AppealLevel.ExternalReview, 30)]
    [InlineData(LineOfBusiness.MedicarePartD, AppealType.Reconsideration, AppealLevel.FirstLevel, 7)]
    [InlineData(LineOfBusiness.MedicarePartD, AppealType.Reconsideration, AppealLevel.ExternalReview, 7)]
    public async Task Create_Defaults_TargetResponseDate_To_Tier_Clock(
        LineOfBusiness lob, AppealType type, AppealLevel level, int days)
    {
        _factory.Reset();
        var client = NewClient();

        var before = DateTime.UtcNow;
        var appeal = await ReadAppealAsync(await client.PostAsJsonAsync(
            "/api/appeals", BuildCreate(lob, type: type, level: level), JsonOptions));
        var after = DateTime.UtcNow;

        appeal.TargetResponseDate!.Value.ToUniversalTime()
            .Should().BeOnOrAfter(before.AddDays(days)).And.BeOnOrBefore(after.AddDays(days));
    }

    [Theory]
    [InlineData(LineOfBusiness.Commercial, 40)]  // inside the 45-day ACA external review clock
    [InlineData(LineOfBusiness.Medicaid, 85)]    // inside the 90-day State Fair Hearing clock
    public async Task Create_External_Review_Override_Beyond_Internal_Clock_Is_Honored(LineOfBusiness lob, int days)
    {
        // Before external-review clocks were modeled these overrides were
        // rejected against the 30-day internal maximum.
        _factory.Reset();
        var client = NewClient();
        var body = BuildCreate(lob, level: AppealLevel.ExternalReview);
        var requested = DateTime.UtcNow.AddDays(days);
        requested = new DateTime(requested.Ticks - requested.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        body.TargetResponseDate = requested;

        var response = await client.PostAsJsonAsync("/api/appeals", body, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadAppealAsync(response)).TargetResponseDate!.Value.ToUniversalTime().Should().Be(requested);
    }

    [Theory]
    [InlineData(LineOfBusiness.Commercial, AppealLevel.ExternalReview, false, 50 * 24)]  // > 45 days
    [InlineData(LineOfBusiness.Commercial, AppealLevel.ExternalReview, true, 96)]        // > 72 hours
    [InlineData(LineOfBusiness.Medicaid, AppealLevel.ExternalReview, false, 95 * 24)]    // > 90 days
    [InlineData(LineOfBusiness.MedicarePartD, AppealLevel.FirstLevel, false, 10 * 24)]   // > 7 days
    public async Task Create_Override_Beyond_Tier_Clock_Returns400_With_Problem_Type(
        LineOfBusiness lob, AppealLevel level, bool urgent, int hours)
    {
        _factory.Reset();
        var client = NewClient();
        var body = BuildCreate(lob, urgent: urgent, level: level);
        body.TargetResponseDate = DateTime.UtcNow.AddHours(hours);

        var response = await client.PostAsJsonAsync("/api/appeals", body, JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await ReadProblemAsync(response);
        problem.GetProperty("type").GetString().Should().Be(AppealsController.TargetResponseDateProblemType);
        problem.GetProperty("errors").TryGetProperty(nameof(CreateAppealRequest.TargetResponseDate), out _)
            .Should().BeTrue();
        _factory.Repo.SnapshotEvents().Should().BeEmpty("a rejected create must not persist anything");
    }
}
