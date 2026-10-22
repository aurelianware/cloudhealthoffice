using CloudHealthOffice.GrouperEngine.Formulas;
using CloudHealthOffice.GrouperEngine.Grouping;
using CloudHealthOffice.GrouperEngine.Hac;
using CloudHealthOffice.GrouperEngine.Mask;
using CloudHealthOffice.GrouperEngine.Models;
using CloudHealthOffice.GrouperEngine.Severity;

namespace CloudHealthOffice.GrouperEngine.Services;

/// <summary>
/// One CMS release's tables, as the formula grouper needs them.
/// </summary>
/// <param name="Version">The grouper version, e.g. <c>V440</c>.</param>
/// <param name="Codes">The code-to-operand tables.</param>
/// <param name="Clusters">The procedure cluster definitions.</param>
/// <param name="Formulas">The DRG logic table.</param>
/// <param name="Exclusions">The CC exclusion lists.</param>
/// <param name="Hacs">The hospital-acquired-condition tables.</param>
public sealed record MsDrgRelease(
    string Version,
    IMsDrgCodeAttributeSource Codes,
    IMsDrgClusterSource Clusters,
    IMsDrgFormulaSource Formulas,
    IMsDrgExclusionSource Exclusions,
    IMsDrgHacSource Hacs);

/// <summary>
/// What the formula grouper made of a stay.
/// </summary>
/// <param name="Version">The release used.</param>
/// <param name="Grouping">
/// The two groupings and the HAC determination, or <see langword="null"/> when
/// the claim was refused before grouping.
/// </param>
/// <param name="UnrecognisedCodes">
/// Codes the release's tables do not hold, in the order they appeared. The
/// stay is still grouped: an unrecognised secondary diagnosis or procedure
/// contributes nothing to the mask, which is what CMS does with it, and an
/// unrecognised principal raises the <c>invalid_pdx</c> edit and refuses the
/// claim through the tables rather than ahead of them. They are reported
/// because a caller may well want to reject the claim back to the biller — but
/// that is the caller's policy, not the grouper's answer.
/// </param>
/// <param name="Refusal">
/// Why the claim was not grouped, or <see langword="null"/>.
/// </param>
public sealed record MsDrgFormulaGroupingResult(
    string Version,
    MsDrgPaidGrouping? Grouping,
    IReadOnlyList<string> UnrecognisedCodes,
    string? Refusal)
{
    /// <summary>The DRG the stay is paid, or null when it was refused.</summary>
    public int? PaidDrg => Grouping?.AfterHacPolicy.Groupable == true
        ? Grouping.AfterHacPolicy.Drg
        : null;
}

/// <summary>
/// Drives the whole formula engine from one stay: mask, codes, CC exclusions,
/// the HAC policy, and both grouping passes.
///
/// <para>
/// This is the composition root the parity run and the pricing path both want,
/// and it is deliberately thin — every decision it makes is about which table
/// to ask, never about what the answer is.
/// </para>
///
/// <para>
/// Callers reach it through <see cref="FormulaMsDrgGrouper"/>, which puts it
/// behind <see cref="IMsDrgGrouper"/> — the Pricing API asks that seam, not
/// this class. <c>PricingStage</c> is still to be wired (ADR 024 step 8), and
/// that is the half that changes adjudicated payments, so a plan keeps paying
/// the billed DRG there until it has been through parity testing of its own.
/// </para>
/// </summary>
public sealed class MsDrgFormulaGrouper
{
    private readonly MsDrgRelease release;
    private readonly MsDrgMaskBuilder masks;

    /// <summary>Creates a grouper over one release.</summary>
    public MsDrgFormulaGrouper(MsDrgRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        this.release = release;
        masks = new MsDrgMaskBuilder(release.Codes, release.Clusters);
    }

    /// <summary>Groups a stay.</summary>
    public MsDrgFormulaGroupingResult Group(MsDrgGroupingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var claim = masks.BuildClaim(request);

        // An unrecognised code is NOT a refusal here, and an earlier revision
        // of this class had it the other way round. The parity run is what
        // settled it: CMS groups a stay carrying an unknown secondary
        // diagnosis or procedure as though the code were absent, and refuses
        // an unknown principal through its own invalid_pdx edit rather than
        // ahead of grouping. Refusing here disagreed with CMS on exactly those
        // claims, which is the wrong direction to be strict in -- the payer's
        // answer is the one that gets paid. The codes are reported instead.
        var principal = claim.Codes.Diagnoses.Count > 0
            ? claim.Codes.Diagnoses[0].Code
            : null;

        if (principal is null)
        {
            // Nothing to group under and no edit to raise: CMS's own chain
            // needs a principal diagnosis to exist before any of it runs.
            return new MsDrgFormulaGroupingResult(
                release.Version,
                Grouping: null,
                claim.UnrecognisedCodes,
                "The claim carries no principal diagnosis.");
        }

        var secondaries = request.SecondaryDiagnoses
            .Select((diagnosis, index) => new MsDrgHacSecondary(
                claim.Codes.Diagnoses[index + 1].Code,
                ParsePoa(diagnosis.PresentOnAdmission)))
            .ToList();

        var stay = new MsDrgHacStay(
            claim.Mask,
            claim.Codes,
            release.Exclusions.ExclusionsFor(principal),
            PatientDied(request.DischargeStatus),
            secondaries,
            request.PoaReporting);

        return new MsDrgFormulaGroupingResult(
            release.Version,
            MsDrgHacAwareGrouper.Group(release.Formulas, release.Hacs, stay),
            claim.UnrecognisedCodes,
            Refusal: null);
    }

    /// <summary>
    /// UB-04's present-on-admission indicator. Anything that is not one of
    /// CMS's six is <see cref="PresentOnAdmission.Invalid"/>, which is not the
    /// same as nothing reported and does not behave the same way in the HAC
    /// rules.
    /// </summary>
    public static PresentOnAdmission ParsePoa(string? indicator) =>
        indicator?.Trim().ToUpperInvariant() switch
        {
            "Y" => PresentOnAdmission.Yes,
            "N" => PresentOnAdmission.No,
            "U" => PresentOnAdmission.Unknown,
            "W" => PresentOnAdmission.ClinicallyUndetermined,
            "1" => PresentOnAdmission.ExemptOne,
            "E" => PresentOnAdmission.ExemptE,
            null or "" => PresentOnAdmission.Blank,
            _ => PresentOnAdmission.Invalid,
        };

    /// <summary>
    /// Whether the discharge status says the patient died. 20 is CMS's, and
    /// the hospice statuses are not: a stay that ended in hospice is not a
    /// death on this claim.
    /// </summary>
    public static bool PatientDied(string? dischargeStatus) =>
        int.TryParse(dischargeStatus?.Trim(), out var status) && status == 20;
}
