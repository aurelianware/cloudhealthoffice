using EnrollmentImportService.Models;

namespace EnrollmentImportService.Services;

/// <summary>
/// Derives <see cref="EnrollmentEventType"/> from an 834 <see cref="MemberEnrollment"/>
/// segment. Pulled out of the import service so it can be unit-tested without dragging in
/// the full repository/publisher graph — and so the call site doesn't collide with the
/// name-shadowing between the root namespace and the production import-service class.
/// </summary>
public static class EnrollmentEventClassifier
{
    public static EnrollmentEventType Classify(MemberEnrollment e)
    {
        // Order matters: most specific reasons first.
        if (e.MaintenanceType == "024")
        {
            return e.BenefitStatus == "C" || e.MaintenanceReason == "EC"
                ? EnrollmentEventType.CobraTerminated
                : EnrollmentEventType.Terminated;
        }

        if (e.MaintenanceType == "025")
            return EnrollmentEventType.ReinstatementApproved;

        if (e.BenefitStatus == "C" && e.MaintenanceType == "021")
            return EnrollmentEventType.CobraElected;

        if (e.MaintenanceReason is "EC" or "37")
            return EnrollmentEventType.SepTriggered;

        if (e.MaintenanceType == "021")
            return EnrollmentEventType.Enrolled;

        // Fallback for all other maintenance types (commonly 001 = Change).
        // AddressChanged wins when a demographics delta is present; any other change
        // (plan, dates, group info) is surfaced as PlanChanged. The schema keeps the
        // enum stable so callers can widen later without breaking stored events.
        if (HasAddressChange(e)) return EnrollmentEventType.AddressChanged;
        return EnrollmentEventType.PlanChanged;
    }

    private static bool HasAddressChange(MemberEnrollment e) =>
        e.Demographics != null
        && (!string.IsNullOrWhiteSpace(e.Demographics.Address1)
            || !string.IsNullOrWhiteSpace(e.Demographics.City)
            || !string.IsNullOrWhiteSpace(e.Demographics.State)
            || !string.IsNullOrWhiteSpace(e.Demographics.Zip));
}
