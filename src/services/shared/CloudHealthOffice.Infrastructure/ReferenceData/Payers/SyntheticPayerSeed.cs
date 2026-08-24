using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers.Stedi;

namespace CloudHealthOffice.Infrastructure.ReferenceData.Payers;

/// <summary>
/// Deterministic, non-PHI synthetic payers used by CI and local hosts when no
/// live directory sync has run. Public payer names are not used; identifiers
/// are invented.
/// </summary>
internal static class SyntheticPayerSeed
{
    public const string EligibleId = "SYNTH-ELIGIBLE";
    public const string EligibleTradingPartnerId = "60054";
    public const string UnsupportedId = "SYNTH-UNSUPPORTED";
    public const string EnrollmentId = "SYNTH-ENROLL";
    public const string MissingExternalId = "SYNTH-NO-EXTERNAL";
    public const string DuplicateAId = "SYNTH-DUP-A";
    public const string DuplicateBId = "SYNTH-DUP-B";
    public const string SharedAlias = "SYNTH-DUP";

    public static IReadOnlyList<PayerReference> Create(DateTimeOffset syncedAt) =>
        new[]
        {
            Create(
                EligibleId,
                "Synthetic Eligible Payer",
                new[] { "AETNA", EligibleId, EligibleTradingPartnerId },
                EligibleTradingPartnerId,
                "AAAAA",
                PayerTransactionSupport.Supported,
                syncedAt),
            Create(
                UnsupportedId,
                "Synthetic Unsupported Eligibility Payer",
                new[] { UnsupportedId },
                "60099",
                "BBBBB",
                PayerTransactionSupport.NotSupported,
                syncedAt),
            Create(
                EnrollmentId,
                "Synthetic Enrollment-Required Payer",
                new[] { EnrollmentId },
                "60100",
                "CCCCC",
                PayerTransactionSupport.EnrollmentRequired,
                syncedAt),
            new PayerReference
            {
                Id = MissingExternalId,
                Name = "Synthetic Payer Without Clearinghouse Identifier",
                Aliases = new List<string> { MissingExternalId },
                Active = true,
                SupportedTransactions =
                {
                    new PayerTransactionCapability
                    {
                        Transaction = HealthcareTransactionType.Eligibility270271,
                        Support = PayerTransactionSupport.Supported
                    },
                    new PayerTransactionCapability
                    {
                        Transaction = HealthcareTransactionType.ClaimStatus276277,
                        Support = PayerTransactionSupport.Supported
                    }
                },
                Provenance = SeedProvenance(syncedAt)
            },
            Create(
                DuplicateAId,
                "Synthetic Duplicate Payer A",
                new[] { SharedAlias, DuplicateAId },
                "60101",
                "DDDDD",
                PayerTransactionSupport.Supported,
                syncedAt),
            Create(
                DuplicateBId,
                "Synthetic Duplicate Payer B",
                new[] { SharedAlias, DuplicateBId },
                "60102",
                "EEEEE",
                PayerTransactionSupport.Supported,
                syncedAt)
        };

    private static PayerReference Create(
        string id,
        string name,
        string[] aliases,
        string tradingPartnerServiceId,
        string directoryId,
        PayerTransactionSupport eligibility,
        DateTimeOffset syncedAt)
    {
        var enrollmentRequired = eligibility == PayerTransactionSupport.EnrollmentRequired;
        return new PayerReference
        {
            Id = id,
            Name = name,
            Aliases = aliases.ToList(),
            Active = true,
            ExternalIdentifiers =
            {
                new PayerExternalIdentifier
                {
                    System = StediPayerIdentifiers.System,
                    Type = StediPayerIdentifiers.IdType,
                    Value = directoryId
                },
                new PayerExternalIdentifier
                {
                    System = StediPayerIdentifiers.System,
                    Type = StediPayerIdentifiers.TradingPartnerServiceIdType,
                    Value = tradingPartnerServiceId
                },
                new PayerExternalIdentifier
                {
                    System = StediPayerIdentifiers.System,
                    Type = StediPayerIdentifiers.PrimaryPayerIdType,
                    Value = tradingPartnerServiceId
                }
            },
            SupportedTransactions =
            {
                new PayerTransactionCapability
                {
                    Transaction = HealthcareTransactionType.Eligibility270271,
                    Support = eligibility
                },
                new PayerTransactionCapability
                {
                    Transaction = HealthcareTransactionType.ProfessionalClaim837P,
                    Support = eligibility
                },
                new PayerTransactionCapability
                {
                    Transaction = HealthcareTransactionType.InstitutionalClaim837I,
                    Support = eligibility
                },
                new PayerTransactionCapability
                {
                    Transaction = HealthcareTransactionType.DentalClaim837D,
                    Support = eligibility
                },
                new PayerTransactionCapability
                {
                    Transaction = HealthcareTransactionType.ClaimAttachment275,
                    Support = eligibility
                },
                new PayerTransactionCapability
                {
                    Transaction = HealthcareTransactionType.ClaimStatus276277,
                    Support = eligibility
                }
            },
            EnrollmentRequirements = enrollmentRequired
                ? new List<PayerEnrollmentRequirement>
                {
                    new()
                    {
                        Transaction = HealthcareTransactionType.Eligibility270271,
                        Required = true
                    },
                    new()
                    {
                        Transaction = HealthcareTransactionType.ProfessionalClaim837P,
                        Required = true
                    },
                    new()
                    {
                        Transaction = HealthcareTransactionType.ClaimAttachment275,
                        Required = true
                    },
                    new()
                    {
                        Transaction = HealthcareTransactionType.ClaimStatus276277,
                        Required = true
                    }
                }
                : new List<PayerEnrollmentRequirement>(),
            Provenance = SeedProvenance(syncedAt)
        };
    }

    private static PayerReferenceProvenance SeedProvenance(DateTimeOffset syncedAt) => new()
    {
        Source = PayerReferenceOptions.SourceSeed,
        SourceUpdatedAt = syncedAt,
        LastSyncedAt = syncedAt
    };
}
