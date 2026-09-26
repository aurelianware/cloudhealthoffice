using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using FluentAssertions;
using NSubstitute;

namespace CloudHealthOffice.ProviderEligibilityApi.Tests;

/// <summary>
/// The payer directory is loaded into memory by the Stedi sync. Until it has
/// loaded, the replica is not ready and refuses work with a retryable 503 —
/// never a 422 "payer not found" that tells the caller its request is wrong.
/// </summary>
public sealed class PayerDirectoryReadinessTests
{
    private static readonly Dictionary<string, string?> SyncEnabled = new()
    {
        ["PayerReference:Sync:Enabled"] = "true",
        // The shared startup sync is not under test here.
        ["PayerReference:Sync:OnStartup"] = "false"
    };

    [Fact]
    public async Task Not_ready_until_the_directory_has_loaded()
    {
        using var factory = Factory(loaded: false);

        var response = await factory.CreateClient().GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Ready_once_the_directory_has_loaded()
    {
        using var factory = Factory(loaded: true);

        var response = await factory.CreateClient().GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Liveness_does_not_wait_for_the_directory()
    {
        using var factory = Factory(loaded: false);

        var response = await factory.CreateClient().GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Eligibility_check_while_loading_is_a_retryable_503_and_spends_no_transaction()
    {
        using var factory = Factory(loaded: false);

        var response = await factory.CreateAuthorizedClient()
            .PostAsJsonAsync("/api/v1/eligibility/check", EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.Should().NotBeNull();
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("outcome").GetString().Should().Be("Failed");
        body.GetProperty("errorCategory").GetString().Should().Be("ReferenceDataUnavailable");
        body.GetProperty("eligible").GetBoolean().Should().BeFalse();
        body.GetProperty("correlationId").GetString().Should().Be("booking-42");
        await factory.Gateway.DidNotReceiveWithAnyArgs().CheckEligibilityAsync(default!, default);
    }

    [Fact]
    public async Task Payer_search_while_loading_is_a_retryable_503()
    {
        using var factory = Factory(loaded: false);

        var response = await factory.CreateAuthorizedClient().GetAsync("/api/v1/payers?q=delta");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.Should().NotBeNull();
        await factory.Payers.DidNotReceiveWithAnyArgs().SearchAsync(default!, default);
    }

    [Fact]
    public async Task Eligibility_check_proceeds_once_loaded()
    {
        using var factory = Factory(loaded: true);
        factory.Gateway.CheckEligibilityAsync(Arg.Any<GatewayEligibilityRequest>(), Arg.Any<CancellationToken>())
            .Returns(EligibilityTestData.ActiveCoverage());

        var response = await factory.CreateAuthorizedClient()
            .PostAsJsonAsync("/api/v1/eligibility/check", EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Becomes_ready_when_a_later_sync_succeeds()
    {
        var synchronizer = Synchronizer(loaded: false);
        using var factory = new ProviderEligibilityApiFactory(settings: SyncEnabled) { Synchronizer = synchronizer };
        var client = factory.CreateClient();

        (await client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        synchronizer.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(Loaded());

        (await client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static ProviderEligibilityApiFactory Factory(bool loaded) =>
        new(settings: SyncEnabled) { Synchronizer = Synchronizer(loaded) };

    private static IPayerDirectorySynchronizer Synchronizer(bool loaded)
    {
        var synchronizer = Substitute.For<IPayerDirectorySynchronizer>();
        synchronizer.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(loaded ? Loaded() : new PayerDirectorySyncStatus
            {
                Source = "stedi",
                LastAttemptedAt = DateTimeOffset.UtcNow,
                LastSucceeded = false,
                LastError = "directory unavailable"
            });
        return synchronizer;
    }

    private static PayerDirectorySyncStatus Loaded() => new()
    {
        Source = "stedi",
        LastAttemptedAt = DateTimeOffset.UtcNow,
        LastSucceededAt = DateTimeOffset.UtcNow,
        LastSucceeded = true,
        LastReceived = 1200
    };
}
