namespace FhirService.Models;

/// <summary>
/// Explicit Demo / Hybrid / Live labeling for FHIR resource adapters.
/// Bound from the <c>FhirAdapters</c> configuration section. Defaults are
/// Demo + synthetic so a deployment can never silently look live.
/// </summary>
public sealed class FhirAdapterOptions
{
    public const string SectionName = "FhirAdapters";

    /// <summary>Configured overall mode: Demo, Hybrid, or Live.</summary>
    public string Mode { get; set; } = FhirAdapterModes.Demo;

    /// <summary>synthetic, de-identified, limited-phi, or production-phi.</summary>
    public string DataClassification { get; set; } = FhirAdapterDataClasses.Synthetic;

    /// <summary>Tenant this adapter map describes. Demo default is demo-tenant.</summary>
    public string TenantId { get; set; } = "demo-tenant";

    /// <summary>
    /// Per-resource mode overrides. Keys are resource or capability names
    /// (Patient, Coverage, Appeal, PriorAuthorization, PayerToPayer, …).
    /// Values are Demo, Hybrid, Live, or OutOfScope.
    /// </summary>
    public Dictionary<string, string> Resources { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public static class FhirAdapterModes
{
    public const string Demo = "Demo";
    public const string Hybrid = "Hybrid";
    public const string Live = "Live";
    public const string OutOfScope = "OutOfScope";
}

public static class FhirAdapterDataClasses
{
    public const string Synthetic = "synthetic";
    public const string DeIdentified = "de-identified";
    public const string LimitedPhi = "limited-phi";
    public const string ProductionPhi = "production-phi";
}
