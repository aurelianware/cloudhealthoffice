using CoverageService.Repositories;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoverageService.Services;

/// <summary>
/// Stub of the nightly panel reconciliation. Counts open assignments per (tenant,
/// NPI) and logs anything that exceeds the provider's <c>PanelLimit</c>.
///
/// This intentionally has no scheduler bound to it yet — the goal of the stub is
/// to make the observability path exist before the panel race actually bites.
/// Wiring it to a hosted background service / Azure Function trigger is tracked
/// under roadmap 5.7 Phase 2 alongside the Redis-lock work
/// (see docs/architecture/pcp-assignment.md "Panel race").
///
/// TODO(addendum-a): trigger from a HostedService cron and emit a metric per
/// over-limit panel so on-call can alert on sustained overage.
/// </summary>
public sealed class PcpPanelReconciliationJob
{
    private readonly IPcpAssignmentRepository _assignments;
    private readonly IProviderServiceClient _providers;
    private readonly ILogger<PcpPanelReconciliationJob> _logger;

    public PcpPanelReconciliationJob(
        IPcpAssignmentRepository assignments,
        IProviderServiceClient providers,
        ILogger<PcpPanelReconciliationJob> logger)
    {
        _assignments = assignments;
        _providers = providers;
        _logger = logger;
    }

    /// <summary>
    /// Returns the list of (npi, currentCount, limit) tuples that exceed their
    /// configured limit. Caller is responsible for emitting metrics or paging.
    /// </summary>
    public async Task<IReadOnlyList<PanelOverageReport>> ScanAsync(
        string tenantId, IEnumerable<string> npisToCheck, CancellationToken ct = default)
    {
        var report = new List<PanelOverageReport>();
        foreach (var npi in npisToCheck)
        {
            var provider = await _providers.GetByNpiAsync(npi, ct);
            if (provider == null) continue;

            var count = await _assignments.CountOpenByNpiAsync(tenantId, npi);

            // Check every participation — if any has a limit and we're over it, flag.
            foreach (var part in provider.NetworkParticipations)
            {
                if (!part.PanelLimit.HasValue) continue;
                if (count > part.PanelLimit.Value)
                {
                    var entry = new PanelOverageReport(npi, count, part.PanelLimit.Value, part.LineOfBusiness.ToString());
                    report.Add(entry);
                    _logger.LogWarning(
                        "PCP panel over-limit tenant={TenantId} npi={Npi} count={Count} limit={Limit} lob={Lob}",
                        tenantId, npi, count, part.PanelLimit.Value, part.LineOfBusiness);
                }
            }
        }
        return report;
    }
}

public sealed record PanelOverageReport(string Npi, int CurrentCount, int Limit, string Lob);
