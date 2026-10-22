using CloudHealthOffice.Infrastructure.Security;

namespace EligibilityService.Services;

public static class TemporalAccumulatorRegistration
{
    public static IServiceCollection AddTemporalAccumulatorSource(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        var source = configuration["TemporalEligibility:AccumulatorSource"]
            ?? (environment.IsDevelopment() || environment.IsEnvironment("Testing") ? "Stub" : "Unavailable");
        switch (source.Trim().ToLowerInvariant())
        {
            case "unavailable":
                services.AddSingleton<IAccumulatorClient, UnavailableAccumulatorClient>();
                break;
            case "stub":
                ChoSyntheticData.EnsureAllowed(environment, configuration, "TemporalEligibility:AccumulatorSource=Stub");
                services.AddSingleton<IAccumulatorClient, StubAccumulatorClient>();
                break;
            default:
                throw new InvalidOperationException("TemporalEligibility:AccumulatorSource must be Unavailable or Stub. No historical HTTP source is implemented.");
        }
        return services;
    }
}
