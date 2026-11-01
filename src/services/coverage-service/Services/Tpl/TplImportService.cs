using CloudHealthOffice.Infrastructure.Logging;
using CoverageService.Models.Tpl;
using CoverageService.Repositories;

namespace CoverageService.Services.Tpl;

public enum TplImportMode
{
    /// <summary>The file carries changes only; rows it does not mention are left alone.</summary>
    Incremental,
    /// <summary>The file is the state's whole picture for this source: an
    /// active row it no longer carries is ended the day before the file date.</summary>
    Full,
}

public sealed record TplImportRequest(
    string TenantId,
    string Content,
    TplFileLayout Layout,
    string State,
    string? FileName,
    DateTime FileDate,
    TplImportMode Mode,
    bool DryRun,
    bool AllowLargeDrop,
    string Actor);

public sealed class TplImportReport
{
    public string Layout { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public bool DryRun { get; set; }
    public TplImportMode Mode { get; set; }
    public DateTime FileDate { get; set; }
    public bool LayoutVerified { get; set; }
    public string? VerificationNote { get; set; }
    public Dictionary<string, string> ColumnsRead { get; set; } = new();
    public int RowsRead { get; set; }
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Ended { get; set; }
    public int Removed { get; set; }
    /// <summary>Active rows ended because a full file no longer carried them.</summary>
    public int EndedNotOnFile { get; set; }
    public int Rejected { get; set; }
    public int Skipped { get; set; }
    public int UnknownMembers { get; set; }
    /// <summary>Plan-discovered rows the state file now carries (adopted by the state source).</summary>
    public int ConfirmedPlanDiscovered { get; set; }
    public int MembersWithTpl { get; set; }
    /// <summary>Coverage codes whose scope is CHO's reading; check them before relying on cost avoidance.</summary>
    public List<string> CodesNeedingVerification { get; set; } = new();
    /// <summary>First 200 rejected rows with the reason.</summary>
    public List<TplRowIssue> RejectedRows { get; set; } = new();
    /// <summary>First 200 skipped rows with the reason.</summary>
    public List<TplRowIssue> SkippedRows { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    /// <summary>Set when nothing could be loaded; nothing was written.</summary>
    public string? Error { get; set; }
}

public interface ITplImportService
{
    Task<TplImportReport> ImportAsync(TplImportRequest request, CancellationToken ct = default);
}

/// <summary>
/// Loads a state TPL file into <see cref="ITplPolicyRepository"/>.
///
/// Rows are matched to stored spans by <see cref="TplPolicy.NaturalKey"/>.
/// Rows for members the plan has never enrolled are rejected: the state's file
/// covers its whole Medicaid population, and a plan only holds its own members.
/// Nothing is ever deleted; ended and removed spans stay so a claim adjudicated
/// against them can be explained later.
/// </summary>
public sealed class TplImportService : ITplImportService
{
    public const string PlanDiscoveredSource = "plan-discovered";
    private const int ReportSampleSize = 200;

    /// <summary>A full file that would end more than this share of a
    /// source's active rows is refused unless the caller allows it: a
    /// truncated file must not wipe out a state's TPL picture.</summary>
    public const double LargeDropShare = 0.25;
    private const int LargeDropMinimumRows = 100;

    private readonly ITplPolicyRepository _policies;
    private readonly ICoverageRepository _coverage;
    private readonly ILogger<TplImportService> _logger;

    public TplImportService(ITplPolicyRepository policies, ICoverageRepository coverage, ILogger<TplImportService> logger)
    {
        _policies = policies;
        _coverage = coverage;
        _logger = logger;
    }

    public async Task<TplImportReport> ImportAsync(TplImportRequest request, CancellationToken ct = default)
    {
        var layout = request.Layout;
        var state = request.State.Trim().ToUpperInvariant();
        var fileDate = DateTime.SpecifyKind(request.FileDate.Date, DateTimeKind.Utc);
        var report = new TplImportReport
        {
            Layout = layout.Id,
            State = state,
            DryRun = request.DryRun,
            Mode = request.Mode,
            FileDate = fileDate,
            LayoutVerified = layout.Verified,
            VerificationNote = layout.VerificationNote,
        };
        if (!layout.Verified)
            report.Warnings.Add($"Layout '{layout.Id}' has not been checked against the state's record layout. {layout.VerificationNote}");

        var parsed = TplFileParser.Parse(request.Content, layout);
        report.ColumnsRead = parsed.ColumnsRead;
        report.CodesNeedingVerification = parsed.CodesNeedingVerification.ToList();
        report.Skipped = parsed.Skipped.Count;
        report.SkippedRows = parsed.Skipped.Take(ReportSampleSize).ToList();
        if (parsed.FileError is not null)
        {
            report.Error = parsed.FileError;
            return report;
        }

        var rejected = new List<TplRowIssue>(parsed.Rejected);
        var skipped = new List<TplRowIssue>();
        report.RowsRead = parsed.Rows.Count + parsed.Rejected.Count + parsed.Skipped.Count;

        var memberIds = parsed.Rows.Select(r => r.MemberId).Distinct(StringComparer.Ordinal).ToList();
        var known = await _coverage.GetKnownMemberIdsAsync(request.TenantId, memberIds, ct).ConfigureAwait(false);
        report.UnknownMembers = memberIds.Count(id => !known.Contains(id));

        var existing = await _policies.GetByMembersAsync(request.TenantId, state, known.ToList(), ct).ConfigureAwait(false);
        var byKey = existing.ToDictionary(p => p.NaturalKey, StringComparer.Ordinal);
        var changed = new Dictionary<string, TplPolicy>(StringComparer.Ordinal);
        var confirmedKeys = new HashSet<string>(StringComparer.Ordinal);
        var membersWithTpl = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in parsed.Rows)
        {
            if (!known.Contains(row.MemberId))
            {
                rejected.Add(new(row.LineNumber, "Member is not enrolled with this plan (no coverage row)."));
                continue;
            }

            if (row.MeansNoCoverage)
            {
                // The state says the member has no TPL from this date: end what
                // this source holds for them. Plan-discovered rows stay; the plan
                // reports those to the state separately.
                var noneFrom = row.EffectiveDate;
                var ended = 0;
                foreach (var p in existing.Where(p => p.MemberId == row.MemberId && p.Source == layout.Id
                                                      && p.Status == TplPolicyStatus.Active
                                                      && (p.EndDate is null || p.EndDate >= noneFrom)))
                {
                    var target = Track(p);
                    target.EndDate = noneFrom.AddDays(-1) < target.EffectiveDate ? target.EffectiveDate : noneFrom.AddDays(-1);
                    target.Status = TplPolicyStatus.Ended;
                    target.EndReason = $"State reported no other coverage from {noneFrom:yyyy-MM-dd} (line {row.LineNumber}).";
                    Stamp(target);
                    confirmedKeys.Add(target.NaturalKey);
                    ended++;
                }
                report.Ended += ended;
                continue;
            }

            var key = TplPolicy.BuildNaturalKey(row.MemberId, state, row.CarrierCode, row.CarrierName,
                row.PolicyNumber, row.EffectiveDate);
            confirmedKeys.Add(key);
            byKey.TryGetValue(key, out var current);
            if (changed.TryGetValue(key, out var alreadyChanged)) current = alreadyChanged;

            switch (row.Action)
            {
                case TplRecordAction.Remove:
                    if (current is null)
                    {
                        skipped.Add(new(row.LineNumber, "Remove for a policy CHO does not hold."));
                        continue;
                    }
                    var removed = Track(current);
                    removed.Status = TplPolicyStatus.Removed;
                    removed.EndReason = $"Removed by the state (line {row.LineNumber}).";
                    Stamp(removed);
                    report.Removed++;
                    continue;

                case TplRecordAction.End:
                    if (current is null)
                    {
                        skipped.Add(new(row.LineNumber, "End for a policy CHO does not hold."));
                        continue;
                    }
                    var ending = Track(current);
                    ending.EndDate = row.EndDate ?? fileDate;
                    ending.Status = TplPolicyStatus.Ended;
                    ending.EndReason = $"Ended by the state (line {row.LineNumber}).";
                    Stamp(ending);
                    report.Ended++;
                    continue;
            }

            var status = row.EndDate is { } end && end < fileDate ? TplPolicyStatus.Ended : TplPolicyStatus.Active;
            if (status == TplPolicyStatus.Active) membersWithTpl.Add(row.MemberId);

            if (current is null)
            {
                var created = new TplPolicy
                {
                    TenantId = request.TenantId,
                    MemberId = row.MemberId,
                    State = state,
                    Source = layout.Id,
                    NaturalKey = key,
                    FirstLoadedAt = DateTime.UtcNow,
                };
                Apply(created, row, status);
                Stamp(created);
                changed[key] = created;
                byKey[key] = created;
                report.Inserted++;
                continue;
            }

            if (current.Source == PlanDiscoveredSource)
            {
                // The state file now carries a policy the plan found itself:
                // the state owns it from here, and the add report is moot.
                var adopted = Track(current);
                Apply(adopted, row, status);
                adopted.Source = layout.Id;
                adopted.StateConfirmedAt = DateTime.UtcNow;
                if (adopted.PendingStateAction == TplStateAction.Add)
                {
                    adopted.PendingStateAction = null;
                    adopted.StateReportDueBy = null;
                }
                Stamp(adopted);
                report.ConfirmedPlanDiscovered++;
                continue;
            }

            if (Same(current, row, status))
            {
                var confirmed = Track(current);
                confirmed.LastConfirmedAt = DateTime.UtcNow;
                confirmed.SourceFileName = request.FileName;
                report.Unchanged++;
                continue;
            }

            var updated = Track(current);
            Apply(updated, row, status);
            Stamp(updated);
            report.Updated++;
        }

        if (request.Mode == TplImportMode.Full)
        {
            var active = await _policies.GetActiveBySourceAsync(request.TenantId, state, layout.Id, ct).ConfigureAwait(false);
            var notOnFile = active.Where(p => !confirmedKeys.Contains(p.NaturalKey)).ToList();
            if (!request.AllowLargeDrop && notOnFile.Count >= LargeDropMinimumRows
                                        && notOnFile.Count > active.Count * LargeDropShare)
            {
                report.Error =
                    $"This full file would end {notOnFile.Count} of {active.Count} active {layout.Id} policies " +
                    $"(more than {LargeDropShare:P0}). That usually means a truncated or wrong file. Nothing was written; " +
                    "re-run with allowLargeDrop=true if the drop is real.";
                Finish(report, rejected, skipped, membersWithTpl);
                return report;
            }

            var dayBefore = fileDate.AddDays(-1);
            foreach (var p in notOnFile)
            {
                var target = Track(p);
                target.EndDate = dayBefore < target.EffectiveDate ? target.EffectiveDate : dayBefore;
                target.Status = TplPolicyStatus.Ended;
                target.EndReason = $"Not on the full {layout.Id} file of {fileDate:yyyy-MM-dd}.";
                Stamp(target);
                report.EndedNotOnFile++;
            }
        }

        Finish(report, rejected, skipped, membersWithTpl);

        if (!request.DryRun && changed.Count > 0)
        {
            await _policies.UpsertManyAsync(changed.Values.ToList(), ct).ConfigureAwait(false);
            _logger.LogInformation(
                "TPL import {Layout} for {State}: {Inserted} inserted, {Updated} updated, {Ended} ended, {NotOnFile} ended not on file, {Removed} removed, {Rejected} rejected",
                LogSanitizer.Sanitize(layout.Id), LogSanitizer.Sanitize(state), report.Inserted, report.Updated, report.Ended, report.EndedNotOnFile, report.Removed, report.Rejected);
        }
        return report;

        // Copy-on-write so a dry run never mutates the rows it read.
        TplPolicy Track(TplPolicy p)
        {
            if (changed.TryGetValue(p.NaturalKey, out var copy)) return copy;
            copy = Clone(p);
            changed[p.NaturalKey] = copy;
            byKey[p.NaturalKey] = copy;
            return copy;
        }

        void Stamp(TplPolicy p)
        {
            p.LastConfirmedAt = DateTime.UtcNow;
            p.LastUpdatedBy = request.Actor;
            p.SourceFileName = request.FileName;
        }
    }

    private static void Finish(TplImportReport report, List<TplRowIssue> rejected, List<TplRowIssue> skipped, HashSet<string> members)
    {
        report.Rejected = rejected.Count;
        report.RejectedRows = rejected.OrderBy(r => r.LineNumber).Take(ReportSampleSize).ToList();
        report.Skipped += skipped.Count;
        report.SkippedRows = report.SkippedRows.Concat(skipped).OrderBy(r => r.LineNumber).Take(ReportSampleSize).ToList();
        report.MembersWithTpl = members.Count;
    }

    private static void Apply(TplPolicy p, TplFileRow row, TplPolicyStatus status)
    {
        p.CarrierCode = row.CarrierCode;
        p.CarrierName = row.CarrierName;
        p.PolicyNumber = row.PolicyNumber;
        p.GroupNumber = row.GroupNumber;
        p.PolicyholderName = row.PolicyholderName;
        p.StateCoverageCode = row.StateCoverageCode;
        p.Scope = row.Scope;
        p.PayAndChaseOnly = row.PayAndChaseOnly;
        p.EffectiveDate = row.EffectiveDate;
        p.EndDate = row.EndDate;
        p.Status = status;
        p.EndReason = status == TplPolicyStatus.Ended ? "End date on the state file." : null;
    }

    private static bool Same(TplPolicy p, TplFileRow row, TplPolicyStatus status) =>
        p.Status == status
        && p.CarrierCode == row.CarrierCode
        && p.CarrierName == row.CarrierName
        && p.GroupNumber == row.GroupNumber
        && p.PolicyholderName == row.PolicyholderName
        && p.StateCoverageCode == row.StateCoverageCode
        && p.Scope == row.Scope
        && p.PayAndChaseOnly == row.PayAndChaseOnly
        && p.EndDate == row.EndDate;

    private static TplPolicy Clone(TplPolicy p) => new()
    {
        Id = p.Id,
        TenantId = p.TenantId,
        MemberId = p.MemberId,
        State = p.State,
        Source = p.Source,
        CarrierCode = p.CarrierCode,
        CarrierName = p.CarrierName,
        PolicyNumber = p.PolicyNumber,
        GroupNumber = p.GroupNumber,
        PolicyholderName = p.PolicyholderName,
        StateCoverageCode = p.StateCoverageCode,
        Scope = p.Scope,
        PayAndChaseOnly = p.PayAndChaseOnly,
        EffectiveDate = p.EffectiveDate,
        EndDate = p.EndDate,
        Status = p.Status,
        EndReason = p.EndReason,
        SourceFileName = p.SourceFileName,
        FirstLoadedAt = p.FirstLoadedAt,
        LastConfirmedAt = p.LastConfirmedAt,
        LastUpdatedBy = p.LastUpdatedBy,
        NaturalKey = p.NaturalKey,
        DiscoveredAt = p.DiscoveredAt,
        PendingStateAction = p.PendingStateAction,
        PendingStateActionReason = p.PendingStateActionReason,
        StateReportDueBy = p.StateReportDueBy,
        ReportedToStateAt = p.ReportedToStateAt,
        StateReportReference = p.StateReportReference,
        StateConfirmedAt = p.StateConfirmedAt,
    };
}
