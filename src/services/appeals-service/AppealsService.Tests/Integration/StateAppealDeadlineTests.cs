using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppealsService.Controllers;
using AppealsService.Models;
using AppealsService.Services;
using CloudHealthOffice.PlanConfiguration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace AppealsService.Tests.Integration;

/// <summary>
/// Create and extend through the controller with a State on the case:
/// California Medi-Cal grievances get 30 days / 72 hours and no extension,
/// Texas keeps the federal 90 days and 14-day extension.
/// </summary>
public class StateAppealDeadlineTests : IClassFixture<AppealsWebApplicationFactory>
{
    private const string CaliforniaTenant = "tenant-medi-cal";

    private readonly AppealsWebApplicationFactory _factory;

    public StateAppealDeadlineTests(AppealsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private HttpClient NewClient(string tenant = "tenant-state", bool californiaDefault = false)
    {
        var factory = californiaDefault
            ? _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"AppealJurisdictions:Tenants:{CaliforniaTenant}"] = "ca"
                })))
            : _factory;
        var client = factory.CreateDefaultClient(new CloudHealthOffice.Infrastructure.Security.ChoDevelopmentTokenHandler());
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private static CreateAppealRequest Grievance(string? state, bool urgent = false, DateTime? target = null) => new()
    {
        ClaimId = "CLM-ST-001",
        ClaimNumber = "CLM-0001",
        MemberId = "M-0001",
        PatientName = "Jane Doe",
        ProviderNPI = "1234567890",
        AppealReason = "Waited three weeks for a specialist appointment.",
        LineOfBusiness = LineOfBusiness.Medicaid,
        AppealType = AppealType.Grievance,
        State = state,
        IsUrgent = urgent,
        TargetResponseDate = target
    };

    private static async Task<Appeal> ReadAppealAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Request failed: {(int)response.StatusCode}. Body: {body}");
        return JsonSerializer.Deserialize<Appeal>(body, JsonOptions)!;
    }

    private static async Task<Appeal> CreateSubmittedAsync(HttpClient client, CreateAppealRequest body)
    {
        var created = await ReadAppealAsync(await client.PostAsJsonAsync("/api/appeals", body, JsonOptions));
        return await ReadAppealAsync(await client.PostAsJsonAsync(
            $"/api/appeals/{created.Id}/submit", new IdempotencyEnvelope(), JsonOptions));
    }

    private static Task<HttpResponseMessage> ExtendAsync(HttpClient client, string id, int days = 14) =>
        client.PostAsJsonAsync($"/api/appeals/{id}/extend", new ExtendDeadlineRequest
        {
            Reason = AppealExtensionReason.EnrolleeRequested,
            ExtensionDays = days,
            WrittenNoticeSentAt = DateTime.UtcNow
        }, JsonOptions);

    private static TimeSpan Window(Appeal a) => a.TargetResponseDate!.Value.ToUniversalTime() - a.SubmittedDate.ToUniversalTime();

    [Fact]
    public async Task California_grievance_is_due_in_30_days_and_cannot_be_extended()
    {
        _factory.Reset();
        var client = NewClient();
        var appeal = await CreateSubmittedAsync(client, Grievance("CA"));

        Window(appeal).Should().BeCloseTo(TimeSpan.FromDays(30), TimeSpan.FromSeconds(1));
        appeal.State.Should().Be("CA");
        appeal.RulePackVersion.Should().StartWith("ca-medicaid:");

        var response = await ExtendAsync(client, appeal.Id);
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        problem.GetProperty("regulatoryBasis").GetString().Should().Contain("APL 21-011");
    }

    [Fact]
    public async Task California_expedited_grievance_is_due_in_72_hours()
    {
        _factory.Reset();
        var appeal = await CreateSubmittedAsync(NewClient(), Grievance("ca", urgent: true));

        Window(appeal).Should().BeCloseTo(TimeSpan.FromHours(72), TimeSpan.FromSeconds(1));
        appeal.State.Should().Be("CA");
    }

    [Fact]
    public async Task California_grievance_target_past_30_days_is_rejected()
    {
        _factory.Reset();
        var response = await NewClient().PostAsJsonAsync("/api/appeals",
            Grievance("CA", target: DateTime.UtcNow.AddDays(45)), JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(AppealsController.TargetResponseDateProblemType);
    }

    [Fact]
    public async Task Texas_grievance_keeps_90_days_and_the_14_day_extension()
    {
        _factory.Reset();
        var client = NewClient();
        var appeal = await CreateSubmittedAsync(client, Grievance("TX"));

        Window(appeal).Should().BeCloseTo(TimeSpan.FromDays(90), TimeSpan.FromSeconds(1));
        appeal.RulePackVersion.Should().StartWith("tx-medicaid:");

        var extended = await ReadAppealAsync(await ExtendAsync(client, appeal.Id));
        extended.DeadlineExtension!.ExtensionDays.Should().Be(14);
        extended.DeadlineExtension.RegulatoryBasis.Should().Contain("438.408");
    }

    [Fact]
    public async Task No_state_keeps_the_federal_grievance_clock()
    {
        _factory.Reset();
        var appeal = await CreateSubmittedAsync(NewClient(), Grievance(null));

        Window(appeal).Should().BeCloseTo(TimeSpan.FromDays(90), TimeSpan.FromSeconds(1));
        appeal.State.Should().BeNull();
        appeal.RulePackVersion.Should().BeNull();
    }

    [Fact]
    public async Task Tenant_default_state_applies_when_the_request_names_none()
    {
        _factory.Reset();
        var appeal = await CreateSubmittedAsync(NewClient(CaliforniaTenant, californiaDefault: true), Grievance(null));

        Window(appeal).Should().BeCloseTo(TimeSpan.FromDays(30), TimeSpan.FromSeconds(1));
        appeal.State.Should().Be("CA");
        appeal.StateSource.Should().Be(AppealStateSources.TenantDefault);
    }

    // ── State from the member's program ─────────────────────────────────

    private static ProgramDescriptor Program(string state, string code, string lob = LinesOfBusiness.Medicaid) => new()
    {
        Id = $"tenant-state|CARRIER|{code}",
        Code = code,
        Name = code,
        CarrierId = "CARRIER",
        LineOfBusiness = lob,
        StateCode = state
    };

    [Fact]
    public async Task The_members_program_sets_the_state()
    {
        _factory.Reset();
        _factory.MemberPrograms.Add("M-0001", Program("CA", "MEDI-CAL"));

        var appeal = await CreateSubmittedAsync(NewClient(), Grievance(null));

        Window(appeal).Should().BeCloseTo(TimeSpan.FromDays(30), TimeSpan.FromSeconds(1));
        appeal.State.Should().Be("CA");
        appeal.StateSource.Should().Be(AppealStateSources.Program);
        appeal.ProgramId.Should().Be("tenant-state|CARRIER|MEDI-CAL");
    }

    [Fact]
    public async Task The_members_program_wins_over_the_tenant_default()
    {
        _factory.Reset();
        _factory.MemberPrograms.Add("M-0001", Program("TX", "STAR"));

        var appeal = await CreateSubmittedAsync(NewClient(CaliforniaTenant, californiaDefault: true), Grievance(null));

        appeal.State.Should().Be("TX");
        Window(appeal).Should().BeCloseTo(TimeSpan.FromDays(90), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_request_state_that_disagrees_with_the_program_is_refused()
    {
        _factory.Reset();
        _factory.MemberPrograms.Add("M-0001", Program("TX", "STAR"));

        var response = await NewClient().PostAsJsonAsync("/api/appeals", Grievance("CA"), JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("STAR").And.Contain("TX");
    }

    [Fact]
    public async Task A_program_of_another_line_of_business_is_not_used()
    {
        // A dual-eligible member's Medicare program says nothing about a Medicaid grievance.
        _factory.Reset();
        _factory.MemberPrograms.Add("M-0001", Program("TX", "MAPD", LinesOfBusiness.Medicare));

        var appeal = await CreateSubmittedAsync(NewClient(), Grievance("CA"));

        appeal.State.Should().Be("CA");
        appeal.StateSource.Should().Be(AppealStateSources.Request);
        appeal.ProgramId.Should().BeNull();
    }

    [Fact]
    public async Task An_unreachable_lookup_falls_back_instead_of_refusing_intake()
    {
        _factory.Reset();
        _factory.MemberPrograms.Unavailable = true;

        var appeal = await CreateSubmittedAsync(NewClient(CaliforniaTenant, californiaDefault: true), Grievance(null));

        appeal.State.Should().Be("CA");
        appeal.StateSource.Should().Be(AppealStateSources.TenantDefault);
    }

    [Fact]
    public async Task Malformed_state_is_rejected()
    {
        _factory.Reset();
        var response = await NewClient().PostAsJsonAsync("/api/appeals", Grievance("C1"), JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
