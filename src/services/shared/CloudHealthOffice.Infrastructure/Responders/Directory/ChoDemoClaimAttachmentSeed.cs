namespace CloudHealthOffice.Infrastructure.Responders.Directory;

/// <summary>
/// Synthetic payer-side claims for inbound 275 development and tests.
/// Identifiers are invented; no real PHI.
/// </summary>
public static class ChoDemoClaimAttachmentSeed
{
    public const string ClaimId = "CLM-DEMO-275-001";
    public const string PayerClaimControlNumber = "PCCN-DEMO-275-001";
    public const string PatientControlNumber = "PCN-DEMO-275-001";
    public const string AttachmentControlNumber = "ACN-DEMO-275-001";
    public const string AmbiguousPatientControlNumber = "PCN-AMBIGUOUS";
    public const string OtherTenantClaimId = "CLM-OTHER-275-001";
    public const string SecondClaimId = "CLM-DEMO-275-002";

    public static IReadOnlyList<PayerDirectoryClaim> Claims { get; } = new[]
    {
        new PayerDirectoryClaim
        {
            TenantId = ChoDemoEligibilitySeed.TenantId,
            ClaimId = ClaimId,
            CanonicalPayerId = ChoDemoEligibilitySeed.CanonicalPayerId,
            PayerClaimControlNumber = PayerClaimControlNumber,
            PatientControlNumber = PatientControlNumber,
            AttachmentControlNumber = AttachmentControlNumber,
            Status = PayerDirectoryClaimStatus.Pended,
            ServiceLines =
            {
                new PayerDirectoryClaimLine { LineNumber = 1, LineControlNumber = "LINE-1", ProcedureCode = "D0330", ToothNumber = "14" },
                new PayerDirectoryClaimLine { LineNumber = 2, LineControlNumber = "LINE-2", ProcedureCode = "D2740", ToothNumber = "19" }
            }
        },
        new PayerDirectoryClaim
        {
            TenantId = ChoDemoEligibilitySeed.TenantId,
            ClaimId = SecondClaimId,
            CanonicalPayerId = ChoDemoEligibilitySeed.CanonicalPayerId,
            PayerClaimControlNumber = "PCCN-DEMO-275-002",
            PatientControlNumber = AmbiguousPatientControlNumber,
            Status = PayerDirectoryClaimStatus.Pended,
            ServiceLines = { new PayerDirectoryClaimLine { LineNumber = 1, LineControlNumber = "LINE-1" } }
        },
        new PayerDirectoryClaim
        {
            TenantId = ChoDemoEligibilitySeed.TenantId,
            ClaimId = "CLM-DEMO-275-003",
            CanonicalPayerId = ChoDemoEligibilitySeed.CanonicalPayerId,
            PatientControlNumber = AmbiguousPatientControlNumber,
            Status = PayerDirectoryClaimStatus.InAdjudication,
            ServiceLines = { new PayerDirectoryClaimLine { LineNumber = 1 } }
        },
        new PayerDirectoryClaim
        {
            TenantId = ChoDemoEligibilitySeed.OtherTenantId,
            ClaimId = OtherTenantClaimId,
            CanonicalPayerId = "CHO-OTHER-HEALTH",
            PayerClaimControlNumber = PayerClaimControlNumber,
            PatientControlNumber = PatientControlNumber,
            Status = PayerDirectoryClaimStatus.Pended,
            ServiceLines = { new PayerDirectoryClaimLine { LineNumber = 1 } }
        }
    };
}
