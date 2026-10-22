using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Models;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.GrouperEngine.Services;

/// <summary>
/// Assigns an MS-DRG by walking the CMS definitions-manual tables, in the
/// order the manual itself reads:
///
/// <list type="number">
///   <item>Validate the principal diagnosis and check it may stand as one.</item>
///   <item>Try the pre-MDC rules (transplants, tracheostomy, ECMO), which run
///         before MDC assignment and ignore the principal diagnosis's MDC.</item>
///   <item>Assign the MDC from the principal diagnosis.</item>
///   <item>Decide the partition: surgical if any procedure is an
///         operating-room procedure for that MDC, medical otherwise.</item>
///   <item>Apply the surgical hierarchy to pick one surgical class, then match
///         the first family rule for the MDC, partition and class.</item>
///   <item>Resolve severity: the highest CC/MCC among the secondary diagnoses,
///         after CC exclusions and the hospital-acquired-condition rule, then
///         take the highest severity member the family actually has.</item>
///   <item>Apply the sex edit on the chosen DRG.</item>
/// </list>
///
/// <para>
/// No DRG number is hard-coded here: every answer comes from the tables, so
/// a new fiscal year is a data load, not a code change (ADR 024 §5).
/// </para>
/// </summary>
public sealed class TableDrivenMsDrgGrouper : IMsDrgGrouper
{
    private readonly IMsDrgTableProvider _tables;
    private readonly ILogger<TableDrivenMsDrgGrouper> _logger;

    public TableDrivenMsDrgGrouper(IMsDrgTableProvider tables, ILogger<TableDrivenMsDrgGrouper> logger)
    {
        _tables = tables;
        _logger = logger;
    }

    public async Task<MsDrgGroupingResult> GroupAsync(
        MsDrgGroupingRequest request, CancellationToken cancellationToken = default)
    {
        var tables = await _tables.GetForDischargeDateAsync(request.DischargeDate, cancellationToken);
        if (tables is null)
        {
            _logger.LogWarning(
                "No MS-DRG tables cover discharge date {DischargeDate:yyyy-MM-dd}", request.DischargeDate);
            return MsDrgGroupingResult.NotGrouped(
                GroupingStatus.NoTablesForDischargeDate, "none", request.BilledDrg,
                $"No grouper version covers discharge date {request.DischargeDate:yyyy-MM-dd}.");
        }

        var principal = Normalize(request.PrincipalDiagnosis.Code);
        if (principal.Length == 0)
        {
            return MsDrgGroupingResult.NotGrouped(
                GroupingStatus.MissingPrincipalDiagnosis, tables.Version, request.BilledDrg,
                "The claim carries no principal diagnosis (837I HI*ABK).");
        }

        var known = tables.KnownDiagnoses.Count > 0
            ? tables.KnownDiagnoses.Contains(principal)
            : tables.MdcAssignments.ContainsKey(principal);
        if (!known)
        {
            return MsDrgGroupingResult.NotGrouped(
                GroupingStatus.InvalidPrincipalDiagnosis, tables.Version, request.BilledDrg,
                $"Principal diagnosis {principal} is not in the {tables.Version} code set.");
        }

        var notes = new List<string>();
        var procedures = request.Procedures
            .Select(p => Normalize(p.Code))
            .Where(c => c.Length > 0)
            .ToList();

        // ── Severity is resolved up front: pre-MDC families are split by it too.
        var severity = ResolveComplication(request, tables, principal);

        // ── 2. Pre-MDC rules run before MDC assignment.
        if (MatchPreMdc(tables, principal, procedures, request.DischargeStatus) is { } preMdcRule)
        {
            notes.Add($"Pre-MDC rule {preMdcRule.FamilyKey} matched before MDC assignment.");
            return Finalize(
                tables, preMdcRule, mdc: null, DrgPartition.PreMdc,
                surgicalClass: null, drivingProcedure: null,
                severity, request, notes);
        }

        // ── 3. MDC assignment.
        if (!tables.MdcAssignments.TryGetValue(principal, out var mdcAssignment))
        {
            return MsDrgGroupingResult.NotGrouped(
                GroupingStatus.NoMdcAssignment, tables.Version, request.BilledDrg,
                $"Principal diagnosis {principal} maps to no MDC in {tables.Version}.");
        }

        if (!mdcAssignment.AcceptableAsPrincipal)
        {
            return MsDrgGroupingResult.NotGrouped(
                GroupingStatus.UnacceptablePrincipalDiagnosis, tables.Version, request.BilledDrg,
                $"Principal diagnosis {principal} is an unacceptable principal diagnosis (MCE edit).");
        }

        var mdc = mdcAssignment.Mdc;

        // ── 4 and 5. Partition, surgical hierarchy, family rule.
        var surgical = SelectSurgicalClass(tables, mdc, procedures);
        var partition = surgical is null ? DrgPartition.Medical : DrgPartition.Surgical;
        if (surgical is not null)
        {
            notes.Add(
                $"Surgical class {surgical.SurgicalClass} (hierarchy rank {surgical.HierarchyRank}) " +
                $"selected from procedure {surgical.Code}.");
        }

        var rule = tables.FamilyRules
            .Where(r => r.Partition == partition && r.Mdc == mdc)
            .OrderBy(r => r.Rank)
            .FirstOrDefault(r => Matches(r, principal, procedures, surgical, request.DischargeStatus));

        if (rule is null)
        {
            return MsDrgGroupingResult.NotGrouped(
                GroupingStatus.NoDrgRuleMatched, tables.Version, request.BilledDrg,
                $"MDC {mdc} {partition} has no family rule matching this stay.");
        }

        return Finalize(
            tables, rule, mdc, partition,
            surgical?.SurgicalClass, surgical?.Code,
            severity, request, notes);
    }

