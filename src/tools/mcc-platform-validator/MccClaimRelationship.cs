using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

/// <summary>
/// The patient's relationship to the subscriber as claims-service reads it:
/// the X12 837 2000B SBR02 / 2000C PAT01 code ("18" self, "01" spouse,
/// "19" child), never the generator's display word ("Self", "Spouse").
/// </summary>
public static class MccClaimRelationship
{
    public const string SelfCode = "18";

    /// <summary>The code for <paramref name="member"/>. The display word wins:
    /// scenarios set it (a COB birthday-rule child) without always resetting
    /// the generated code.</summary>
    public static string X12Code(SyntheticMember member)
        => member.Relationship?.Trim().ToLowerInvariant() switch
        {
            "self" or "subscriber" => SelfCode,
            "spouse" => "01",
            "child" => "19",
            null or "" => string.IsNullOrWhiteSpace(member.RelationshipCode) ? SelfCode : member.RelationshipCode.Trim(),
            _ => "G8", // other relationship
        };
}
