using ClaimsService.Services.Adjudication;
using CloudHealthOffice.Infrastructure.Logging;
using Microsoft.Extensions.Options;

namespace ClaimsService.Import.Cpcds.Drop;

/// <summary>
/// Loads what is waiting in one tenant's drop, in the order the data depends
/// on: members and coverage first (so claims find their members and CHO
/// coverage), then for each claim file the providers it names, then the
/// claims. Every import is idempotent, so a file dropped twice, or a run
/// retried after a failure, writes nothing it already wrote.
/// </summary>
public sealed class CpcdsDropRunner
{
    public const string Actor = "cpcds-drop";

    private readonly ICpcdsDropStore _store;
    private readonly ICpcdsClaimImportService _claims;
    private readonly ICpcdsUpstreamImports _upstream;
    private readonly ICpcdsDropArchive? _archive;
    private readonly ICpcdsDropRunRepository _runs;
    private readonly IEnumerable<ICpcdsImportBatchCompletedHandler> _handlers;
    private readonly IAdjudicationTenantContext _tenantContext;
    private readonly CpcdsDropOptions _options;
    private readonly CpcdsDropTenants _tenants;
    private readonly ILogger<CpcdsDropRunner> _logger;
    private readonly TimeProvider _clock;

    public CpcdsDropRunner(
        ICpcdsDropStore store,
        ICpcdsClaimImportService claims,
        ICpcdsUpstreamImports upstream,
        ICpcdsDropRunRepository runs,
        IEnumerable<ICpcdsImportBatchCompletedHandler> handlers,
        IAdjudicationTenantContext tenantContext,
        IOptions<CpcdsDropOptions> options,
        CpcdsDropTenants tenants,
        ILogger<CpcdsDropRunner> logger,
        TimeProvider? clock = null,
        ICpcdsDropArchive? archive = null)
    {
        _archive = archive;
        _store = store;
        _claims = claims;
        _upstream = upstream;
        _runs = runs;
        _handlers = handlers;
        _tenantContext = tenantContext;
        _options = options.Value;
        _tenants = tenants;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The run, or null when the tenant has no drop configured or nothing is ready.</summary>
    public async Task<CpcdsDropRun?> RunAsync(string tenantId, CancellationToken ct)
    {
        if (await _tenants.ResolveAsync(tenantId, ct) is not { } tenant)
            return null;

        var now = _clock.GetUtcNow().UtcDateTime;
        var ready = await _store.ListReadyAsync(tenantId, _options.QuietPeriod, now, ct);
        var notes = new List<string>();

        var members = ready.FirstOrDefault(f => CpcdsDropFileKinds.Classify(f.Name) == CpcdsDropFileKind.Members);
        var coverages = members is null
            ? null
            : ready.FirstOrDefault(f => CpcdsDropFileKinds.Classify(f.Name) == CpcdsDropFileKind.Coverages);
        var claimFiles = ready.Where(f => CpcdsDropFileKinds.Classify(f.Name) == CpcdsDropFileKind.Claims).ToList();

        foreach (var file in ready)
        {
            var kind = CpcdsDropFileKinds.Classify(file.Name);
            if (kind == CpcdsDropFileKind.Unknown)
                notes.Add($"{file.Name} left in inbound: the name says neither members, coverage nor claims.");
            else if (kind == CpcdsDropFileKind.Coverages && members is null)
                notes.Add($"{file.Name} left in inbound: a coverage file loads with its member file, and none is waiting.");
            else if ((kind == CpcdsDropFileKind.Members && file != members) || (kind == CpcdsDropFileKind.Coverages && file != coverages))
                notes.Add($"{file.Name} left in inbound for the next run: one member and coverage pair loads per run.");
        }

        var picked = new[] { members, coverages }.OfType<CpcdsDropFile>().Concat(claimFiles).ToList();
        if (picked.Count == 0)
            return null;

        var run = new CpcdsDropRun
        {
            Id = $"{now:yyyyMMdd'T'HHmmss'Z'}-{Guid.NewGuid():N}"[..25],
            TenantId = tenantId,
            SourceSystem = tenant.SourceSystem,
            StartedAt = now,
            Notes = notes,
        };

        // Take the files. Another pod may have taken some between the listing and here.
        var taken = new Dictionary<CpcdsDropFile, CpcdsDropFile>();
        foreach (var file in picked)
        {
            if (await _store.ClaimAsync(tenantId, run.Id, file, ct) is { } claimed)
                taken[file] = claimed;
        }
        if (taken.Count == 0)
            return null;

        run.Files = taken.Values.Select(f => new CpcdsDropRunFile
        {
            Name = f.Name, Kind = CpcdsDropFileKinds.Classify(f.Name), Length = f.Length, LastWriteUtc = f.LastWriteUtc,
        }).ToList();
        run.NewestFileWrittenAt = taken.Values.Max(f => f.LastWriteUtc);
        await _runs.SaveAsync(run, ct);
        _tenantContext.TenantId = tenantId;

        _logger.LogInformation("CPCDS drop run {RunId} for tenant {TenantId}: {Files} files",
            run.Id, LogSanitizer.Sanitize(tenantId), taken.Count);

        var batches = new List<CpcdsImportBatchCompleted>();
        try
        {
            if (_options.Archive.Enabled && _archive is not null)
                run.Steps.Add(await ArchiveAsync(tenantId, run, tenant, taken.Values, ct));

            if (members is not null && taken.TryGetValue(members, out var membersFile))
            {
                var membersContent = await _store.ReadAllTextAsync(membersFile, ct);
                CpcdsDropFile? coveragesFile = null;
                string? coveragesContent = null;
                if (coverages is not null && taken.TryGetValue(coverages, out var c))
                {
                    coveragesFile = c;
                    coveragesContent = await _store.ReadAllTextAsync(c, ct);
                }
                run.Steps.Add(await _upstream.ImportMembersAsync(tenantId, membersFile, membersContent, coveragesFile, coveragesContent, ct));
            }

            foreach (var original in claimFiles)
            {
                if (!taken.TryGetValue(original, out var file)) continue;
                var content = await _store.ReadAllTextAsync(file, ct);

                run.Steps.Add(await _upstream.ImportClaimProvidersAsync(tenantId, file, content, tenant.ColumnMap, ct));

                var (step, batch) = await ImportClaimsAsync(tenantId, run.Id, tenant, file, content, ct);
                run.Steps.Add(step);
                if (batch is not null) batches.Add(batch);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A file that cannot be read, or a fault outside the imports' own
            // handling: the run fails, and its files go to failed/ rather than
            // staying in processing/.
            _logger.LogError(ex, "CPCDS drop run {RunId} stopped", run.Id);
            run.Steps.Add(new CpcdsDropStep
            {
                Name = "run",
                Status = CpcdsDropStepStatus.Failed,
                Errors = [$"The run stopped: {ex.GetType().Name}."],
            });
        }

        run.Status = Overall(run.Steps);
        run.ArchivedTo = await _store.CompleteAsync(
            tenantId, run.Id, succeeded: run.Steps.All(s => s.Status != CpcdsDropStepStatus.Failed), CancellationToken.None);
        run.CompletedAt = _clock.GetUtcNow().UtcDateTime;
        await _runs.SaveAsync(run, ct);

        foreach (var batch in batches)
        {
            foreach (var handler in _handlers)
            {
                try
                {
                    if (await handler.OnBatchImportedAsync(batch, ct) is { } note)
                        run.Notes.Add(note);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "After-import step {Handler} failed for drop run {RunId}", handler.GetType().Name, run.Id);
                    run.Notes.Add($"{handler.GetType().Name} failed after {batch.FileName}: {ex.GetType().Name}.");
                }
            }
        }
        if (batches.Count > 0)
            await _runs.SaveAsync(run, ct);

        _logger.LogInformation("CPCDS drop run {RunId} for tenant {TenantId} finished {Status}",
            run.Id, LogSanitizer.Sanitize(tenantId), run.Status);
        return run;
    }

    /// <summary>
    /// Keeps each file, as received, in document-service before anything loads
    /// from it. A file that can't be archived still loads: the step reports it
    /// (and the run's status shows errors), but it never fails the run, so a
    /// document-service outage doesn't hold up the plan's data.
    /// </summary>
    private async Task<CpcdsDropStep> ArchiveAsync(
        string tenantId, CpcdsDropRun run, CpcdsDropTenantOptions tenant, IEnumerable<CpcdsDropFile> files, CancellationToken ct)
    {
        var step = new CpcdsDropStep { Name = "archive" };
        var archived = 0;
        foreach (var file in files)
        {
            step.Files.Add(file.Name);
            var content = await _store.ReadAllTextAsync(file, ct);
            var result = await _archive!.ArchiveAsync(tenantId, run.Id, file, content, tenant.SourceSystem, ct);
            if (result.DocumentId is { } documentId)
            {
                archived++;
                if (run.Files.FirstOrDefault(f => f.Name == file.Name) is { } record)
                    record.DocumentId = documentId;
            }
            else
            {
                step.Errors.Add($"{file.Name} was not archived: {result.Error}");
            }
        }
        step.Counts["archived"] = archived;
        step.Counts["notArchived"] = step.Files.Count - archived;
        step.Status = step.Errors.Count == 0 ? CpcdsDropStepStatus.Succeeded : CpcdsDropStepStatus.CompletedWithErrors;
        return step;
    }

    private async Task<(CpcdsDropStep Step, CpcdsImportBatchCompleted? Batch)> ImportClaimsAsync(
        string tenantId, string runId, CpcdsDropTenantOptions tenant, CpcdsDropFile file, string content, CancellationToken ct)
    {
        var step = new CpcdsDropStep { Name = "claims", Files = [file.Name] };
        try
        {
            var result = await _claims.ImportAsync(tenantId, content, new CpcdsImportOptions
            {
                SourceSystem = tenant.SourceSystem,
                FileName = file.Name,
                ExtractedAt = file.LastWriteUtc,
                LineOfBusiness = tenant.LineOfBusiness,
                ColumnMap = tenant.ColumnMap,
            }, Actor, ct);

            step.ImportBatchId = result.BatchId;
            step.Counts["rowsRead"] = result.RowsRead;
            step.Counts["claimsInFile"] = result.ClaimsInFile;
            step.Counts["created"] = result.Created;
            step.Counts["updated"] = result.Updated;
            step.Counts["unchanged"] = result.Unchanged;
            step.Counts["rejected"] = result.Rejected;
            step.Counts["submittedAmountInFile"] = result.SubmittedAmountInFile;
            step.Counts["paidAmountInFile"] = result.PaidAmountInFile;
            step.Counts["paidAmountLoaded"] = result.PaidAmountLoaded;
            step.Errors.AddRange(result.FileErrors);
            foreach (var claim in result.Claims.Where(c => c.Outcome == CpcdsClaimOutcome.Rejected))
            {
                if (step.Errors.Count >= HttpCpcdsUpstreamImports.MaxErrorsKept) break;
                step.Errors.Add($"{claim.ClaimId}: {string.Join(" ", claim.Errors)}");
            }

            step.Status = result.ClaimsInFile == 0 && result.FileErrors.Count > 0 ? CpcdsDropStepStatus.Failed
                : step.Errors.Count > 0 ? CpcdsDropStepStatus.CompletedWithErrors
                : CpcdsDropStepStatus.Succeeded;

            var batch = step.Status == CpcdsDropStepStatus.Failed
                ? null
                : new CpcdsImportBatchCompleted(tenantId, runId, result.BatchId, tenant.SourceSystem, file.Name,
                    result.Created, result.Updated, result.Unchanged, result.Rejected);
            return (step, batch);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "CPCDS claim import failed in drop run {RunId}", runId);
            step.Status = CpcdsDropStepStatus.Failed;
            step.Errors.Add($"The claim import failed: {ex.GetType().Name}.");
            return (step, null);
        }
    }

    internal static CpcdsDropRunStatus Overall(IReadOnlyCollection<CpcdsDropStep> steps)
    {
        if (steps.Count > 0 && steps.All(s => s.Status == CpcdsDropStepStatus.Failed)) return CpcdsDropRunStatus.Failed;
        return steps.All(s => s.Status == CpcdsDropStepStatus.Succeeded)
            ? CpcdsDropRunStatus.Succeeded
            : CpcdsDropRunStatus.CompletedWithErrors;
    }
}
