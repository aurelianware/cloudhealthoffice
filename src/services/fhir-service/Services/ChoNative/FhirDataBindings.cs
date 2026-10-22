using FhirService.Models;

namespace FhirService.Services.ChoNative;

/// <summary>
/// The resolved source of each <see cref="IFhirDataAdapter"/> resource (ADR 018),
/// computed once from <see cref="FhirAdapterOptions.Sources"/>. The adapter
/// router and the adapter-status report both read this, so the report cannot
/// describe a binding the router does not use.
/// </summary>
public sealed record FhirDataBindings(string Patient, string Coverage, string Encounter, string Claim,
    string PayerToPayer = FhirAdapterSources.Mock)
{
    /// <summary>Resources that can be served by CHO's own services today.</summary>
    public static readonly IReadOnlySet<string> NativeCapable =
        new HashSet<string>(["Patient", "Coverage", "Encounter", "Claim", "PayerToPayer"], StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Known = ["Patient", "Coverage", "Encounter", "Claim", "PayerToPayer"];

    public bool AnyNative =>
        Patient == FhirAdapterSources.ChoNative
        || Coverage == FhirAdapterSources.ChoNative
        || Encounter == FhirAdapterSources.ChoNative
        || Claim == FhirAdapterSources.ChoNative
        || PayerToPayerNative;

    /// <summary>Payer-to-Payer members, coverage and claims come from CHO's own services (ADR 020).</summary>
    public bool PayerToPayerNative => PayerToPayer == FhirAdapterSources.ChoNative;

    /// <summary>The source bound to <paramref name="resource"/>, or null when it is not a data-adapter resource.</summary>
    public string? SourceOf(string resource) => resource switch
    {
        "Patient" => Patient,
        "Coverage" => Coverage,
        "Encounter" => Encounter,
        "Claim" => Claim,
        "PayerToPayer" => PayerToPayer,
        _ => null,
    };

    /// <summary>
    /// Resolves the bindings, refusing configuration that would serve something
    /// other than what it says: an unknown resource key, an unknown source, or
    /// ChoNative on a resource with no CHO-native implementation.
    /// </summary>
    /// <exception cref="InvalidOperationException">The configuration is invalid.</exception>
    public static FhirDataBindings From(FhirAdapterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        foreach (var key in options.Sources.Keys)
        {
            if (!Known.Contains(key, StringComparer.OrdinalIgnoreCase))
                errors.Add($"FhirAdapters:Sources:{key} is not a FHIR data resource (expected one of {string.Join(", ", Known)}).");
        }

        string Resolve(string resource)
        {
            if (!options.Sources.TryGetValue(resource, out var raw) || string.IsNullOrWhiteSpace(raw))
                return FhirAdapterSources.Mock;

            var value = raw.Trim();
            if (string.Equals(value, FhirAdapterSources.Mock, StringComparison.OrdinalIgnoreCase))
                return FhirAdapterSources.Mock;
            if (string.Equals(value, FhirAdapterSources.ChoNative, StringComparison.OrdinalIgnoreCase))
            {
                if (NativeCapable.Contains(resource))
                    return FhirAdapterSources.ChoNative;
                errors.Add($"FhirAdapters:Sources:{resource}=ChoNative is not available: {resource} has no CHO-native source yet.");
                return FhirAdapterSources.Mock;
            }

            errors.Add($"FhirAdapters:Sources:{resource}='{value}' is not a known source (expected Mock or ChoNative).");
            return FhirAdapterSources.Mock;
        }

        var bindings = new FhirDataBindings(Resolve("Patient"), Resolve("Coverage"), Resolve("Encounter"), Resolve("Claim"), Resolve("PayerToPayer"));
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid FHIR adapter sources: " + string.Join(" ", errors));
        return bindings;
    }
}
