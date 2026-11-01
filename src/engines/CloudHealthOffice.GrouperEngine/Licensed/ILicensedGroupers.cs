namespace CloudHealthOffice.GrouperEngine.Licensed;

/// <summary>
/// Classifies an inpatient stay by APR-DRG and severity of illness, through
/// the plan's own licensed grouper.
///
/// <para>
/// CHO does not and will not implement APR-DRG: the logic and its tables
/// are 3M/Solventum's and licensed per user. The implementations are
/// <see cref="NotConfiguredLicensedGrouper"/>, which answers that nothing
/// is configured, and <see cref="HttpLicensedGrouper"/>, which asks a
/// grouper service the plan runs under its own licence through the
/// contract in docs/integration/LICENSED-GROUPER-CONTRACT.md.
/// </para>
/// </summary>
public interface IAprDrgGrouper
{
    /// <summary>
    /// Groups one stay. Never throws for claim data or transport problems:
    /// the result's status says what went wrong so adjudication can pend with
    /// a reason. Cancellation is the only exception that propagates.
    /// </summary>
    Task<AprDrgGroupingResult> GroupAsync(AprDrgGroupingRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Classifies an outpatient hospital claim's lines by EAPG, through the
/// plan's own licensed grouper. Same contract as <see cref="IAprDrgGrouper"/>.
/// </summary>
public interface IEapgGrouper
{
    /// <summary>
    /// Groups one claim. Never throws for claim data or transport problems.
    /// </summary>
    Task<EapgGroupingResult> GroupAsync(EapgGroupingRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// The default when a deployment has no licensed grouper: every request
/// answers <see cref="LicensedGroupingStatus.NotConfigured"/>.
///
/// <para>
/// Deliberately not a billed-group fallback like
/// <see cref="Services.BilledDrgGrouper"/>. Texas and California group
/// Medicaid stays themselves and do not require the hospital to bill an
/// APR-DRG, so there is often no billed group to fall back on, and paying a
/// hospital's own severity level is exactly the exposure an APR-DRG method
/// exists to remove. Nothing reads this until a plan turns an APR-DRG or
/// EAPG method on, and then a missing grouper should pend the claim loudly.
/// </para>
/// </summary>
public sealed class NotConfiguredLicensedGrouper : IAprDrgGrouper, IEapgGrouper
{
    internal const string Note =
        "No licensed grouper is configured on this deployment (LicensedGrouper:BaseUrl is empty).";

    /// <inheritdoc />
    public Task<AprDrgGroupingResult> GroupAsync(
        AprDrgGroupingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(AprDrgGroupingResult.NotGrouped(
            LicensedGroupingStatus.NotConfigured, request.BilledDrg, Note));
    }

    /// <inheritdoc />
    public Task<EapgGroupingResult> GroupAsync(
        EapgGroupingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(EapgGroupingResult.NotGrouped(LicensedGroupingStatus.NotConfigured, Note));
    }
}
