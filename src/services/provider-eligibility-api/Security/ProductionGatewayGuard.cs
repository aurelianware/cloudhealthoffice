using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Mock;

namespace ProviderEligibilityApi.Security;

/// <summary>
/// Refuses to start outside Development when the Mock gateway would answer
/// eligibility checks. The mock returns plausible coverage, so a practice
/// would be told a real patient is covered on the strength of made-up data.
/// An unset <c>DefaultGateway</c> counts as Mock because that is its default.
/// </summary>
public static class ProductionGatewayGuard
{
    public static void EnsureNotMock(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment()) return;

        var configured = configuration[$"{HealthcareTransactionOptions.SectionName}:DefaultGateway"];
        var gateway = string.IsNullOrWhiteSpace(configured)
            ? new HealthcareTransactionOptions().DefaultGateway
            : configured.Trim();

        if (string.Equals(gateway, MockHealthcareGateway.GatewayName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"HealthcareTransactions:DefaultGateway resolves to '{MockHealthcareGateway.GatewayName}' in the " +
                $"'{environment.EnvironmentName}' environment. The mock gateway returns made-up coverage and is " +
                "allowed only in Development; configure a real gateway (Stedi).");
        }
    }
}
