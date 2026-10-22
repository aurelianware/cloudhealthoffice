using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Models;

namespace CloudHealthOffice.GrouperEngine.Services;

/// <summary>
/// Reports the DRG the facility billed, and groups nothing.
///
/// <para>
/// This is not a placeholder. Paying the billed DRG is what the platform did
/// before it had a grouper of its own, and it stays the default: a plan keeps
/// being paid exactly as it was until someone deliberately points it at a
/// grouper. That makes installing the seam a change with no payment
/// consequence, which is the only way to install one safely.
/// </para>
///
/// <para>
/// It is also the honest implementation for a payer that has contracted to
/// accept the facility's own grouping, which some do. Bound deliberately, it
/// is correct; bound by accident, it is what already happens.
/// </para>
/// </summary>
public sealed class BilledDrgGrouper : IMsDrgGrouper
{
    /// <summary>The version string reported for an answer that used no tables.</summary>
    public const string NoVersion = "none";

    /// <inheritdoc />
    public Task<MsDrgGroupingResult> GroupAsync(
        MsDrgGroupingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var billed = request.BilledDrg?.Trim();
        if (string.IsNullOrEmpty(billed))
        {
            return Task.FromResult(MsDrgGroupingResult.NotGrouped(
                GroupingStatus.NoBilledDrg,
                NoVersion,
                request.BilledDrg,
                "No grouper is configured and the claim carries no billed DRG, so there is no DRG to pay."));
        }

        return Task.FromResult(new MsDrgGroupingResult
        {
            Status = GroupingStatus.Grouped,
            Drg = billed,
            Source = DrgSource.BilledByProvider,
            GrouperVersion = NoVersion,
            BilledDrg = billed,
            Notes = ["The DRG was taken from the claim. No grouping was performed."]
        });
    }
}
