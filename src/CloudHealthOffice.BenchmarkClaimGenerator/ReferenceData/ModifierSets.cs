namespace CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

/// <summary>
/// Modifier combinations organized by usage scenario.
/// </summary>
internal static class ModifierSets
{
    /// <summary>Multi-procedure modifier stacking combinations.</summary>
    internal static readonly string[][] MultiProcedure =
    {
        new[] { "25" },             // Significant, separately identifiable E/M
        new[] { "59" },             // Distinct procedural service
        new[] { "76" },             // Repeat procedure by same physician
        new[] { "77" },             // Repeat procedure by another physician
        new[] { "25", "59" },       // E/M + distinct procedure
        new[] { "RT" },             // Right side
        new[] { "LT" },             // Left side
        new[] { "59", "RT" },       // Distinct procedure, right side
        new[] { "59", "LT" }        // Distinct procedure, left side
    };

    /// <summary>Bilateral procedure modifiers.</summary>
    internal static readonly string[][] Bilateral =
    {
        new[] { "50" },             // Bilateral procedure
        new[] { "RT" },             // Right side only (for separate billing)
        new[] { "LT" }              // Left side only (for separate billing)
    };

    /// <summary>Assistant surgeon modifiers.</summary>
    internal static readonly string[][] AssistantSurgeon =
    {
        new[] { "80" },             // Assistant surgeon
        new[] { "82" }              // Assistant surgeon (when qualified resident not available)
    };

    /// <summary>Telemedicine modifiers.</summary>
    internal static readonly string[][] Telemedicine =
    {
        new[] { "95" },             // Synchronous telemedicine
        new[] { "GT" }              // Via interactive audio and video
    };

    /// <summary>Global surgery period modifiers.</summary>
    internal static readonly string[][] GlobalSurgery =
    {
        new[] { "54" },             // Surgical care only
        new[] { "55" },             // Postoperative management only
        new[] { "56" },             // Preoperative management only
        new[] { "78" },             // Unplanned return to OR during postop
        new[] { "79" }              // Unrelated procedure during postop
    };
}
