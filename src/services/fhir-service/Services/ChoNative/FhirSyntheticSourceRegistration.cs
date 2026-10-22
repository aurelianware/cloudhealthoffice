using CloudHealthOffice.Infrastructure.Security;
using FhirService.Models;

namespace FhirService.Services.ChoNative;

/// <summary>Guard the sources actually selected, not an unused legacy provider.</summary>
public static class FhirSyntheticSourceRegistration
{
    public static IServiceCollection AddFhirSyntheticSources(this IServiceCollection services,
        FhirDataBindings bindings, IConfiguration configuration, IHostEnvironment environment)
    {
        var mocks = new[] { "Patient", "Coverage", "Encounter", "Claim", "PayerToPayer" }
            .Where(resource => bindings.SourceOf(resource) == FhirAdapterSources.Mock).ToArray();
        if (mocks.Length > 0)
            ChoSyntheticData.EnsureAllowed(environment, configuration,
                "FHIR mock sources: " + string.Join(", ", mocks));

        // RoutingFhirDataAdapter takes this as an inactive dependency on the
        // all-native path. No native method falls back to it on failure.
        services.AddSingleton<MockFhirDataAdapter>();
        if (!bindings.PayerToPayerNative)
        {
            services.AddSingleton<MockPatientAccessDataProvider>();
            services.AddSingleton<IPatientAccessDataProvider>(sp => sp.GetRequiredService<MockPatientAccessDataProvider>());
            services.AddSingleton<IChoMemberDirectory>(sp => sp.GetRequiredService<MockPatientAccessDataProvider>());
        }
        return services;
    }
}
