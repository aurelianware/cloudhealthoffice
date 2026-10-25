using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CoverageService.Exceptions;
using CoverageService.Models;
using CoverageService.Repositories;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace CoverageService.Services;

/// <summary>A recoverable enrollment/roster exception; never silently drop or reroute the coverage.</summary>
public sealed class PcpRosterWriteException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Protects production repository writers, including 834 reinstatements and status sweeps.
/// Existing PCP coverage spans reserve capacity; this does not schedule PCP changes.
/// </summary>
public sealed class PcpRosterWriteGuard(IMongoDatabase database, PcpMongoSession context,
    PcpWriteTransaction transaction, IProviderServiceClient providers)
{
    private readonly IMongoCollection<Coverage> _coverage = database.GetCollection<Coverage>(CoverageRepositoryMongo.CollectionName);

    public async Task<T> ExecuteAsync<T>(Coverage proposed, Func<Task<T>> write,
        bool creating = false, bool deleting = false, CoverageStatus? newStatus = null)
    {
        // The caller's revision must survive driver retries and aborted transactions.
        var expectedRevision = proposed.Revision;
        var joiningAssignment = context.Current is not null;
        try
        {
            return await transaction.ExecuteAsync(proposed.TenantId, proposed.MemberId, proposed.PcpNpi, async () =>
            {
                proposed.Revision = expectedRevision;
                var old = await _coverage.Find(context.Current!, c => c.TenantId == proposed.TenantId && c.Id == proposed.Id)
                    .FirstOrDefaultAsync();
                if (!creating && (old is null || old.Revision != expectedRevision || old.MemberId != proposed.MemberId))
                    throw new CoverageConcurrencyException(proposed.Id, expectedRevision);
                var candidate = BsonSerializer.Deserialize<Coverage>(proposed.ToBson());
                if (newStatus.HasValue) candidate.Status = newStatus.Value;

                // Changing the PCP itself must also write assignment history. The
                // assignment service is the only supported writer for that change.
                if (!joiningAssignment && !deleting && !string.Equals(old?.PcpNpi ?? "", candidate.PcpNpi ?? "", StringComparison.Ordinal))
                    throw new PcpRosterWriteException("PCP_ASSIGNMENT_API_REQUIRED", "Change the PCP through the member PCP assignment API; coverage edits cannot bypass assignment history.");
                if (!deleting && !string.IsNullOrEmpty(candidate.PcpNpi))
                {
                    if (!joiningAssignment && old?.PcpAssignmentDate != candidate.PcpAssignmentDate)
                        throw new PcpRosterWriteException("PCP_ASSIGNMENT_API_REQUIRED", "Coverage edits cannot change the PCP assignment date.");
                    await ValidateAdditionAsync(candidate, old);
                }
                return await write();
            });
        }
        catch { proposed.Revision = expectedRevision; throw; }
    }

    private async Task ValidateAdditionAsync(Coverage candidate, Coverage? old)
    {
        var before = await _coverage.Find(context.Current!, c => c.TenantId == candidate.TenantId && c.PcpNpi == candidate.PcpNpi)
            .ToListAsync();
        var after = before.Where(c => c.Id != candidate.Id).Append(candidate).ToList();
        // Only newly occupied member-days need admission. Removing occupancy,
        // duplicate coverage and non-roster edits remain possible on full panels.
        var participationChanged = old is not null && (old.PlanId != candidate.PlanId || old.LineOfBusiness != candidate.LineOfBusiness);
        var addedDays = PcpCoverageIntervals.AddedMemberBoundaries(before, after, candidate.MemberId, participationChanged);
        if (addedDays.Count == 0) return;
        var provider = await providers.GetByNpiAsync(candidate.TenantId, candidate.PcpNpi!);
        if (provider is null) throw new PcpRosterWriteException(PcpValidationCodes.ProviderNotFound, "Provider was not found for roster admission.");
        if (provider.Status != ProviderStatusDto.Active || provider.CredentialingStatus != CredentialingStatusDto.Approved)
            throw new PcpRosterWriteException(PcpValidationCodes.ProviderInactive, "Provider must be active and credentialed for roster admission.");

        // Participation boundaries can change the limit inside a long coverage span.
        var boundaries = addedDays.Concat(provider.NetworkParticipations.Select(p => p.EffectiveDate.Date))
            .Concat(provider.NetworkParticipations.Where(p => p.TerminationDate.HasValue && p.TerminationDate.Value.Date < DateTime.MaxValue.Date)
                .Select(p => p.TerminationDate!.Value.Date.AddDays(1))).Distinct().OrderBy(d => d);
        foreach (var day in boundaries)
        {
            if (!PcpCoverageIntervals.Occupies(new[] { candidate }, candidate.MemberId, day)
                || (!participationChanged && PcpCoverageIntervals.Occupies(before, candidate.MemberId, day))) continue;
            var participation = provider.NetworkParticipations.Where(p => (p.PlanId is null || p.PlanId == candidate.PlanId)
                && p.LineOfBusiness == candidate.LineOfBusiness && p.EffectiveDate.Date <= day
                && (!p.TerminationDate.HasValue || p.TerminationDate.Value.Date >= day))
                .OrderBy(p => p.NetworkTier).FirstOrDefault();
            if (participation is null)
                throw new PcpRosterWriteException(PcpValidationCodes.NoNetworkParticipation, "No network participation covers the added PCP roster interval.");
            if (!(participation.PanelAccepted ?? participation.AcceptingNewPatients)
                || (participation.AcceptedLobs.Count > 0 && !participation.AcceptedLobs.Contains(candidate.LineOfBusiness)))
                throw new PcpRosterWriteException(PcpValidationCodes.NotAcceptingPatients, "Provider is not accepting this added PCP roster interval.");
            // Global tenant/NPI counting deliberately preserves #1547's conservative
            // scope; plan/location-specific limits need an approved policy change.
            if (participation.PanelLimit.HasValue && PcpCoverageIntervals.Count(after, day) > participation.PanelLimit.Value)
                throw new PcpRosterWriteException(PcpValidationCodes.PanelFull, "PCP capacity is exceeded within the added coverage interval; enrollment requires exception resolution.");
        }
    }
}

