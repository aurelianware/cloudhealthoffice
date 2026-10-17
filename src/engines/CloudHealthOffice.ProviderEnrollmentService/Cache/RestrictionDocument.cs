using System.Text.Json.Serialization;
using CloudHealthOffice.ProviderEnrollmentService.Models;

namespace CloudHealthOffice.ProviderEnrollmentService.Cache;

/// <summary>An <see cref="EnrollmentRestriction"/> as stored in the enrollment cache.</summary>
public sealed class RestrictionDocument
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;
    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;
    [JsonPropertyName("effectiveDate")]
    public string? EffectiveDate { get; init; }
    [JsonPropertyName("liftDate")]
    public string? LiftDate { get; init; }

    public static RestrictionDocument From(EnrollmentRestriction r) => new()
    {
        Type         = r.Type.ToString(),
        Description  = r.Description,
        EffectiveDate = r.EffectiveDate?.ToString("O"),
        LiftDate      = r.LiftDate?.ToString("O")
    };

    public EnrollmentRestriction ToRestriction() => new()
    {
        Type         = Enum.Parse<RestrictionType>(Type),
        Description  = Description,
        EffectiveDate = EffectiveDate is null ? null : DateOnly.Parse(EffectiveDate),
        LiftDate      = LiftDate is null ? null : DateOnly.Parse(LiftDate)
    };
}
