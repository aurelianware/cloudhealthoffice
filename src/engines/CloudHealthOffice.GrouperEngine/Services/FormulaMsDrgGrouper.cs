using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Formulas;
using CloudHealthOffice.GrouperEngine.Grouping;
using CloudHealthOffice.GrouperEngine.Hac;
using CloudHealthOffice.GrouperEngine.Models;

namespace CloudHealthOffice.GrouperEngine.Services;

/// <summary>
/// Supplies the CMS release in force for a discharge date.
///
/// <para>
/// Separate from <see cref="IMsDrgTableProvider"/> because the two carry
/// different things: that one hands over the hand-built table set the earlier
/// grouper walks, this one hands over a whole imported CMS release. A
/// deployment holding no release has no implementation to register, and that
/// is the state every deployment starts in.
/// </para>
/// </summary>
public interface IMsDrgReleaseProvider
{
    /// <summary>
    /// The release covering <paramref name="dischargeDate"/>, or null when none
    /// does. A stay is grouped by the tables in force on the date of discharge,
    /// so a release that has not started yet must not answer for it.
    /// </summary>
    Task<MsDrgRelease?> GetForDischargeDateAsync(
        DateOnly dischargeDate, CancellationToken cancellationToken = default);
}

/// <summary>
/// One release and the discharge dates it covers.
/// </summary>
/// <param name="Release">The imported tables.</param>
/// <param name="FirstDischargeDate">
/// The first discharge date this release prices, normally 1 October.
/// </param>
/// <param name="LastDischargeDate">
/// The last discharge date it prices, or null while it is the current one.
/// </param>
public sealed record MsDrgReleaseWindow(
    MsDrgRelease Release,
    DateOnly FirstDischargeDate,
    DateOnly? LastDischargeDate)
{
    /// <summary>Whether this release prices a stay discharged on the date.</summary>
    public bool Covers(DateOnly dischargeDate) =>
        dischargeDate >= FirstDischargeDate
        && (LastDischargeDate is null || dischargeDate <= LastDischargeDate);
}

/// <summary>
/// Chooses between the releases a deployment has loaded.
/// </summary>
public sealed class ReleaseWindowProvider : IMsDrgReleaseProvider
{
    private readonly IReadOnlyList<MsDrgReleaseWindow> windows;

    /// <summary>Creates a provider over the loaded releases, in any order.</summary>
    public ReleaseWindowProvider(IEnumerable<MsDrgReleaseWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        this.windows = [.. windows];
    }

    /// <inheritdoc />
    public Task<MsDrgRelease?> GetForDischargeDateAsync(
        DateOnly dischargeDate, CancellationToken cancellationToken = default) =>
        Task.FromResult(windows.FirstOrDefault(w => w.Covers(dischargeDate))?.Release);
}

/// <summary>
/// Puts the formula engine behind the grouper seam, so that the pricing path
/// can ask for a DRG without knowing which grouper answered.
///
/// <para>
/// This is the engine that was shown to agree with CMS's own grouper on every
/// row of the parity corpus, which <see cref="TableDrivenMsDrgGrouper"/> —
/// the other implementation of this interface — was not. Where both are
/// available, this is the one to bind.
/// </para>
///
/// <para>
/// The adapter's whole job is translation, and the two places it declines to
/// translate are worth reading before trusting a field:
/// <see cref="MsDrgGroupingResult.Severity"/> and
/// <see cref="MsDrgGroupingResult.DrgTitle"/> are left unset, for the reasons
/// given on <see cref="FamilySeverityIsNotInTheFormulaTable"/>.
/// </para>
/// </summary>
public sealed class FormulaMsDrgGrouper : IMsDrgGrouper
{
    /// <summary>
    /// Why the severity split member is not reported through this adapter.
    ///
    /// <para>
    /// CMS's DRG logic table carries a <c>drgSeverity</c> field on every row,
    /// and it is not the family's severity marker. In V44, 731 of the 824 rows
    /// carry 0, including every member of three-member families such as
    /// 11/12/13 — so a stay paid DRG 13 ("without CC/MCC") sits on a row
    /// marked 0, exactly like the "with MCC" sibling above it. Mapping that
    /// field onto <see cref="DrgSeverity"/> would put a plausible and wrong
    /// value on a field an appeal reads.
    /// </para>
    ///
    /// <para>
    /// The split member could be inferred from the DRG's position within its
    /// base family, and that is safe only for three-member families: in a
    /// two-member family the lower DRG may be "with CC/MCC" or "with MCC", and
    /// the formula table does not say which. So the field stays unset and
    /// <see cref="MsDrgGroupingResult.ComplicationLevel"/>, which the engine
    /// computes outright, carries the severity information instead.
    /// </para>
    /// </summary>
    public const string FamilySeverityIsNotInTheFormulaTable =
        "The CMS formula table's severity field is not the family severity marker.";

