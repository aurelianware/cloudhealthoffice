using CoverageService.Models;
using CoverageService.Repositories;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CoverageService.Services;

/// <summary>
/// Result of an assignment attempt. Either <see cref="Assignment"/> is populated
/// (success) or <see cref="Error"/> is (validation failure).
/// </summary>
public sealed class PcpAssignmentResult
{
    public PcpAssignment? Assignment { get; init; }
    public PcpValidationError? Error { get; init; }
    public bool IsSuccess => Assignment != null;

    public static PcpAssignmentResult Ok(PcpAssignment assignment) => new() { Assignment = assignment };
    public static PcpAssignmentResult Fail(PcpValidationError error) => new() { Error = error };
}

public interface IPcpAssignmentService
{
    Task<PcpAssignmentResult> AssignAsync(string tenantId, string memberId, AssignPcpCommand cmd, CancellationToken ct = default);
    Task<PcpAssignment?> GetCurrentAsync(string tenantId, string memberId, CancellationToken ct = default);
    Task<IReadOnlyList<PcpAssignment>> GetHistoryAsync(string tenantId, string memberId, CancellationToken ct = default);
}

public sealed class AssignPcpCommand
{
    public string ProviderNpi { get; set; } = string.Empty;
    public string? ProviderId { get; set; }
    public DateTime EffectiveDate { get; set; }
    public string? Reason { get; set; }
    public PcpAssignmentSource Source { get; set; } = PcpAssignmentSource.MemberChoice;
    public DateTime? MemberDateOfBirth { get; set; }
    public string? AssignedBy { get; set; }
}

/// <summary>
/// PCP assignment with a fail-fast validation ladder. The order of checks is the
/// API contract — the first failure is what gets returned to the caller, logged,
/// and metric'd, so callers can build deterministic remediation flows.
///
/// On success: closes the prior open assignment row (stamps EndDate), writes a
/// new <see cref="PcpAssignment"/>, and updates the denormalized PCP fields on
/// every active <see cref="Coverage"/> for the member.
/// </summary>
public sealed class PcpAssignmentService : IPcpAssignmentService
{
    private readonly ICoverageRepository _coverage;
    private readonly IPcpAssignmentRepository _assignments;
    private readonly IProviderServiceClient _providers;
    private readonly IPanelCounter _panel;
    private readonly ILogger<PcpAssignmentService> _logger;

    public PcpAssignmentService(
        ICoverageRepository coverage,
        IPcpAssignmentRepository assignments,
        IProviderServiceClient providers,
        IPanelCounter panel,
        ILogger<PcpAssignmentService> logger)
    {
        _coverage = coverage;
        _assignments = assignments;
        _providers = providers;
        _panel = panel;
        _logger = logger;
    }

    public Task<PcpAssignment?> GetCurrentAsync(string tenantId, string memberId, CancellationToken ct = default)
        => _assignments.GetCurrentAsync(tenantId, memberId);

    public Task<IReadOnlyList<PcpAssignment>> GetHistoryAsync(string tenantId, string memberId, CancellationToken ct = default)
        => _assignments.GetHistoryAsync(tenantId, memberId);