    // ─────────────────────────────────────────────────────────
    //  Steps
    // ─────────────────────────────────────────────────────────

    /// <summary>
    /// The highest complication level among the secondary diagnoses. A code is
    /// not counted when the CC exclusion list excludes it against this
    /// principal diagnosis, when it repeats the principal diagnosis, or when
    /// it was not present on admission (the hospital-acquired-condition
    /// payment policy: POA <c>N</c> or <c>U</c>).
    /// </summary>
    private static ComplicationOutcome ResolveComplication(
        MsDrgGroupingRequest request, MsDrgTableSet tables, string principal)
    {
        tables.CcExclusions.TryGetValue(principal, out var exclusion);

        var level = ComplicationLevel.None;
        string? driver = null;
        var excluded = new List<ExcludedComplication>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { principal };

        foreach (var diagnosis in request.SecondaryDiagnoses)
        {
            var code = Normalize(diagnosis.Code);
            if (code.Length == 0 || !seen.Add(code))
                continue;
            if (!tables.Complications.TryGetValue(code, out var assignment) ||
                assignment.Level == ComplicationLevel.None)
                continue;

            if (exclusion?.ExcludedSecondaryCodes.Contains(code) == true)
            {
                excluded.Add(new ExcludedComplication(
                    code, assignment.Level, $"CC exclusion against principal diagnosis {principal}"));
                continue;
            }

            if (IsHospitalAcquired(diagnosis.PresentOnAdmission))
            {
                excluded.Add(new ExcludedComplication(
                    code, assignment.Level,
                    $"Not present on admission (POA {diagnosis.PresentOnAdmission})"));
                continue;
            }

            if (assignment.Level > level)
            {
                level = assignment.Level;
                driver = code;
            }
        }

        return new ComplicationOutcome(level, driver, excluded);
    }

