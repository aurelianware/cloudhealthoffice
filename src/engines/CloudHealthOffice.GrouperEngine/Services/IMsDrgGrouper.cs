using CloudHealthOffice.GrouperEngine.Models;

namespace CloudHealthOffice.GrouperEngine.Services;

/// <summary>
/// Assigns an MS-DRG to an inpatient stay.
///
/// <para>
/// The seam described in ADR 024. Three implementations are intended to
/// satisfy it interchangeably: the platform's own table-driven grouper
/// (<see cref="TableDrivenMsDrgGrouper"/>), a client for the CMS reference
/// grouper jar run as a sidecar, and an adapter for a licensed grouper. A
/// caller must not care which is bound.
/// </para>
/// </summary>
public interface IMsDrgGrouper
{
    /// <summary>
    /// Groups one stay. Never throws for claim-data problems: an input the
    /// grouper cannot use comes back as a <see cref="MsDrgGroupingResult"/>
    /// whose status says what was wrong, because adjudication has to pend
    /// the claim with a reason rather than fail.
    /// </summary>
    Task<MsDrgGroupingResult> GroupAsync(MsDrgGroupingRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Supplies the grouper's table set for a discharge date. Implemented in
/// this assembly against in-memory tables, and (next phase) against Mongo
/// for tables imported from the CMS definitions manual.
/// </summary>
public interface IMsDrgTableProvider
{
    /// <summary>
    /// The table set in force on <paramref name="dischargeDate"/>, or null
    /// when no version covers it.
    /// </summary>
    Task<MsDrgTableSet?> GetForDischargeDateAsync(DateOnly dischargeDate, CancellationToken cancellationToken = default);
}
