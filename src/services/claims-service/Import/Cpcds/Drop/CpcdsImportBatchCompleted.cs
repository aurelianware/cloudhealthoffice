using ClaimsService.Services.Adjudication.Shadow;

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
    private readonly CpcdsDropTenants _tenants;
    private readonly ShadowAdjudicationTenants _shadowTenants;
    private readonly ShadowAdjudicationOptions _shadowOptions;

    public ShadowAdjudicateAfterImport(
        IShadowAdjudicationService shadow,
        CpcdsDropTenants tenants,
        ShadowAdjudicationTenants shadowTenants)
    {
        _shadow = shadow;
        _tenants = tenants;
        _shadowTenants = shadowTenants;
        _shadowOptions = shadowTenants.Options;
    }

    public async Task<string?> OnBatchImportedAsync(CpcdsImportBatchCompleted batch, CancellationToken ct)
    {
        if (await _tenants.ResolveAsync(batch.TenantId, ct) is not { ShadowAfterImport: true })
            return null;
        if (!await _shadowTenants.IsEnabledForAsync(batch.TenantId, ct))
            return _shadowOptions.Enabled
                ? "Shadow adjudication after import is on for this tenant's drop, but shadow adjudication is off for the tenant; skipped. Turn it on in the portal (Shadow adjudication)."
                : $"Shadow adjudication after import is on for this tenant's drop, but {ShadowAdjudicationOptions.SectionName}:Enabled is off in this deployment; skipped.";
        if (batch.Created + batch.Updated + batch.Unchanged == 0)
            return null;

        // The whole batch, in the background: a large claim file no longer
        // stops at MaxClaimsPerRun, and the drop run does not wait for it.
        var loaded = batch.Created + batch.Updated + batch.Unchanged;
        var run = await _shadow.QueueAsync(
            batch.TenantId, new ShadowAdjudicationRunRequest { ImportBatchId = batch.ImportBatchId, Background = true },
            "cpcds-drop", ct);
        var note = $"Shadow adjudication run {run.Id} queued for {batch.FileName}: {loaded} claims.";
        return loaded > run.ClaimsRequested
            ? note + $" Only the first {run.ClaimsRequested} will be compared (MaxClaimsPerBackgroundRun)."
            : note;
    }
}
