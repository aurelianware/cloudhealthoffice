using CloudHealthOffice.Infrastructure.Security;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClaimsService.Adapters;
using ClaimsService.Models;
using ClaimsService.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Infrastructure.Messaging;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests;

/// <summary>
/// End to end through each HTTP intake path: whatever receipt date the
/// submitter sends (backdated or in the future), the claim that reaches the
/// adapter (and so the store) carries the server's clock at intake. Runs the
/// real <see cref="ClaimSubmissionService"/> behind the real controllers with
/// a pinned <see cref="TimeProvider"/>.
/// </summary>
public class ClaimIntakeReceiptDateTests : IClassFixture<ClaimIntakeReceiptDateTests.ReceiptStampingFactory>
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 15, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateTime Backdated = new(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Future = new(2026, 11, 28, 0, 0, 0, DateTimeKind.Utc);

    public sealed class ReceiptStampingFactory : ClaimsApiFactory
    {
        public ConcurrentQueue<AdapterClaim> Persisted { get; } = new();

        public ReceiptStampingFactory()
        {
            ClaimAdapter
                .SubmitClaimAsync(Arg.Any<ClaimSubmissionAdapterRequest>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var claim = ci.Arg<ClaimSubmissionAdapterRequest>().Claim;
                    claim.Id = Guid.NewGuid().ToString();
                    claim.ClaimVersionId = claim.Id;
                    claim.VersionNumber = 1;
                    Persisted.Enqueue(claim);
                    return new ClaimAdapterResponse { Platform = "cho", Claim = claim };
                });
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                // The pinned clock goes to the submission service only, not
                // into DI, so token issuance and the rest of the host keep
                // real time.
                services.RemoveAll<IClaimSubmissionService>();
                services.AddScoped<IClaimSubmissionService>(sp => new ClaimSubmissionService(
                    sp.GetRequiredService<ClaimAdapterFactory>(),
                    sp.GetRequiredService<IClaimVersionEventPublisher>(),
                    sp.GetRequiredService<IMessageBus>(),
                    sp.GetRequiredService<ILogger<ClaimSubmissionService>>(),
                    new PinnedClock(Now)));
            });
        }
    }

    private readonly ReceiptStampingFactory _factory;
    private readonly HttpClient _client;

    public ClaimIntakeReceiptDateTests(ReceiptStampingFactory factory)
    {
        _factory = factory;
        _factory.Persisted.Clear();
        _client = factory.CreateDefaultClient(new ChoDevelopmentTokenHandler());
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
    }

    [Fact]
    public async Task V1Submit_IgnoresClientReceiptDates()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/claims", new
        {
            claimNumber = "CLM-V1-RCPT",
            memberId = "MBR-1",
            billingProviderNPI = "1234567893",
            claimType = ClaimType.Professional,
            serviceDateFrom = "2026-09-01",
            serviceDateTo = "2026-09-01",
            submittedDate = Future,
            receivedDate = Backdated,
            claimLines = new[] { new { lineNumber = 1, procedureCode = "99213", chargeAmount = 150m, units = 1 } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        AssertStampedAtIntake(Assert.Single(_factory.Persisted));
        var body = await response.Content.ReadFromJsonAsync<AdapterClaim>();
        Assert.Equal(Now.UtcDateTime, body!.SubmittedDate.ToUniversalTime());
    }

    [Fact]
    public async Task LegacySubmit_IgnoresClientReceiptDates()
    {
        var serviceDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var response = await _client.PostAsJsonAsync("/api/claims", new Claim
        {
            TenantId = "test-tenant",
            ClaimNumber = "CLM-LEGACY-RCPT",
            MemberId = "MBR-1",
            BillingProviderNPI = "1234567893",
            LineOfBusiness = LineOfBusiness.Commercial,
            ClaimType = ClaimType.Professional,
            PlaceOfServiceCode = "11",
            ServiceDateFrom = serviceDate,
            ServiceDateTo = serviceDate,
            SubmittedDate = Backdated,
            ReceivedDate = Future,
            ClaimLines = new List<ClaimLine>
            {
                new()
                {
                    LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 150m, Units = 1,
                    ServiceDateFrom = serviceDate, ServiceDateTo = serviceDate,
                },
            },
        });

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        AssertStampedAtIntake(Assert.Single(_factory.Persisted));
    }

    [Fact]
    public async Task Raw837Import_StampsReceiptDate_AndKeepsBht04AsSubmitterTransactionDate()
    {
        var body = EDI.Snip.Snip837Samples.ProfessionalBody("CLM-837-RCPT");
        EDI.Snip.Snip837Samples.Replace(body, "NM1*IL", "NM1*IL*1*TESTPATIENT*ALEX****MI*MEM-RAW837");
        var edi = EDI.Snip.Snip837Samples.Wrap(EDI.Snip.Snip837Samples.ProfessionalVersion, body);
        Assert.Contains("BHT*0019*00*BATCH0001*20260115*", edi);

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(edi));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", "receipt.837");

        var response = await _client.PostAsync("/api/v1/claims/import/raw837", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = Assert.Single(_factory.Persisted);
        AssertStampedAtIntake(persisted);
        Assert.Equal(new DateTime(2026, 1, 15), persisted.SubmitterTransactionDate);
    }

    private static void AssertStampedAtIntake(AdapterClaim persisted)
    {
        Assert.Equal(Now.UtcDateTime, persisted.SubmittedDate);
        Assert.Equal(Now.UtcDateTime, persisted.ReceivedDate);
    }

    private sealed class PinnedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
