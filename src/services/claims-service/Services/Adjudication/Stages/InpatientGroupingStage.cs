using ClaimsService.Models;
using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Hac;
using CloudHealthOffice.GrouperEngine.Licensed;
using CloudHealthOffice.GrouperEngine.Models;
using CloudHealthOffice.GrouperEngine.Services;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.PlanConfiguration;

namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// What grouping decided for an inpatient claim, so
/// <see cref="PricingStage"/> knows which DRG to price and the record says
/// why.
/// </summary>
/// <param name="PricedDrg">
/// The DRG <see cref="PricingStage"/> should price by: the grouped one when
/// grouping ran and succeeded, the billed one otherwise.
/// </param>
/// <param name="BilledDrg">The DRG the facility put on the claim, or null.</param>
/// <param name="Result">
/// The grouper's own result, kept whole. An inpatient DRG is the single field
/// an appeal is most often about, and the result names every input that moved
/// it — the driving complication, the complications excluded and why. Reducing
/// it to a three-character code here would throw away the answer to the
/// appeal.
/// </param>
/// <param name="Grouped">Whether the priced DRG came from the grouper.</param>
public sealed record InpatientGroupingOutcome(
    string? PricedDrg,
    string? BilledDrg,
    MsDrgGroupingResult? Result,
    bool Grouped)
{
    /// <summary>
    /// The licensed grouper's APR-DRG answer, kept whole, when the plan
    /// groups by APR-DRG. <see cref="PricedDrg"/> is then the "DRG-SOI" key
    /// (e.g. <c>139-3</c>) and <see cref="Result"/> is null.
    /// </summary>
    public AprDrgGroupingResult? AprDrgResult { get; init; }

    /// <summary>
    /// The state Medicaid APR-DRG method the stay is paid by
    /// (<c>pricing.inpatient.medicaidDrgMethod</c>), set only when the stay
    /// grouped by APR-DRG and the plan names a method. Null prices it by the
    /// contract or fee schedule.
    /// </summary>
    public string? MedicaidDrgMethod { get; init; }

    /// <summary>Grouping did not run: the billed DRG stands.</summary>
    public static InpatientGroupingOutcome NotGrouped(string? billedDrg) =>
        new(billedDrg, billedDrg, null, Grouped: false);

    /// <summary>Whether the grouper disagreed with the facility.</summary>
    /// <remarks>
    /// For an APR-DRG stay the facility's billed DRG is compared with the base
    /// APR-DRG, because a facility bills the three-digit group and the
    /// severity is the grouper's to assign.
    /// </remarks>
    public bool DisagreesWithFacility =>
        Grouped
        && !string.IsNullOrWhiteSpace(BilledDrg)
        && !string.Equals(AprDrgResult?.AprDrg ?? PricedDrg, BilledDrg, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Assigns the MS-DRG for an inpatient claim, so <see cref="PricingStage"/>
/// (Order=250) prices by the DRG CHO's grouper worked out rather than the one
/// the facility billed.
///
/// <para>
/// <b>Off unless a payer turns it on.</b> The plan setting
/// <c>pricing.inpatient.groupDrg</c> is false everywhere by default, and when
/// it is false this stage does nothing but record the billed DRG. Grouping
/// changes which DRG rate row a claim prices against, so it changes paid
/// amounts; that is a payer's decision to make per line of business after
/// looking at its own inpatient history through the grouper, not something a
/// release should start doing.
/// </para>
///
/// <para>
/// <b>Fail closed.</b> A claim that should have been grouped and could not be
/// is pended, never priced by the billed DRG as a fallback. The whole point of
/// turning this on is that the facility's own number is not the one being
/// paid; quietly reverting to it on a bad code or a missing table would pay
/// exactly the amount the payer was trying to stop paying, and would do it
/// invisibly. The same reasoning
/// <see cref="MsDrgGrouperRegistration.AddMsDrgGrouper"/> gives for refusing
/// to start when a configured release will not import.
/// </para>
///
/// <para>
/// <b>When the setting cannot be read</b> the fallback is the default, false,
/// and whether that is safe depends on the host. A host with no CMS tables
/// loaded could not have grouped whatever the setting said, so the unknown
/// costs nothing and the claim prices as it always has. A host that does have
/// tables may be one that groups, and pricing by the billed DRG there is the
/// silent wrong amount above — so the claim pends instead. The host's own
/// capability answers a question the settings service cannot.
/// </para>
///
/// <para>
/// Order 240: after <see cref="NetworkCredentialingStage"/> (200), before
/// <see cref="PricingStage"/> (250), which reads
/// <see cref="ClaimAdjudicationContext.InpatientGrouping"/>.
/// </para>
/// </summary>
public sealed class InpatientGroupingStage : IClaimAdjudicationStage
{
    public const string StageName = "InpatientGrouping";

    /// <summary>
    /// The plan setting that turns grouping on.
    /// </summary>
    public static readonly string SettingKey = PlanSettings.GroupInpatientDrg.Key;

    /// <summary>
    /// <see cref="PendDetails.PendCode"/> when a claim that should have been
    /// grouped could not be. Shares the clinical-review bucket that an
    /// inpatient DRG question belongs in: resolving one means reading the
    /// chart, not re-running the pipeline.
    /// </summary>
    public const string GroupingPendCode = "DRGREVIEW";

    /// <summary>
    /// <see cref="PendDetails.PendCode"/> when the host can group but could
    /// not find out whether it should.
    /// </summary>
    public const string SettingUnavailablePendCode = "CONFIGUNAVAIL";

    /// <summary>
    /// <see cref="PendDetails.PendCode"/> when the plan groups by APR-DRG and
    /// the licensed grouper is not configured or could not be reached.
    /// Resolved by fixing or waiting for the grouper and re-running, not by
    /// reading the chart, so it is kept apart from <see cref="GroupingPendCode"/>.
    /// </summary>
    public const string GrouperUnavailablePendCode = "GROUPERUNAVAIL";

    /// <summary>The plan setting that chooses MS-DRG or APR-DRG.</summary>
    public static readonly string ClassificationSettingKey = PlanSettings.InpatientDrgClassification.Key;

    /// <summary>The plan setting naming the APR-DRG version the state prices by.</summary>
    public static readonly string AprDrgVersionSettingKey = PlanSettings.AprDrgVersion.Key;

    /// <summary>The state Medicaid APR-DRG method an APR-DRG-grouped stay is paid by.</summary>
    public static readonly string MedicaidDrgMethodSettingKey = PlanSettings.MedicaidDrgMethod.Key;

    private readonly IMsDrgGrouper _grouper;
    private readonly IAprDrgGrouper? _aprGrouper;
    private readonly IPlanSettings _settings;
    private readonly IMsDrgReleaseProvider? _releases;
    private readonly ILogger<InpatientGroupingStage> _logger;

    public InpatientGroupingStage(
        IMsDrgGrouper grouper,
        IPlanSettings settings,
        ILogger<InpatientGroupingStage> logger,
        IMsDrgReleaseProvider? releases = null,
        IAprDrgGrouper? aprGrouper = null)
    {
        _grouper = grouper;
        _aprGrouper = aprGrouper;
        _settings = settings;
        _logger = logger;
        _releases = releases;
    }

    public string Name => StageName;
    public int Order => 240;
    public bool IsRequired => false;

    /// <summary>
    /// Whether this host has CMS tables loaded and so could group at all.
    /// Registered only when a release imported, which is what makes the
    /// nullable injection above a capability answer rather than a guess.
    /// </summary>
    private bool CanGroup =>
        _releases is not null
        || _aprGrouper is not (null or NotConfiguredLicensedGrouper);

    public async Task<ClaimAdjudicationStageResult> ExecuteAsync(
        ClaimAdjudicationContext context,
        CancellationToken ct)
    {
        var claim = context.Claim;
        var billedDrg = string.IsNullOrWhiteSpace(claim.Institutional?.DrgCode)
            ? null
            : claim.Institutional!.DrgCode!.Trim();

        if (!IsInpatient(claim))
        {
            // Nothing to group. An outpatient institutional claim has no stay,
            // and a professional one has no facility.
            context.InpatientGrouping = InpatientGroupingOutcome.NotGrouped(billedDrg);
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        var setting = await _settings
            .ResolveAsync(
                context.TenantId,
                SettingKey,
                // The claim's full scope: line of business, benefit plan,
                // sponsor and sponsor policy. It was the line of business
                // alone, so a value entered on this sponsor's policy -- the
                // level a payer configuring one employer's stay pricing would
                // actually use -- resolved to the tenant default instead.
                //
                // The line of business is still the claim's own: its enum
                // names and numbers are the ones LinesOfBusiness.Normalize
                // knows, whereas ResolvedBenefitPlan carries a plan type,
                // which is a different thing and would silently never match a
                // stored value.
                context.SettingScopeForClaim(),
                claim.Institutional?.DischargeDate ?? claim.ServiceDateFrom,
                ct)
            .ConfigureAwait(false);

        if (setting.IsFallback && CanGroup)
        {
            _logger.LogWarning(
                "Could not read {SettingKey} for claim {ClaimVersionId} on a host that can group; pending",
                LogSanitizer.Sanitize(SettingKey),
                LogSanitizer.Sanitize(context.ClaimVersionId));
            context.InpatientGrouping = InpatientGroupingOutcome.NotGrouped(billedDrg);
            return Pend(context, SettingUnavailablePendCode,
                "Could not determine whether this plan prices inpatient claims by the grouped DRG; "
                + "the claim was not priced by the billed DRG because this deployment may group.");
        }

        if (!setting.AsBool())
        {
            context.InpatientGrouping = InpatientGroupingOutcome.NotGrouped(billedDrg);
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        var classification = await _settings
            .ResolveAsync(
                context.TenantId,
                ClassificationSettingKey,
                context.SettingScopeForClaim(),
                claim.Institutional?.DischargeDate ?? claim.ServiceDateFrom,
                ct)
            .ConfigureAwait(false);

        if (classification.IsFallback)
        {
            // Grouping is on but which grouper is unknown. Guessing MS-DRG for
            // a Medicaid line would price an APR-DRG contract against the
            // wrong table, so the claim waits for the setting.
            context.InpatientGrouping = InpatientGroupingOutcome.NotGrouped(billedDrg);
            return Pend(context, SettingUnavailablePendCode,
                "Could not determine whether this plan groups inpatient claims by MS-DRG or APR-DRG.");
        }

        if (IsAprDrg(classification.Value))
        {
            return await GroupByAprDrgAsync(context, billedDrg, ct).ConfigureAwait(false);
        }

        var request = BuildRequest(context, billedDrg);
        if (request is null)
        {
            context.InpatientGrouping = InpatientGroupingOutcome.NotGrouped(billedDrg);
            return Pend(context, GroupingPendCode,
                "This plan prices inpatient claims by the grouped DRG and the claim carries no "
                + "principal diagnosis to group from.");
        }

        MsDrgGroupingResult result;
        try
        {
            result = await _grouper.GroupAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "The grouper threw for claim {ClaimVersionId}",
                LogSanitizer.Sanitize(context.ClaimVersionId));
            context.InpatientGrouping = InpatientGroupingOutcome.NotGrouped(billedDrg);
            return Pend(context, GroupingPendCode,
                "Grouping failed for this claim; it was not priced by the billed DRG because this "
                + "plan prices inpatient claims by the grouped DRG.");
        }

        if (result.Status != GroupingStatus.Grouped || string.IsNullOrWhiteSpace(result.Drg))
        {
            context.InpatientGrouping = new InpatientGroupingOutcome(
                PricedDrg: null, billedDrg, result, Grouped: false);
            return Pend(context, GroupingPendCode,
                $"The stay could not be grouped ({result.Status}): {Explain(result)}");
        }

        var outcome = new InpatientGroupingOutcome(
            PricedDrg: result.Drg, billedDrg, result, Grouped: true);
        context.InpatientGrouping = outcome;

        if (outcome.DisagreesWithFacility)
        {
            // Logged at information, not warning: a disagreement is the
            // expected outcome on a meaningful share of claims and is the
            // reason to group. It is recorded so that the difference between
            // what was billed and what was paid can be answered later.
            _logger.LogInformation(
                "Claim {ClaimVersionId} grouped to DRG {GroupedDrg}; the facility billed {BilledDrg}",
                LogSanitizer.Sanitize(context.ClaimVersionId),
                LogSanitizer.Sanitize(result.Drg),
                LogSanitizer.Sanitize(billedDrg));
        }

        return ClaimAdjudicationStageResult.Pass(StageName);
    }

    private static bool IsAprDrg(string? settingValue) =>
        string.Equals(settingValue?.Trim().Trim('"'), "aprDrg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Groups the stay through the plan's licensed APR-DRG grouper and sets
    /// the priced DRG to the "DRG-SOI" key state weight tables use. Fails
    /// closed exactly as the MS-DRG path does: any answer short of a
    /// validated group pends, and the billed DRG is never priced instead.
    /// </summary>
    private async Task<ClaimAdjudicationStageResult> GroupByAprDrgAsync(
        ClaimAdjudicationContext context, string? billedDrg, CancellationToken ct)
    {
        if (_aprGrouper is null or NotConfiguredLicensedGrouper)
        {
            context.InpatientGrouping = InpatientGroupingOutcome.NotGrouped(billedDrg);
            return Pend(context, GrouperUnavailablePendCode,
                "This plan prices inpatient claims by APR-DRG and no licensed APR-DRG grouper is configured "
                + "(LicensedGrouper:BaseUrl).");
        }

        var claim = context.Claim;
        var asOf = claim.Institutional?.DischargeDate ?? claim.ServiceDateFrom;
        var version = await _settings
            .ResolveAsync(context.TenantId, AprDrgVersionSettingKey, context.SettingScopeForClaim(), asOf, ct)
            .ConfigureAwait(false);
        if (version.IsFallback)
        {
            // State weights are published per version; grouping in whatever
            // version the grouper defaults to could price against the wrong
            // weight table.
            context.InpatientGrouping = InpatientGroupingOutcome.NotGrouped(billedDrg);
            return Pend(context, SettingUnavailablePendCode,
                "Could not determine which APR-DRG version this plan prices by.");
        }

        var msRequest = BuildRequest(context, billedDrg);
        if (msRequest is null)
        {
            context.InpatientGrouping = InpatientGroupingOutcome.NotGrouped(billedDrg);
            return Pend(context, GroupingPendCode,
                "This plan prices inpatient claims by APR-DRG and the claim carries no principal diagnosis to group from.");
        }

        var request = BuildAprDrgRequest(context, msRequest, version.AsInt());

        AprDrgGroupingResult result;
        try
        {
            result = await _aprGrouper.GroupAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The contract says the grouper never throws; a defect in an
            // implementation still must not price the claim.
            _logger.LogError(ex,
                "The APR-DRG grouper threw for claim {ClaimVersionId}",
                LogSanitizer.Sanitize(context.ClaimVersionId));
            context.InpatientGrouping = InpatientGroupingOutcome.NotGrouped(billedDrg);
            return Pend(context, GrouperUnavailablePendCode,
                "APR-DRG grouping failed for this claim; it was not priced by the billed DRG.");
        }

        if (result.Status != LicensedGroupingStatus.Grouped || result.DrgSoiKey is null)
        {
            context.InpatientGrouping = new InpatientGroupingOutcome(
                PricedDrg: null, billedDrg, Result: null, Grouped: false)
            {
                AprDrgResult = result,
            };
            var code = result.Status is LicensedGroupingStatus.NotConfigured or LicensedGroupingStatus.Unavailable
                ? GrouperUnavailablePendCode
                : GroupingPendCode;
            var detail = result.Notes.Count > 0 ? string.Join("; ", result.Notes) : "no further detail";
            return Pend(context, code, $"The stay could not be grouped by APR-DRG ({result.Status}): {detail}");
        }

        var method = await _settings
            .ResolveAsync(context.TenantId, MedicaidDrgMethodSettingKey, context.SettingScopeForClaim(), asOf, ct)
            .ConfigureAwait(false);
        if (method.IsFallback)
        {
            // Not knowing whether the stay is paid by the state method or by
            // the contract would price it by whichever the default says, which
            // for a Texas Medicaid line is the wrong one.
            context.InpatientGrouping = new InpatientGroupingOutcome(
                PricedDrg: null, billedDrg, Result: null, Grouped: false)
            {
                AprDrgResult = result,
            };
            return Pend(context, SettingUnavailablePendCode,
                "Could not determine whether this plan pays inpatient stays by a state Medicaid APR-DRG method.");
        }

        var outcome = new InpatientGroupingOutcome(
            PricedDrg: result.DrgSoiKey, billedDrg, Result: null, Grouped: true)
        {
            AprDrgResult = result,
            MedicaidDrgMethod = MedicaidDrgMethods.Normalize(method.Value),
        };
        context.InpatientGrouping = outcome;

        if (outcome.DisagreesWithFacility)
        {
            _logger.LogInformation(
                "Claim {ClaimVersionId} grouped to APR-DRG {GroupedDrg}; the facility billed {BilledDrg}",
                LogSanitizer.Sanitize(context.ClaimVersionId),
                LogSanitizer.Sanitize(result.DrgSoiKey),
                LogSanitizer.Sanitize(billedDrg));
        }

        return ClaimAdjudicationStageResult.Pass(StageName);
    }

    /// <summary>
    /// The APR-DRG request: the same inputs the MS-DRG request is built from,
    /// plus the admitting diagnosis, admission date and the state's version.
    /// </summary>
    internal static AprDrgGroupingRequest BuildAprDrgRequest(
        ClaimAdjudicationContext context, MsDrgGroupingRequest stay, int version)
    {
        var claim = context.Claim;
        var admitting = claim.DiagnosisCodes
            .FirstOrDefault(d => string.Equals(d.CodeQualifier, "ABJ", StringComparison.OrdinalIgnoreCase));

        return new AprDrgGroupingRequest
        {
            ClaimReference = context.ClaimVersionId,
            PrincipalDiagnosis = stay.PrincipalDiagnosis,
            // BuildRequest keeps every non-principal code, which includes the
            // admitting diagnosis; it travels in its own field here.
            SecondaryDiagnoses = admitting is null
                ? stay.SecondaryDiagnoses
                : stay.SecondaryDiagnoses
                    .Where(d => !string.Equals(d.Code, Normalize(admitting.Code), StringComparison.Ordinal))
                    .ToList(),
            AdmittingDiagnosis = admitting is null || string.IsNullOrWhiteSpace(admitting.Code)
                ? null
                : Normalize(admitting.Code),
            Procedures = stay.Procedures,
            AdmissionDate = claim.Institutional?.AdmissionDate is { } admitted
                ? DateOnly.FromDateTime(admitted.Date)
                : null,
            DischargeDate = stay.DischargeDate,
            AgeYears = stay.AgeYears,
            AgeDays = stay.AgeDays,
            Sex = stay.Sex,
            DischargeStatus = stay.DischargeStatus,
            BirthWeightGrams = stay.BirthWeightGrams,
            LengthOfStayDays = stay.LengthOfStayDays,
            RequestedVersion = version > 0 ? version.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            BilledDrg = stay.BilledDrg,
        };
    }

    /// <summary>
    /// Whether this claim is an inpatient stay. An institutional claim with an
    /// admission date: the same test <see cref="PricingStage"/> uses to decide
    /// a claim can present a day count, because the 837I requires the
    /// admission date on an inpatient claim and omits it on an outpatient one.
    /// </summary>
    internal static bool IsInpatient(AdapterClaim claim) =>
        claim.ClaimType == ClaimType.Institutional
        && claim.Institutional is { AdmissionDate: not null };

    /// <summary>
    /// Builds the grouper's request from the claim and the resolved member, or
    /// null when the claim carries no principal diagnosis.
    /// </summary>
    internal static MsDrgGroupingRequest? BuildRequest(
        ClaimAdjudicationContext context, string? billedDrg)
    {
        var claim = context.Claim;
        var institutional = claim.Institutional!;

        // The principal diagnosis is the ABK-qualified one; the 837I allows
        // exactly one. Falling back to the first diagnosis on the claim would
        // group a stay on a secondary, which is the kind of near-miss that
        // pays a plausible wrong DRG, so a claim without one is refused by the
        // caller rather than guessed at here.
        var principal = claim.DiagnosisCodes
            .FirstOrDefault(d => string.Equals(d.CodeQualifier, "ABK", StringComparison.OrdinalIgnoreCase));
        if (principal is null || string.IsNullOrWhiteSpace(principal.Code))
            return null;

        var secondaries = claim.DiagnosisCodes
            .Where(d => !ReferenceEquals(d, principal) && !string.IsNullOrWhiteSpace(d.Code))
            .Select(d => new GroupingDiagnosis(Normalize(d.Code), d.PresentOnAdmission))
            .ToList();

        var procedures = new List<GroupingProcedure>();
        if (institutional.PrincipalProcedure is { Code: { Length: > 0 } } principalProcedure)
        {
            procedures.Add(new GroupingProcedure(
                Normalize(principalProcedure.Code), DateOnlyOrNull(principalProcedure.Date)));
        }
        procedures.AddRange(institutional.OtherProcedures
            .Where(p => !string.IsNullOrWhiteSpace(p.Code))
            .Select(p => new GroupingProcedure(Normalize(p.Code), DateOnlyOrNull(p.Date))));

        // Discharge date selects the grouper version, so it has to be the best
        // available and not merely present: the discharge itself, then the
        // statement through date on an interim bill, then the claim's service
        // period. ServiceDateTo is non-nullable and default on a claim that
        // never set it, which would group a 1-January-0001 stay against no
        // tables at all.
        var dischargeDate = institutional.DischargeDate
            ?? institutional.StatementToDate
            ?? (claim.ServiceDateTo != default ? claim.ServiceDateTo : claim.ServiceDateFrom);

        var member = context.ResolvedMember;

        return new MsDrgGroupingRequest
        {
            PrincipalDiagnosis = new GroupingDiagnosis(
                Normalize(principal.Code), principal.PresentOnAdmission),
            SecondaryDiagnoses = secondaries,
            Procedures = procedures,
            DischargeDate = DateOnly.FromDateTime(dischargeDate.Date),
            AgeYears = AgeYearsAt(member?.DateOfBirth, institutional.AdmissionDate),
            AgeDays = AgeDaysAt(member?.DateOfBirth, institutional.AdmissionDate),
            Sex = Sex(member?.Gender),
            DischargeStatus = institutional.PatientStatusCode,
            LengthOfStayDays = institutional.CalculateLengthOfStay(),
            BirthWeightGrams = BirthWeightGrams(institutional),
            // Present-on-admission reporting is a property of the facility,
            // not of the claim, and nothing in CHO holds it yet — no field on
            // the provider record, no importer. Unknown is CMS's own answer to
            // not knowing, and in FormulaMsDrgGrouper over the imported CMS
            // tables it refuses only a stay that actually carries a
            // hospital-acquired condition, so the great majority of claims
            // group and the ones where the distinction pays differently pend
            // instead of being guessed at. See ADR 024 step 8.
            PoaReporting = HospitalPoaReporting.Unknown,
            BilledDrg = billedDrg,
        };
    }

    /// <summary>
    /// 837I value code 54 carries the birth weight in grams, which MDC 15
    /// groups a neonate on.
    /// </summary>
    private static int? BirthWeightGrams(InstitutionalClaimDetails institutional) =>
        institutional.ValueCodes
            .FirstOrDefault(v => string.Equals(v.Code, "54", StringComparison.Ordinal)) is { Amount: { } grams }
        && grams > 0
            ? (int)decimal.Truncate(grams)
            : null;

    /// <summary>
    /// The grouper's tables are keyed without the decimal point and in upper
    /// case; claims carry codes either way.
    /// </summary>
    private static string Normalize(string code) =>
        code.Replace(".", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();

    private static DateOnly? DateOnlyOrNull(DateTime? value) =>
        value is { } date ? DateOnly.FromDateTime(date.Date) : null;

    internal static int? AgeYearsAt(DateTime? dateOfBirth, DateTime? admission)
    {
        if (dateOfBirth is not { } born || admission is not { } admitted
            || admitted.Date < born.Date)
        {
            return null;
        }

        var years = admitted.Year - born.Year;
        if (admitted.Date < born.Date.AddYears(years)) years--;
        return years;
    }

    private static int? AgeDaysAt(DateTime? dateOfBirth, DateTime? admission) =>
        dateOfBirth is { } born && admission is { } admitted && admitted.Date >= born.Date
            ? (admitted.Date - born.Date).Days
            : null;

    /// <summary>
    /// The grouper takes administrative sex as M, F or U. A code CHO cannot
    /// interpret maps to null rather than to U: U is a claim that says the sex
    /// is unknown, and the two are different facts to anyone reading the
    /// grouping afterwards.
    /// </summary>
    private static string? Sex(string? gender) => gender?.Trim().ToUpperInvariant() switch
    {
        "M" or "MALE" => "M",
        "F" or "FEMALE" => "F",
        "U" or "UNKNOWN" => "U",
        _ => null,
    };

    /// <summary>
    /// A line of the grouper's own reasoning, for the pend reason. The
    /// grouper's notes say which edit or table refused the stay, which is what
    /// a reviewer needs; the status alone sends them looking.
    /// </summary>
    private static string Explain(MsDrgGroupingResult result) =>
        result.Notes.Count > 0
            ? string.Join("; ", result.Notes)
            : "the grouper gave no further detail";

    private static ClaimAdjudicationStageResult Pend(
        ClaimAdjudicationContext context,
        string pendCode,
        string reason)
    {
        // An earlier stage's pend is the one the work queue sees, as in
        // PricingStage.
        context.PendDetails ??= new PendDetails
        {
            PendCode = pendCode,
            PendReason = reason is { Length: > 500 } ? reason[..500] : reason,
            PendedAt = DateTime.UtcNow,
        };
        return ClaimAdjudicationStageResult.Pend(StageName, reason);
    }
}