    private readonly IMsDrgReleaseProvider releases;

    /// <summary>Creates a grouper over the deployment's releases.</summary>
    public FormulaMsDrgGrouper(IMsDrgReleaseProvider releases)
    {
        ArgumentNullException.ThrowIfNull(releases);
        this.releases = releases;
    }

    /// <inheritdoc />
    public async Task<MsDrgGroupingResult> GroupAsync(
        MsDrgGroupingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var release = await releases
            .GetForDischargeDateAsync(request.DischargeDate, cancellationToken)
            .ConfigureAwait(false);

        if (release is null)
        {
            return MsDrgGroupingResult.NotGrouped(
                GroupingStatus.NoTablesForDischargeDate,
                "none",
                request.BilledDrg,
                $"No CMS release is loaded for a stay discharged {request.DischargeDate:yyyy-MM-dd}.");
        }

        // The engine throws when a row matched that assigns no DRG and nothing
        // rerouted the stay afterwards. That is a disagreement between the
        // claim and the tables, and CMS throws there too -- but adjudication
        // has to pend the claim with a reason rather than fail, which is this
        // interface's contract, so it is caught and reported here.
        MsDrgFormulaGroupingResult grouped;
        try
        {
            grouped = new MsDrgFormulaGrouper(release).Group(request);
        }
        catch (MsDrgGroupingFailedException failure)
        {
            return MsDrgGroupingResult.NotGrouped(
                GroupingStatus.NoDrgRuleMatched, release.Version, request.BilledDrg, failure.Message);
        }

        if (grouped.Grouping is null)
        {
            return MsDrgGroupingResult.NotGrouped(
                GroupingStatus.MissingPrincipalDiagnosis,
                release.Version,
                request.BilledDrg,
                [grouped.Refusal ?? "The claim could not be grouped.", .. UnrecognisedNote(grouped)]);
        }

        var paid = grouped.Grouping.AfterHacPolicy;
        if (!paid.Groupable)
        {
            return MsDrgGroupingResult.NotGrouped(
                StatusFor(paid.ReturnCode),
                release.Version,
                request.BilledDrg,
                [
                    paid.ReturnCode is int code
                        ? $"The grouper's tables refused the claim with return code {code}."
                        : "The grouper's tables assigned no DRG.",
                    .. UnrecognisedNote(grouped)
                ]);
        }

        return new MsDrgGroupingResult
        {
            Status = GroupingStatus.Grouped,
            Drg = paid.Drg.ToString("000"),
            Mdc = paid.Mdc,
            Partition = PartitionOf(paid.Partition),
            FamilyKey = paid.BaseDrg.ToString("000"),

            // Severity and DrgTitle are deliberately unset: see
            // FamilySeverityIsNotInTheFormulaTable, and the formula tables
            // carry no DRG titles.
            ComplicationLevel = paid.Severity,
            DrivingComplicationCode = DrivingComplication(request, release, grouped, paid.Severity),
            ExcludedComplications = ExcludedComplications(request, release, grouped),

            Source = DrgSource.ChoGrouper,
            GrouperVersion = release.Version,
            BilledDrg = request.BilledDrg,
            Notes = [.. PolicyNotes(grouped), .. UnrecognisedNote(grouped)]
        };
    }

    /// <summary>
    /// Maps CMS's return code onto the interface's status. Code 7 is the
    /// <c>invalid_pdx</c> edit, which is how an unrecognised principal
    /// diagnosis is refused; the hospital-acquired-condition processor's codes
    /// are all about unusable present-on-admission data; anything else is an
    /// edit in the tables, named by its number rather than guessed at.
    /// </summary>
    public static GroupingStatus StatusFor(int? returnCode) => returnCode switch
    {
        7 => GroupingStatus.InvalidPrincipalDiagnosis,
        MsDrgHacProcessor.MissingOnePoa
            or MsDrgHacProcessor.UnknownStatusPoaNotPresent
            or MsDrgHacProcessor.UnknownStatusPoaUnreadable
            or MsDrgHacProcessor.UnknownStatusMultipleConditions
            => GroupingStatus.PresentOnAdmissionUnusable,
        not null => GroupingStatus.RefusedByCodeEdit,
        null => GroupingStatus.NoDrgRuleMatched
    };

