using System.Net;
using System.Text;
using ClaimsService.Models;
using ClaimsService.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CloudHealthOffice.ClaimsService.Tests.Services;

/// <summary>
/// The MPIP-enhanced allowed amount is money and is rounded to the cent half
/// away from zero (MoneyRounding.ToCents), not with banker's rounding.
/// </summary>
public class MpipAdjudicationEnhancerRoundingTests
{
    [Fact]
    public async Task EnhancedAllowed_AtHalfCent_RoundsAwayFromZero()
    {
        var rates = Substitute.For<IMpipRateClient>();
        rates.GetMultiplierAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(1.063m);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient("ReferenceDataService").Returns(new HttpClient(new MpipEnabledHandler())
        {
            BaseAddress = new Uri("http://reference-data"),
        });
        var enhancer = new MpipAdjudicationEnhancer(rates, http, NullLogger<MpipAdjudicationEnhancer>.Instance);

        // $15.00 × 1.063 = $15.945 → $15.95 (banker's: $15.94)
        var claim = new Claim
        {
            TenantId = "tenant-fl",
            ClaimNumber = "CLM-MPIP-1",
            BillingProviderNPI = "1234567890",
            ServiceDateFrom = new DateTime(2026, 3, 8),
            ClaimLines =
            [
                new ClaimLine
                {
                    LineNumber = 1,
                    AdjudicationResult = new LineAdjudicationResult { AllowedAmount = 15.00m, PaidAmount = 15.00m },
                },
            ],
        };

        await enhancer.ApplyMpipEnhancementAsync(claim, memberAgeAtServiceDate: 10);

        var line = claim.ClaimLines.Single().AdjudicationResult!;
        line.AllowedAmount.Should().Be(15.95m);
        line.PaidAmount.Should().Be(15.95m);
    }

    private sealed class MpipEnabledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"mpipEnabled\":true}", Encoding.UTF8, "application/json"),
            });
    }
}
