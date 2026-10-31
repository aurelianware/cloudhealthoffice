using ClaimsService.Services.Adjudication.Shadow;
using Microsoft.Extensions.Options;

namespace ClaimsService.Import.Cpcds.Drop;

/// <summary>A CPCDS claim file a drop run loaded.</summary>
public sealed record CpcdsImportBatchCompleted(
    string TenantId,
    string DropRunId,
    string ImportBatchId,
    string? SourceSystem,
    string FileName,
    int Created,
    int Updated,
    int Unchanged,
    int Rejected);

/// <summary>
/// Runs after a drop run loads a claim file, in the drop run's scope (tenant
/// set). What a handler returns is kept as a note on the run. A handler that
/// throws does not fail the run.
/// </summary>
public interface ICpcdsImportBatchCompletedHandler
{
    Task<string?> OnBatchImportedAsync(CpcdsImportBatchCompleted batch, CancellationToken ct);
}

/// <summary>
/// Shadow-adjudicates each loaded batch for tenants that ask for it
/// (<see cref="CpcdsDropTenantOptions.ShadowAfterImport"/>) and have shadow
/// adjudication enabled. One run covers at most
/// <see cref="ShadowAdjudicationOptions.MaxClaimsPerRun"/> claims of the batch.
/// </summary>
public sealed class ShadowAdjudicateAfterImport : ICpcdsImportBatchCompletedHandler
{
    private readonly IShadowAdjudicationService _shadow;
    private readonly CpcdsDropOptions _drop;
    private readonly ShadowAdjudicationOptions _shadowOptions;

    public ShadowAdjudicateAfterImport(
        IShadowAdjudicationService shadow,
        IOptions<CpcdsDropOptions> drop,
        IOptions<ShadowAdjudicationOptions> shadowOptions)
    {
        _shadow = shadow;
        _drop = drop.Value;
        _shadowOptions = shadowOptions.Value;
    }

    public async Task<string?> OnBatchImportedAsync(CpcdsImportBatchCompleted batch, CancellationToken ct)
    {
        if (!_drop.Tenants.TryGetValue(batch.TenantId, out var tenant) || !tenant.ShadowAfterImport)
            return null;
        if (!_shadowOptions.IsEnabledFor(batch.TenantId))
            return $"Shadow adjudication after import is on for this tenant but {ShadowAdjudicationOptions.SectionName} is not enabled for it; skipped.";
        if (batch.Created + batch.Updated + batch.Unchanged == 0)
            return null;

        var loaded = batch.Created + batch.Updated + batch.Unchanged;
        var response = await _shadow.RunAsync(
            batch.TenantId, new ShadowAdjudicationRunRequest { ImportBatchId = batch.ImportBatchId }, "cpcds-drop", ct);
        var run = response.Run;
        var note = $"Shadow adjudication run {run.Id} for {batch.FileName}: {run.ClaimsRequested} of {loaded} claims, {run.ClaimsCompared} compared.";
        // One run takes at most MaxClaimsPerRun claims of the batch; say so, so nobody reads it as the whole file.
        return run.ClaimsRequested < loaded
            ? note + $" Partial: a run covers at most {_shadowOptions.MaxClaimsPerRun} claims."
            : note;
    }
}