    private static DrgPartition PartitionOf(DrgTablePartition partition) => partition switch
    {
        DrgTablePartition.Medical => DrgPartition.Medical,
        DrgTablePartition.Surgical => DrgPartition.Surgical,

        // A row that assigns a DRG and marks neither partition is a pre-MDC
        // assignment: the transplant and tracheostomy rows that are ranked
        // before any MDC is chosen.
        _ => DrgPartition.PreMdc
    };

    private static IReadOnlyList<string> UnrecognisedNote(MsDrgFormulaGroupingResult grouped) =>
        grouped.UnrecognisedCodes.Count == 0
            ? []
            : [
                "The release's tables do not hold "
                + string.Join(", ", grouped.UnrecognisedCodes)
                + ". A code the tables do not hold contributes nothing to the grouping, which is "
                + "what CMS does with it; whether to reject the claim back to the biller is the "
                + "caller's policy, not the grouper's answer."
              ];

    private static IReadOnlyList<string> PolicyNotes(MsDrgFormulaGroupingResult grouped)
    {
        if (grouped.Grouping is not { } grouping)
            return [];

        var notes = new List<string>();
        if (grouping.PolicyChangedTheDrg)
        {
            notes.Add(
                $"The hospital-acquired-condition policy moved the stay from DRG "
                + $"{grouping.BeforeHacPolicy.Drg:000} to {grouping.AfterHacPolicy.Drg:000}.");
        }

        return notes;
    }

    /// <summary>
    /// The secondary diagnosis the stay's complication level is credited to:
    /// the first one still counting that carries that level. "First" is the
    /// claim's own order, because nothing in the tables ranks two codes of the
    /// same level against each other and an appeal is better answered with a
    /// stable code than an arbitrary one.
    /// </summary>
    private static string? DrivingComplication(
        MsDrgGroupingRequest request,
        MsDrgRelease release,
        MsDrgFormulaGroupingResult grouped,
        ComplicationLevel credited)
    {
        if (credited == ComplicationLevel.None || grouped.Grouping is null)
            return null;

        var removed = grouped.Grouping.Hac.RemovedFromSeverity;
        var excluded = release.Exclusions.ExclusionsFor(Principal(request));

        return request.SecondaryDiagnoses
            .Select(d => d.Code)
            .FirstOrDefault(code =>
                !removed.Contains(code)
                && !excluded.Contains(code)
                && SeverityOf(release, code) == credited);
    }

    private static IReadOnlyList<ExcludedComplication> ExcludedComplications(
        MsDrgGroupingRequest request,
        MsDrgRelease release,
        MsDrgFormulaGroupingResult grouped)
    {
        if (grouped.Grouping is null)
            return [];

        var removed = grouped.Grouping.Hac.RemovedFromSeverity;
        var excluded = release.Exclusions.ExclusionsFor(Principal(request));
        var reported = new List<ExcludedComplication>();

        foreach (var diagnosis in request.SecondaryDiagnoses)
        {
            var code = diagnosis.Code;
            var level = SeverityOf(release, code);
            if (level == ComplicationLevel.None)
                continue;

            if (removed.Contains(code))
            {
                reported.Add(new ExcludedComplication(
                    code, level, "The hospital-acquired-condition policy: not present on admission."));
            }
            else if (excluded.Contains(code))
            {
                reported.Add(new ExcludedComplication(
                    code, level, $"CC exclusion against principal diagnosis {Principal(request)}."));
            }
        }

        return reported;
    }

    private static ComplicationLevel SeverityOf(MsDrgRelease release, string code) =>
        release.Codes.TryGetDiagnosis(code, out var attributes)
            ? attributes.Severity
            : ComplicationLevel.None;

    /// <summary>
    /// Codes are looked up exactly as the mask builder looks them up, which is
    /// verbatim: <see cref="MsDrgGroupingRequest"/> requires them already
    /// without a decimal point and upper case, and nothing downstream
    /// normalises. Normalising only here would find an excluded code the
    /// grouping itself had treated as unrecognised, and the two answers would
    /// disagree about the same claim.
    /// </summary>
    private static string Principal(MsDrgGroupingRequest request) =>
        request.PrincipalDiagnosis.Code;
}
