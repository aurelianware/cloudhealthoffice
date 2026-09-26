using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace CloudHealthOffice.ProviderEligibilityApi.Tests;

/// <summary>
/// Boots the real registrations in the Production environment: no database,
/// no message bus, Stedi selected. Proves the host starts and fails closed
/// when the Stedi credential is missing instead of answering from Mock.
/// </summary>
public sealed class ProductionStartupTests
{
    [Fact]
    public async Task Starts_in_production_without_a_database()
    {
        using var factory = new ProviderEligibilityApiFactory(useRealGateway: true);

        var response = await factory.CreateClient().GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Missing_stedi_credential_fails_closed()
    {
        using var factory = new ProviderEligibilityApiFactory(useRealGateway: true);

        var response = await factory.CreateAuthorizedClient()
            .PostAsJsonAsync("/api/v1/eligibility/check", EligibilityTestData.SelfRequest());

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("outcome").GetString().Should().Be("Failed");
        body.GetProperty("errorCategory").GetString().Should().Be("Configuration");
        body.GetProperty("eligible").GetBoolean().Should().BeFalse();
    }
}
