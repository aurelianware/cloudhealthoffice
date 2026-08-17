using CloudHealthOffice.ReferenceData.Domain;

namespace CloudHealthOffice.ReferenceData.Security;

public sealed record ReferenceDataAccessContext(
    bool IsAuthenticated,
    string? TenantId = null,
    bool IsInternal = false);

public static class ReferenceDataExposurePolicy
{
    public static bool CanRead(ReferenceCode code, ReferenceDataAccessContext context) =>
        code.ExposureClassification switch
        {
            ExposureClassification.PublicReference =>
                code.LicenseClassification == LicenseClassification.Public,
            ExposureClassification.AuthenticatedReference => context.IsAuthenticated,
            ExposureClassification.TenantRestricted =>
                context.IsAuthenticated && code.TenantId is not null && code.TenantId == context.TenantId,
            ExposureClassification.InternalOnly => context.IsInternal,
            _ => false
        };

    /// <summary>Licensed or restricted descriptions never cross an unauthorized boundary.</summary>
    public static ReferenceCode Redact(ReferenceCode code, ReferenceDataAccessContext context) =>
        CanRead(code, context)
            ? code
            : code with { Coding = code.Coding with { Display = null }, Description = null };
}