/// <summary>
/// Inclusive coverage termination, clamped to the PCP assignment date. Count
/// distinct medical/legacy members at every boundary, including future spans.
/// Suspended/unknown and malformed terminated-without-date rows do not reserve.
/// </summary>
public static class PcpCoverageIntervals
{
    private static (DateTime Start, DateTime End)? Span(Coverage c)
    {
        if ((!string.IsNullOrEmpty(c.InsuranceLineCode) && c.InsuranceLineCode != InsuranceLineCodes.Health)
            || !Coverage.DateOfServiceStatuses.Contains(c.Status)
            || (c.Status == CoverageStatus.Terminated && !c.TerminationDate.HasValue)) return null;
        var start = c.EffectiveDate.Date;
        if (c.PcpAssignmentDate.HasValue && c.PcpAssignmentDate.Value.Date > start) start = c.PcpAssignmentDate.Value.Date;
        var end = c.TerminationDate?.Date ?? DateTime.MaxValue.Date;
        return start <= end ? (start, end) : null;
    }

    public static bool Occupies(IEnumerable<Coverage> rows, string member, DateTime day) =>
        rows.Any(c => c.MemberId == member && Span(c) is { } s && s.Start <= day.Date && day.Date <= s.End);

    public static int Count(IEnumerable<Coverage> rows, DateTime day) => rows
        .Where(c => Span(c) is { } s && s.Start <= day.Date && day.Date <= s.End)
        .Select(c => c.MemberId).Distinct(StringComparer.Ordinal).Count();

    public static IReadOnlyList<DateTime> AddedMemberBoundaries(IReadOnlyList<Coverage> before,
        IReadOnlyList<Coverage> after, string member, bool includeExisting = false)
    {
        var dates = new SortedSet<DateTime>();
        foreach (var row in before.Concat(after))
        {
            if (Span(row) is not { } span) continue;
            dates.Add(span.Start);
            if (span.End < DateTime.MaxValue.Date) dates.Add(span.End.AddDays(1));
        }
        return dates.Where(d => Occupies(after, member, d) && (includeExisting || !Occupies(before, member, d))).ToList();
    }
}
