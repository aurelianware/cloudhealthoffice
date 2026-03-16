using CloudHealthOffice.ClaimsScrubEngine.Data;
using CloudHealthOffice.ClaimsScrubEngine.Models;
using CloudHealthOffice.ClaimsScrubEngine.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.ClaimsScrubEngine.Configuration;

/// <summary>
/// DI registration for the Claims Scrub Engine.
///
/// Usage in Program.cs:
///
///   builder.Services.AddClaimsScrubEngine();                   // default rules
///   builder.Services.AddClaimsScrubEngine(customRuleSet);      // custom config
/// </summary>
public static class ClaimsScrubEngineServiceCollectionExtensions
{
    /// <summary>
    /// Register the Claims Scrub Engine with default standard rules.
    /// </summary>
    public static IServiceCollection AddClaimsScrubEngine(this IServiceCollection services)
    {
        return services.AddClaimsScrubEngine(DefaultStandardRules.Create());
    }

    /// <summary>
    /// Register the Claims Scrub Engine with a custom rule set.
    /// </summary>
    public static IServiceCollection AddClaimsScrubEngine(
        this IServiceCollection services,
        StandardRuleSet standardRules)
    {
        services.AddSingleton(standardRules);
        services.AddScoped<IValidationRuleEngine, ValidationRuleEngine>();
        services.AddScoped<IClaimRoutingService, ClaimRoutingService>();
        return services;
    }
}
