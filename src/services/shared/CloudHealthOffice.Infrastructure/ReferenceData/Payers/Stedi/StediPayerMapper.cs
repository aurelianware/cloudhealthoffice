using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers.Stedi.DTOs;

namespace CloudHealthOffice.Infrastructure.ReferenceData.Payers.Stedi;

/// <summary>
/// Maps a Stedi payer-directory DTO onto the canonical
/// <see cref="PayerReference"/>. This is the only type that sees both shapes.
/// </summary>
internal static class StediPayerMapper
{
    public const string Source = PayerReferenceOptions.SourceStedi;

    public static PayerReference? ToCanonical(StediPayerDto dto, DateTimeOffset syncedAt)
    {
        if (string.IsNullOrWhiteSpace(dto.StediId) || string.IsNullOrWhiteSpace(dto.DisplayName))
        {
            return null;
        }

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(aliases, dto.PrimaryPayerId);
        Add(aliases, dto.ConciseName);
        if (dto.Aliases is not null)
        {
            foreach (var alias in dto.Aliases) Add(aliases, alias);
        }

        if (dto.Names is not null)
        {
            foreach (var name in dto.Names) Add(aliases, name);
        }

        aliases.Remove(dto.DisplayName);

        var identifiers = new List<PayerExternalIdentifier>
        {
            new()
            {
                System = StediPayerIdentifiers.System,
                Type = StediPayerIdentifiers.IdType,
                Value = dto.StediId.Trim()
            }
        };

        if (!string.IsNullOrWhiteSpace(dto.PrimaryPayerId))
        {
            identifiers.Add(new PayerExternalIdentifier
            {
                System = StediPayerIdentifiers.System,
                Type = StediPayerIdentifiers.PrimaryPayerIdType,
                Value = dto.PrimaryPayerId.Trim()
            });
            identifiers.Add(new PayerExternalIdentifier
            {
                System = StediPayerIdentifiers.System,
                Type = StediPayerIdentifiers.TradingPartnerServiceIdType,
                Value = dto.PrimaryPayerId.Trim()
            });
        }
        else
        {
            identifiers.Add(new PayerExternalIdentifier
            {
                System = StediPayerIdentifiers.System,
                Type = StediPayerIdentifiers.TradingPartnerServiceIdType,
                Value = dto.StediId.Trim()
            });
        }

        var (capabilities, enrollment) = MapSupport(dto);

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Put(metadata, "coverageTypes", Join(dto.CoverageTypes));
        Put(metadata, "operatingStates", Join(dto.OperatingStates));
        Put(metadata, "programs", Join(dto.Programs));
        Put(metadata, "parentPayerGroupId", dto.ParentPayerGroupId);
        Put(metadata, "parentPayerGroupName", dto.ParentPayerGroupName);
        Put(metadata, "website", dto.Urls?.Website);
        Put(metadata, "conciseName", dto.ConciseName);
        Put(metadata, "claimSubmission", dto.TransactionSupport?.ClaimSubmission);
        Put(metadata, "coordinationOfBenefits", dto.TransactionSupport?.CoordinationOfBenefits);
        if (dto.Enrollment?.PtanRequired is { } ptan)
        {
            metadata["ptanRequired"] = ptan ? "true" : "false";
        }

        if (dto.EmployerIdentificationNumbers is { Count: > 0 })
        {
            metadata["employerIdentificationNumbers"] = Join(dto.EmployerIdentificationNumbers)!;
        }

        return new PayerReference
        {
            Id = dto.StediId.Trim(),
            Name = dto.DisplayName.Trim(),
            Aliases = aliases.ToList(),
            ExternalIdentifiers = identifiers,
            SupportedTransactions = capabilities,
            EnrollmentRequirements = enrollment,
            Active = true,
            Provenance = new PayerReferenceProvenance
            {
                Source = Source,
                SourceUpdatedAt = syncedAt,
                LastSyncedAt = syncedAt
            },
            Metadata = metadata
        };
    }

