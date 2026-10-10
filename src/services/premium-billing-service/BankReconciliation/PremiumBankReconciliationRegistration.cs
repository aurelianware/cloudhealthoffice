using CloudHealthOffice.BankReconciliation;
using CloudHealthOffice.Infrastructure.Extensions;

namespace PremiumBillingService.BankReconciliation;

public static class PremiumBankReconciliationRegistration
{
    /// <summary>BAI2 ingestion and premium cash reconciliation (docs/operations/BAI2-BANK-RECONCILIATION-RUNBOOK.md).</summary>
    public static IServiceCollection AddPremiumBankReconciliation(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment, ChoDatabaseProvider? databaseProvider)
    {
        services.AddChoBankReconciliation(configuration, environment, "premium-billing-service", databaseProvider);
        services.Configure<PremiumBankReconciliationOptions>(configuration.GetSection(PremiumBankReconciliationOptions.SectionName));
        services.AddScoped<PremiumBankTransactionMatcher>();
        return services;
    }
}