    public async Task<PcpAssignmentResult> AssignAsync(
        string tenantId, string memberId, AssignPcpCommand cmd, CancellationToken ct = default)
    {
        // Pre-flight: needs to come before the validation ladder because without
        // active coverage there's nothing to attach the assignment to.
        var effective = cmd.EffectiveDate == default ? DateTime.UtcNow.Date : cmd.EffectiveDate.Date;
        var coverages = await _coverage.GetActiveCoverageByMemberIdAsync(tenantId, memberId, effective);
        if (coverages.Count == 0)
        {
            return PcpAssignmentResult.Fail(new PcpValidationError(
                PcpValidationCodes.NoActiveCoverage, "memberId",
                "No active coverage for member on the requested effective date."));
        }

        if (string.IsNullOrWhiteSpace(cmd.ProviderNpi) || cmd.ProviderNpi.Length != 10 || !cmd.ProviderNpi.All(char.IsDigit))
        {
            return PcpAssignmentResult.Fail(new PcpValidationError(
                PcpValidationCodes.InvalidNpi, "providerNpi",
                "providerNpi must be exactly 10 digits."));
        }

        // Pick the canonical coverage to validate against — if a member has
        // multiple active coverages (e.g., Medical + Dental), the Health line
        // drives PCP rules. `LineOfBusiness` is Commercial/Medicare/etc., it
        // does NOT distinguish Health from Dental; the discriminator lives on
        // `InsuranceLineCode` (HLT/DEN/VIS/LIF). Fall back to LOB ordering only
        // when no Health-coded row exists (legacy data).
        var primary = coverages
            .Where(c => c.InsuranceLineCode == InsuranceLineCodes.Health)
            .OrderBy(c => (int)c.LineOfBusiness)
            .FirstOrDefault()
            ?? coverages.OrderBy(c => (int)c.LineOfBusiness).First();

        // ── Validation ladder ─────────────────────────────────────────────
        // Order matters; first failure wins. Do NOT reorder without a portal+API
        // changelog entry — error codes are an integration contract.

        var provider = await _providers.GetByNpiAsync(cmd.ProviderNpi, ct);
        if (provider == null)
        {
            return Fail(PcpValidationCodes.ProviderNotFound, "providerNpi",
                $"Provider with NPI {cmd.ProviderNpi} not found in directory.");
        }

        if (provider.Status != ProviderStatusDto.Active)
        {
            return Fail(PcpValidationCodes.ProviderInactive, "providerNpi",
                $"Provider is {provider.Status}; only Active providers may be assigned as PCP.");
        }

        if (provider.CredentialingStatus != CredentialingStatusDto.Approved)
        {
            return Fail(PcpValidationCodes.ProviderNotCredentialed, "providerNpi",
                $"Provider credentialing is {provider.CredentialingStatus}; must be Approved.");
        }

        var participation = SelectParticipation(provider, primary, effective);
        if (participation == null)
        {
            return Fail(PcpValidationCodes.NoNetworkParticipation, "providerNpi",
                $"Provider has no active network participation for plan {primary.PlanId} / LOB {primary.LineOfBusiness}.");
        }

        // PanelAccepted overrides the broader AcceptingNewPatients flag — a
        // provider may be open to referrals but closed to new PCP assignments.
        var panelAccepted = participation.PanelAccepted ?? participation.AcceptingNewPatients;
        if (!panelAccepted)
        {
            return Fail(PcpValidationCodes.NotAcceptingPatients, "providerNpi",
                "Provider is not currently accepting new PCP patients on this participation.");
        }

        if (participation.AcceptedLobs.Count > 0 && !participation.AcceptedLobs.Contains(primary.LineOfBusiness))
        {
            return Fail(PcpValidationCodes.LobNotAccepted, "providerNpi",
                $"Provider does not accept LOB {primary.LineOfBusiness} as PCP on this participation.");
        }

        if (cmd.MemberDateOfBirth.HasValue)
        {
            var ageYears = AgeInYears(cmd.MemberDateOfBirth.Value, effective);
            if (participation.MinAcceptedAgeYears.HasValue && ageYears < participation.MinAcceptedAgeYears.Value)
            {
                return Fail(PcpValidationCodes.AgeOutOfRange, "memberId",
                    $"Member age {ageYears} below provider's minimum accepted age {participation.MinAcceptedAgeYears.Value}.");
            }
            if (participation.MaxAcceptedAgeYears.HasValue && ageYears > participation.MaxAcceptedAgeYears.Value)
            {
                return Fail(PcpValidationCodes.AgeOutOfRange, "memberId",
                    $"Member age {ageYears} above provider's maximum accepted age {participation.MaxAcceptedAgeYears.Value}.");
            }
        }

        if (participation.PanelLimit.HasValue)
        {
            // Inherently racy — between this read and the AddAsync below, another
            // assignment can land. Documented in docs/architecture/pcp-assignment.md
            // "Panel race" — Phase 1 accepts the slack, nightly reconciliation
            // (PcpPanelReconciliationJob) flags over-limit panels. Phase 2 will move
            // to a Redis lock per (tenantId, npi) — see Addendum A.7.2.
            // TODO(addendum-a): replace with TryAcquireAsync(npi) -> ITransientLock.
            var current = await _panel.CurrentPanelCountAsync(tenantId, cmd.ProviderNpi, ct);
            if (current >= participation.PanelLimit.Value)
            {
                return Fail(PcpValidationCodes.PanelFull, "providerNpi",
                    $"Provider panel is full ({current} / {participation.PanelLimit.Value}).");
            }
        }

        // ── Validation passed; persist ────────────────────────────────────

        var networkStatus = string.IsNullOrEmpty(participation.NetworkTier)
            ? "InNetwork"
            : participation.NetworkTier;

        // Close any open prior row(s). EndDate = effective so the audit trail
        // shows continuity (no gap between assignments).
        await _assignments.EndOpenAssignmentsAsync(tenantId, memberId, effective);

        var assignment = await _assignments.AddAsync(new PcpAssignment
        {
            TenantId = tenantId,
            MemberId = memberId,
            CoverageId = primary.Id,
            ProviderId = cmd.ProviderId ?? provider.Id,
            ProviderNpi = cmd.ProviderNpi,
            ProviderName = provider.FullName,
            EffectiveDate = effective,
            EndDate = null,
            AssignmentReason = cmd.Reason,
            AssignmentSource = cmd.Source,
            NetworkStatusAtAssignment = networkStatus,
            AssignedBy = cmd.AssignedBy
        });

        // Update denormalized PCP fields only on PCP-eligible medical coverage
        // rows. Dental / Vision / Life / etc. rows never carry a PCP — stamping
        // them produces nonsense for downstream readers. This mirrors the
        // validation-side filter above (primary = Health-coded row). See PR #656
        // cleanup pass.
        //
        // Legacy fallback: rows loaded before InsuranceLineCode was required may
        // have a null/empty line code. When no Health row exists, stamp all
        // active rows as before so existing tenants don't regress silently.
        var denormTargets = coverages
            .Where(c => c.InsuranceLineCode == InsuranceLineCodes.Health)
            .ToList();
        if (denormTargets.Count == 0) denormTargets = coverages.ToList();

        var method = cmd.Source switch
        {
            PcpAssignmentSource.MemberChoice => PcpAssignmentMethod.MemberSelected,
            PcpAssignmentSource.AutoAssigned => PcpAssignmentMethod.AutoAssigned,
            PcpAssignmentSource.AdminAssigned => PcpAssignmentMethod.Administrative,
            _ => PcpAssignmentMethod.MemberSelected
        };

        foreach (var cov in denormTargets)
        {
            if (!string.IsNullOrEmpty(cov.PcpNpi) && cov.PcpNpi != cmd.ProviderNpi)
            {
                cov.PreviousPcpNpi = cov.PcpNpi;
            }
            cov.PcpNpi = cmd.ProviderNpi;
            cov.PcpName = provider.FullName;
            cov.PcpAssignmentDate = effective;
            cov.PcpAssignmentMethod = method;
            cov.LastUpdatedDate = DateTime.UtcNow;
            cov.LastUpdatedBy = cmd.AssignedBy ?? "pcp-assignment-service";
            await _coverage.UpdateAsync(cov);
        }

        _logger.LogInformation(
            "PCP assigned tenant={TenantId} member={MemberId} npi={Npi} source={Source}",
            Sanitize(tenantId), Sanitize(memberId), Sanitize(cmd.ProviderNpi), cmd.Source);

        return PcpAssignmentResult.Ok(assignment);
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }

    private static NetworkParticipationDto? SelectParticipation(ProviderDto provider, Coverage coverage, DateTime asOf)
    {
        return provider.NetworkParticipations
            .Where(np => (np.PlanId == null || np.PlanId == coverage.PlanId)
                         && np.LineOfBusiness == coverage.LineOfBusiness
                         && np.EffectiveDate.Date <= asOf
                         && (np.TerminationDate == null || np.TerminationDate.Value.Date >= asOf))
            .OrderBy(np => np.NetworkTier) // Tier1 wins
            .FirstOrDefault();
    }

    private static int AgeInYears(DateTime dob, DateTime asOf)
    {
        var age = asOf.Year - dob.Year;
        if (asOf < dob.AddYears(age)) age--;
        return age;
    }

    private PcpAssignmentResult Fail(string code, string field, string message)
    {
        _logger.LogInformation("PCP validation failed code={Code} field={Field}", code, field);
        return PcpAssignmentResult.Fail(new PcpValidationError(code, field, message));
    }
}
