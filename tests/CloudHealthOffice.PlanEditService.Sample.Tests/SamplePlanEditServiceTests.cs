using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaimsService.Models;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.PlanEdits;
using ClaimsService.Services.Adjudication.Stages;
using CloudHealthOffice.Infrastructure.Http;
using CloudHealthOffice.PlanEdits.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.PlanEditService.Sample.Tests;

/// <summary>
/// The sample plan edit service on its own, and end to end with
/// claims-service's real client and PlanEditsStage: signed request in, edits
/// out, the claim pended with decidable plan edits, an upheld denial edit
/// denying its line.
/// </summary>
public class SamplePlanEditServiceTests : IClassFixture<SamplePlanEditServiceTests.SampleFactory>
{
    private const string Secret = "sample-plan-edit-secret-of-32-plus-characters";
    private const string Tenant = "tenant-1";
    private static readonly DateTime Dos = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);

    private readonly SampleFactory _sample;

    public SamplePlanEditServiceTests(SampleFactory sample) => _sample = sample;

    public sealed class SampleFactory : WebApplicationFactory<PlanEditServiceSample.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseSetting("PlanEdits:SigningSecret", Secret)
                   .UseSetting("PlanEdits:HighDollarLineThreshold", "10000")
                   .UseSetting("PlanEdits:NonCoveredProcedureCodes:0", "S9999");
    }

    // ── The sample on its own ─────────────────────────────────────────────

    [Fact]
    public async Task An_unsigned_or_tampered_request_is_refused()
    {
        var client = _sample.CreateClient();
        var body = JsonSerializer.SerializeToUtf8Bytes(Request(), PlanEditJson.Options);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var unsigned = await client.PostAsync(PlanEditServiceSample.SamplePlanEditEndpoint.Route, Content(body));
        using var tampered = new HttpRequestMessage(HttpMethod.Post, PlanEditServiceSample.SamplePlanEditEndpoint.Route) { Content = Content(body) };
        tampered.Headers.Add(PlanEditContract.TimestampHeader, ts.ToString());
        tampered.Headers.Add(PlanEditContract.SignatureHeader, PlanEditSignature.Sign(Secret, ts, Encoding.UTF8.GetBytes("{}")));
        var tamperedResponse = await client.SendAsync(tampered);

        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, tamperedResponse.StatusCode);
    }

    [Fact]
    public async Task A_signed_request_gets_a_response_that_meets_the_contract()
    {
        var client = _sample.CreateClient();
        var body = JsonSerializer.SerializeToUtf8Bytes(Request(), PlanEditJson.Options);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, PlanEditServiceSample.SamplePlanEditEndpoint.Route) { Content = Content(body) };
        request.Headers.Add(PlanEditContract.TimestampHeader, ts.ToString());
        request.Headers.Add(PlanEditContract.SignatureHeader, PlanEditSignature.Sign(Secret, ts, body));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var parsed = await response.Content.ReadFromJsonAsync<PlanEditResponse>(PlanEditJson.Options);
        Assert.Empty(PlanEditResponseValidator.Validate(parsed, [1, 2]));
        Assert.Equal(
            [PlanEditServiceSample.SamplePlanEditRules.NonCoveredRule, PlanEditServiceSample.SamplePlanEditRules.HighDollarRule],
            parsed!.Edits!.Select(e => e.RuleId));
    }

    // ── End to end with claims-service ────────────────────────────────────

    [Fact]
    public async Task Claims_service_pends_the_claim_with_the_plans_edits()
    {
        var stage = new PlanEditsStage(ChoClient(), NullLogger<PlanEditsStage>.Instance);
        var ctx = Context();

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(PlanEditsStage.PlanEditPendCode, ctx.PendDetails!.PendCode);
        var denial = ctx.PendDetails.PlanEditFindings.Single(e => e.RuleId == "SAMPLE-NONCOVERED");
        Assert.Equal([1], denial.LineNumbers);
        Assert.Equal("96", denial.SuggestedCarc);
        var review = ctx.PendDetails.PlanEditFindings.Single(e => e.RuleId == "SAMPLE-HIDOLLAR");
        Assert.Equal([2], review.LineNumbers);
        Assert.False(review.IsDenialEdit);
    }

    [Fact]
    public async Task On_approval_the_upheld_denial_edit_denies_its_line_and_the_claim_moves_on()
    {
        var stage = new PlanEditsStage(ChoClient(), NullLogger<PlanEditsStage>.Instance);
        var first = Context();
        await stage.ExecuteAsync(first, CancellationToken.None);
        var reviewed = first.PendDetails!;
        var approval = new ExaminerApproval
        {
            ExaminerId = "examiner-1",
            ReviewedPend = reviewed,
            EditDispositions = reviewed.PlanEditFindings.Select(e => new EditDisposition
            {
                EditKey = e.EditKey!,
                Decision = EditDecision.Uphold,
                UpheldByDefault = true,
                DecidedBy = "examiner-1",
                DecidedAt = DateTime.UtcNow,
            }).ToList(),
        };

        var rerun = Context(approval);
        var result = await stage.ExecuteAsync(rerun, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        var denial = Assert.Single(EditDispositionApplier.LineDenials(rerun));
        Assert.Equal(1, denial.LineNumber);
        Assert.Equal("96", denial.Carc);
        Assert.Equal("N130", denial.Rarc);
        Assert.Equal("CO", denial.Group);
    }

    [Fact]
    public async Task A_wrong_secret_on_CHOs_side_is_no_usable_answer_and_pends_SVCUNAVAIL()
    {
        var stage = new PlanEditsStage(ChoClient(secret: "a-different-secret-that-is-32-chars-long"), NullLogger<PlanEditsStage>.Instance);
        var ctx = Context();

        var result = await stage.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(result.CheckUnavailable);
        Assert.Equal(ClaimAdjudicationOrchestrator.ServiceUnavailablePendCode, ctx.PendDetails!.PendCode);
        Assert.Contains(PlanEditUnavailableKinds.HttpStatus, ctx.PendDetails.PendReason);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private IPlanEditServiceClient ChoClient(string secret = Secret)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"Adjudication:PlanEdits:Tenants:{Tenant}:Endpoint"] = "https://edits.plan.example" + PlanEditServiceSample.SamplePlanEditEndpoint.Route,
            [$"Adjudication:PlanEdits:Tenants:{Tenant}:SigningSecret"] = secret,
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChoServiceClientResilience();
        services.AddPlanEditService(config, new Env());
        services.AddHttpClient(PlanEditServiceClient.ClientName(Tenant))
            .ConfigurePrimaryHttpMessageHandler(() => _sample.Server.CreateHandler());
        return services.BuildServiceProvider().GetRequiredService<IPlanEditServiceClient>();
    }

    private static ByteArrayContent Content(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new("application/json");
        return content;
    }

    private static PlanEditRequest Request() => new()
    {
        RequestId = "r1",
        TenantId = Tenant,
        Claim = new PlanEditClaim
        {
            ClaimId = "c1",
            ClaimVersionId = "c1",
            MemberId = "M1",
            Lines =
            [
                new PlanEditLine { LineNumber = 1, ProcedureCode = "S9999", Units = 1, Charge = 50m },
                new PlanEditLine { LineNumber = 2, ProcedureCode = "J2357", Units = 1, Charge = 12000m },
            ],
        },
    };

    private static ClaimAdjudicationContext Context(ExaminerApproval? approval = null) => new()
    {
        TenantId = Tenant,
        ClaimVersionId = "v1",
        ExaminerApproval = approval,
        Claim = new AdapterClaim
        {
            TenantId = Tenant,
            Id = "v1",
            ClaimVersionId = "v1",
            ClaimNumber = "CLM-1",
            MemberId = "M1",
            BillingProviderNPI = "1234567893",
            ClaimType = ClaimType.Professional,
            ServiceDateFrom = Dos,
            ServiceDateTo = Dos,
            ClaimLines =
            [
                new() { LineNumber = 1, ProcedureCode = "S9999", Units = 1, ChargeAmount = 50m, ServiceDateFrom = Dos, ServiceDateTo = Dos },
                new() { LineNumber = 2, ProcedureCode = "J2357", Units = 1, ChargeAmount = 12000m, ServiceDateFrom = Dos, ServiceDateTo = Dos },
            ],
        },
    };

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "claims-service";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