    /// <summary>
    /// POA <c>N</c> (not present) and <c>U</c> (documentation insufficient) are
    /// hospital-acquired and do not raise severity. <c>Y</c>, <c>W</c> and
    /// <c>1</c> (exempt) do, and so does a missing indicator — a payer may not
    /// take severity away for an absent indicator.
    /// </summary>
    private static bool IsHospitalAcquired(string? presentOnAdmission) =>
        presentOnAdmission is { Length: > 0 } poa &&
        (poa.Equals("N", StringComparison.OrdinalIgnoreCase) ||
         poa.Equals("U", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The one surgical class the stay groups by: of every operating-room
    /// procedure that belongs to a class in this MDC, the class with the
    /// lowest hierarchy rank wins. Ties fall back to claim order, so the
    /// principal procedure wins a tie.
    /// </summary>
    private static SurgicalClassAssignment? SelectSurgicalClass(
        MsDrgTableSet tables, int mdc, IReadOnlyList<string> procedures)
    {
        SurgicalClassAssignment? best = null;
        foreach (var code in procedures)
        {
            if (!tables.SurgicalClasses.TryGetValue(code, out var assignments))
                continue;
            foreach (var assignment in assignments)
            {
                if (assignment.Mdc != mdc)
                    continue;
                if (best is null || assignment.HierarchyRank < best.HierarchyRank)
                    best = assignment;
            }
        }
        return best;
    }

    private static DrgFamilyRule? MatchPreMdc(
        MsDrgTableSet tables, string principal, IReadOnlyList<string> procedures, string? dischargeStatus) =>
        tables.FamilyRules
            .Where(r => r.Partition == DrgPartition.PreMdc)
            .OrderBy(r => r.Rank)
            .FirstOrDefault(r => Matches(r, principal, procedures, surgical: null, dischargeStatus));

    private static bool Matches(
        DrgFamilyRule rule,
        string principal,
        IReadOnlyList<string> procedures,
        SurgicalClassAssignment? surgical,
        string? dischargeStatus)
    {
        if (rule.SurgicalClass is { Length: > 0 } required &&
            !string.Equals(required, surgical?.SurgicalClass, StringComparison.OrdinalIgnoreCase))
            return false;

        if (rule.PrincipalDiagnoses.Count > 0 && !rule.PrincipalDiagnoses.Contains(principal))
            return false;

        if (rule.RequiredProcedures.Count > 0 &&
            !rule.RequiredProcedures.All(p => procedures.Contains(p, StringComparer.OrdinalIgnoreCase)))
            return false;

        if (rule.DischargeStatuses.Count > 0 &&
            (dischargeStatus is null || !rule.DischargeStatuses.Contains(dischargeStatus)))
            return false;

        return true;
    }

    /// <summary>
    /// Resolves the family to a DRG at the highest severity the family has at
    /// or below the stay's complication level, then applies the sex edit.
    /// A family with one member (<see cref="DrgSeverity.Unsplit"/>) ignores
    /// severity, which is how most surgical families with a single DRG read.
    /// </summary>
    private MsDrgGroupingResult Finalize(
        MsDrgTableSet tables,
        DrgFamilyRule rule,
        int? mdc,
        DrgPartition partition,
        string? surgicalClass,
        string? drivingProcedure,
        ComplicationOutcome complication,
        MsDrgGroupingRequest request,
        List<string> notes)
    {
        var family = tables.Drgs.Values
            .Where(d => string.Equals(d.FamilyKey, rule.FamilyKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (family.Count == 0)
        {
            _logger.LogError(
                "MS-DRG tables {Version} have rule for family {Family} but no DRG in it",
                tables.Version, rule.FamilyKey);
            return MsDrgGroupingResult.NotGrouped(
                GroupingStatus.NoDrgRuleMatched, tables.Version, request.BilledDrg,
                $"Family {rule.FamilyKey} has no DRG in {tables.Version}.");
        }

        var wanted = complication.Level switch
        {
            ComplicationLevel.Mcc => DrgSeverity.WithMcc,
            ComplicationLevel.Cc => DrgSeverity.WithCc,
            _ => DrgSeverity.WithoutCcMcc
        };

        // Candidates the stay qualifies for: the unsplit member always, and
        // every severity member at or below the stay's level. The highest wins.
        var chosen = family
            .Where(d => d.Severity == DrgSeverity.Unsplit || d.Severity <= wanted)
            .OrderByDescending(d => d.Severity)
            .FirstOrDefault()
            ?? family.OrderBy(d => d.Severity).First();

        if (chosen.Severity != DrgSeverity.Unsplit && chosen.Severity != wanted)
        {
            notes.Add(
                $"Family {rule.FamilyKey} is not split at {wanted}; paid at {chosen.Severity}.");
        }

        if (chosen.RequiredSex is { Length: > 0 } requiredSex &&
            !string.Equals(requiredSex, request.Sex, StringComparison.OrdinalIgnoreCase))
        {
            return MsDrgGroupingResult.NotGrouped(
                GroupingStatus.DemographicConflict, tables.Version, request.BilledDrg,
                $"MS-DRG {chosen.Drg} requires sex {requiredSex}; the claim carries " +
                $"{(string.IsNullOrWhiteSpace(request.Sex) ? "none" : request.Sex)}.");
        }

        return new MsDrgGroupingResult
        {
            Status = GroupingStatus.Grouped,
            Drg = chosen.Drg,
            DrgTitle = chosen.Title,
            Mdc = mdc,
            Partition = partition,
            FamilyKey = rule.FamilyKey,
            Severity = chosen.Severity,
            ComplicationLevel = complication.Level,
            DrivingComplicationCode = complication.Driver,
            ExcludedComplications = complication.Excluded,
            SurgicalClass = surgicalClass,
            DrivingProcedureCode = drivingProcedure,
            Source = DrgSource.ChoGrouper,
            GrouperVersion = tables.Version,
            BilledDrg = request.BilledDrg,
            Notes = notes
        };
    }

    /// <summary>
    /// Grouper tables are keyed without the decimal point and in upper case;
    /// an 837 can carry either shape.
    /// </summary>
    internal static string Normalize(string? code) =>
        string.IsNullOrWhiteSpace(code) ? string.Empty : code.Trim().Replace(".", "").ToUpperInvariant();

    private sealed record ComplicationOutcome(
        ComplicationLevel Level, string? Driver, IReadOnlyList<ExcludedComplication> Excluded);
}