    private static (List<PayerTransactionCapability> Capabilities, List<PayerEnrollmentRequirement> Enrollment)
        MapSupport(StediPayerDto dto)
    {
        var capabilities = new List<PayerTransactionCapability>();
        var enrollment = new List<PayerEnrollmentRequirement>();
        var processes = dto.Enrollment?.TransactionEnrollmentProcesses;

        AddCapability(
            capabilities, enrollment,
            HealthcareTransactionType.Eligibility270271,
            dto.TransactionSupport?.EligibilityCheck,
            FindProcess(processes, "eligibilityInquiry", "eligibilityCheck"));
        AddCapability(
            capabilities, enrollment,
            HealthcareTransactionType.ClaimStatus276277,
            dto.TransactionSupport?.ClaimStatus,
            FindProcess(processes, "claimStatusInquiry", "claimStatus"));
        AddCapability(
            capabilities, enrollment,
            HealthcareTransactionType.ProfessionalClaim837P,
            dto.TransactionSupport?.ProfessionalClaimSubmission,
            FindProcess(processes, "professionalClaim", "professionalClaimSubmission"));
        AddCapability(
            capabilities, enrollment,
            HealthcareTransactionType.InstitutionalClaim837I,
            dto.TransactionSupport?.InstitutionalClaimSubmission,
            FindProcess(processes, "institutionalClaim", "institutionalClaimSubmission"));
        AddCapability(
            capabilities, enrollment,
            HealthcareTransactionType.DentalClaim837D,
            dto.TransactionSupport?.DentalClaimSubmission,
            FindProcess(processes, "dentalClaim", "dentalClaimSubmission"));
        AddCapability(
            capabilities, enrollment,
            HealthcareTransactionType.ClaimAttachment275,
            dto.TransactionSupport?.UnsolicitedClaimAttachment,
            FindProcess(processes, "unsolicitedClaimAttachment"));
        AddCapability(
            capabilities, enrollment,
            HealthcareTransactionType.Remittance835,
            dto.TransactionSupport?.ClaimPayment,
            FindProcess(processes, "claimPayment"));

        return (capabilities, enrollment);
    }

    private static void AddCapability(
        List<PayerTransactionCapability> capabilities,
        List<PayerEnrollmentRequirement> enrollment,
        HealthcareTransactionType transaction,
        string? raw,
        StediEnrollmentProcessDto? process)
    {
        var support = ParseSupport(raw);
        capabilities.Add(new PayerTransactionCapability
        {
            Transaction = transaction,
            Support = support
        });

        if (support == PayerTransactionSupport.EnrollmentRequired || process is not null)
        {
            enrollment.Add(new PayerEnrollmentRequirement
            {
                Transaction = transaction,
                Required = support == PayerTransactionSupport.EnrollmentRequired,
                ProcessType = process?.Type,
                Timeframe = process?.Timeframe
            });
        }
    }

    private static PayerTransactionSupport ParseSupport(string? raw) =>
        raw?.Trim().ToUpperInvariant() switch
        {
            "SUPPORTED" => PayerTransactionSupport.Supported,
            "ENROLLMENT_REQUIRED" => PayerTransactionSupport.EnrollmentRequired,
            _ => PayerTransactionSupport.NotSupported
        };

    private static StediEnrollmentProcessDto? FindProcess(
        Dictionary<string, StediEnrollmentProcessDto>? processes, params string[] keys)
    {
        if (processes is null)
        {
            return null;
        }

        foreach (var key in keys)
        {
            foreach (var pair in processes)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }
        }

        return null;
    }

    private static void Add(HashSet<string> set, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            set.Add(value.Trim());
        }
    }

    private static void Put(Dictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            metadata[key] = value;
        }
    }

    private static string? Join(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return null;
        }

        var joined = string.Join("|", values.Where(v => !string.IsNullOrWhiteSpace(v)));
        return string.IsNullOrEmpty(joined) ? null : joined;
    }
}
