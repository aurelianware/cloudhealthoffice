using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoverageService.Services;

/// <summary>
/// Typed client into provider-service. coverage-service consults this for PCP
/// validation — provider record, network participations, panel limits.
///
/// DTO shape mirrors provider-service's <c>Provider</c> closely enough for our
/// uses without taking a project reference. Fields we don't need (bank account,
/// hospital affiliations, etc.) are intentionally omitted.
/// </summary>
public interface IProviderServiceClient
{
    /// <summary>Look up a provider by NPI. Returns null on 404.</summary>
    Task<ProviderDto?> GetByNpiAsync(string npi, CancellationToken ct = default);
}

/// <summary>
/// In-process panel counter. Production binding hits capitation-service
/// <c>GET /api/coverage/by-pcp/{npi}</c> count; tests bind a fake.
/// </summary>
public interface IPanelCounter
{
    /// <summary>
    /// Current count of members assigned to this NPI. Inherently racy; see
    /// <c>docs/architecture/pcp-assignment.md</c> "Panel race" section.
    /// </summary>
    Task<int> CurrentPanelCountAsync(string tenantId, string providerNpi, CancellationToken ct = default);
}

public sealed class ProviderDto
{
    public string Id { get; set; } = string.Empty;
    public string NPI { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string PrimarySpecialty { get; set; } = string.Empty;
    public ProviderStatusDto Status { get; set; } = ProviderStatusDto.Active;
    public CredentialingStatusDto CredentialingStatus { get; set; } = CredentialingStatusDto.Pending;
    public bool AcceptingNewPatients { get; set; } = true;
    public List<NetworkParticipationDto> NetworkParticipations { get; set; } = new();
}

public sealed class NetworkParticipationDto
{
    public string? PlanId { get; set; }
    public Models.LineOfBusiness LineOfBusiness { get; set; }
    public string NetworkTier { get; set; } = "Tier1";
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public bool AcceptingNewPatients { get; set; } = true;
    public int? PanelLimit { get; set; }
    public bool? PanelAccepted { get; set; }
    public List<Models.LineOfBusiness> AcceptedLobs { get; set; } = new();
    public int? MinAcceptedAgeYears { get; set; }
    public int? MaxAcceptedAgeYears { get; set; }
}

public enum ProviderStatusDto
{
    Active = 1,
    Inactive = 2,
    Terminated = 3,
    Pending = 4
}

public enum CredentialingStatusDto
{
    Pending = 1,
    Approved = 2,
    Denied = 3,
    Expired = 4,
    Suspended = 5
}
