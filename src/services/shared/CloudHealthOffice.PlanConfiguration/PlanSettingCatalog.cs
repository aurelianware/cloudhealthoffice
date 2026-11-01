namespace CloudHealthOffice.PlanConfiguration;

/// <summary>
/// The setting definitions CHO knows. Definitions ship here, in code, so the
/// catalog tenant-service serves and the definitions each owning service
/// resolves are the same build (plan-configuration.md §4). A service adds its
/// setting to <see cref="PlanSettings"/>; tenant-service needs no change to
/// accept values for it. A service can call <see cref="Require"/> at startup
/// to fail fast on a key that is not defined.
/// </summary>
public static class PlanSettingCatalog
{
    private static readonly IReadOnlyDictionary<string, SettingDefinition> ByKey =
        PlanSettings.All.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public static IReadOnlyCollection<SettingDefinition> All => (IReadOnlyCollection<SettingDefinition>)ByKey.Values;

    public static SettingDefinition? Find(string key) => ByKey.TryGetValue(key, out var d) ? d : null;

    public static SettingDefinition Require(string key) =>
        Find(key) ?? throw new InvalidOperationException($"Setting '{key}' is not defined in the plan configuration catalog.");
}

/// <summary>The catalog as a service, so tenant-service can be tested with definitions of its own.</summary>
public interface ISettingCatalog
{
    IReadOnlyCollection<SettingDefinition> All { get; }
    SettingDefinition? Find(string key);
}

/// <summary>The catalog shipped in code (<see cref="PlanSettings"/>).</summary>
public sealed class BuiltInSettingCatalog : ISettingCatalog
{
    public IReadOnlyCollection<SettingDefinition> All => PlanSettingCatalog.All;
    public SettingDefinition? Find(string key) => PlanSettingCatalog.Find(key);
}

/// <summary>Every defined setting. Add new ones here.</summary>
public static class PlanSettings
{
    /// <summary>
    /// Days a dependent whose subscriber is not on file is held before it
    /// reaches the Matching queue (enrollment-operations.md §10, decision 5).
    /// One file cycle for commercial groups; none where the sponsor sends
    /// whole enrollment groups (Marketplace) or is the source of truth
    /// (Medicaid). Benefit plan and sponsor policy levels join when those
    /// records exist.
    /// </summary>
    public static readonly SettingDefinition OrphanGraceDays = new()
    {
        Key = "enrollment.orphan.graceDays",
        Type = SettingType.Integer,
        Owner = "enrollment-import-service",
        Description = "Days an orphaned dependent (subscriber not on file) is held for its subscriber before it opens in the Matching queue.",
        Unit = "days",
        Default = "0",
        DefaultsByLineOfBusiness = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [LinesOfBusiness.Commercial] = "7",
            [LinesOfBusiness.Exchange] = "0",
            [LinesOfBusiness.Medicaid] = "0",
        },
        AllowedLevels = new HashSet<SettingLevel> { SettingLevel.Tenant, SettingLevel.LineOfBusiness, SettingLevel.Sponsor },
        Constraints = new SettingConstraints { Min = 0, Max = 90 },
        ChangeControl = ChangeControl.ReasonRequired,
    };

    /// <summary>
    /// Whether an inpatient claim is priced by the DRG CHO's grouper assigns
    /// rather than the one the facility billed (ADR 024 step 8).
    ///
    /// <para>
    /// Off everywhere by default, and deliberately: turning it on changes
    /// which DRG rate row an inpatient claim prices against, so it changes
    /// paid amounts. A payer adopts it one line of business at a time, after
    /// it has run its own inpatient history through the grouper and looked at
    /// the differences, not because a release shipped.
    /// </para>
    ///
    /// <para>
    /// Medicare has no higher default than the others here. CHO's grouper is
    /// held to CMS's own grouper on a 33-stay corpus, which is enough to show
    /// the algorithm agrees and nothing like a real plan's case mix; a
    /// Medicare default of true would be this repository deciding that for
    /// every deployment.
    /// </para>
    ///
    /// <para>
    /// Benefit plan, sponsor and sponsor policy joined tenant and line of
    /// business in plan-configuration step 3d's last consumer (§12.16). The
    /// level this entry deferred for — a payer piloting the grouper on one
    /// plan — is now settable, and so is the one a payer is likelier to reach
    /// for: a single employer's policy, where stay pricing is actually
    /// negotiated. Declaring a level only once adjudication can see it is the
    /// rule this entry was written to respect; what changed is that
    /// adjudication now can.
    /// </para>
    ///
    /// <para>
    /// Class is deliberately not here. An eligibility class divides a
    /// policy's members by who they are, and whether a stay is priced on the
    /// billed or the grouped DRG is a property of the contract, not of the
    /// member — a class-level value would let one employee's stay be priced by
    /// a different method from a colleague's.
    /// </para>
    /// </summary>
    public static readonly SettingDefinition GroupInpatientDrg = new()
    {
        Key = "pricing.inpatient.groupDrg",
        Type = SettingType.Boolean,
        Owner = "claims-service",
        Description = "Price an inpatient claim by the DRG CHO's grouper assigns instead of the DRG the facility billed.",
        Default = "false",
        AllowedLevels = new HashSet<SettingLevel>
        {
            SettingLevel.Tenant,
            SettingLevel.LineOfBusiness,
            SettingLevel.BenefitPlan,
            SettingLevel.Sponsor,
            SettingLevel.SponsorPolicy,
        },
        ChangeControl = ChangeControl.ReasonRequired,
    };

    /// <summary>
    /// Which classification an inpatient claim is grouped by when
    /// <see cref="GroupInpatientDrg"/> is on: <c>msDrg</c>, CHO's own MS-DRG
    /// grouper (Medicare and most commercial contracts), or <c>aprDrg</c>,
    /// the plan's licensed 3M APR-DRG grouper, which Texas, California and
    /// most other Medicaid programs price by. The APR-DRG answer prices as
    /// "DRG-SOI" (e.g. <c>139-3</c>), the key state weight tables use.
    ///
    /// <para>
    /// Set it per line of business: a plan with Medicare and Medicaid lines
    /// groups the same hospital's stays both ways.
    /// </para>
    /// </summary>
    public static readonly SettingDefinition InpatientDrgClassification = new()
    {
        Key = "pricing.inpatient.drgClassification",
        Type = SettingType.Enum,
        Owner = "claims-service",
        Description = "Group inpatient claims by MS-DRG (CHO's grouper) or APR-DRG (the plan's licensed grouper).",
        Default = "\"msDrg\"",
        AllowedLevels = new HashSet<SettingLevel>
        {
            SettingLevel.Tenant,
            SettingLevel.LineOfBusiness,
            SettingLevel.BenefitPlan,
            SettingLevel.Sponsor,
            SettingLevel.SponsorPolicy,
        },
        Constraints = new SettingConstraints { AllowedValues = ["msDrg", "aprDrg"] },
        ChangeControl = ChangeControl.ReasonRequired,
    };

    /// <summary>
    /// The APR-DRG major version the state prices discharges under, e.g. 40.
    /// The licensed grouper must answer in exactly this version, because
    /// state relative weights are published per version. 0, the default,
    /// leaves the version to the grouper. Effective-dated like every
    /// setting, so a state's version change on its rate year is one dated
    /// value.
    /// </summary>
    public static readonly SettingDefinition AprDrgVersion = new()
    {
        Key = "pricing.inpatient.aprDrgVersion",
        Type = SettingType.Integer,
        Owner = "claims-service",
        Description = "APR-DRG version the state prices by (0 lets the grouper choose).",
        Default = "0",
        AllowedLevels = new HashSet<SettingLevel>
        {
            SettingLevel.Tenant,
            SettingLevel.LineOfBusiness,
            SettingLevel.BenefitPlan,
        },
        Constraints = new SettingConstraints { Min = 0, Max = 99 },
        ChangeControl = ChangeControl.ReasonRequired,
    };

    /// <summary>
    /// Whether releasing money needs a second user (maker-checker):
    /// capitation disbursements and premium drafts cannot be released by the
    /// person who prepared them (plan-configuration.md §12 step 2b).
    ///
    /// <para>
    /// Default <c>true</c>, which is also what the client answers when
    /// tenant-service cannot be reached, so an outage never turns the rule off.
    /// Only the tenant level: this is an internal control over the tenant's own
    /// staff, not something a sponsor or a program negotiates. Turning it off
    /// needs a second person with <c>config:approve</c>, because a control that
    /// one person can remove alone is not a two-person control.
    /// </para>
    /// </summary>
    public static readonly SettingDefinition PaymentEnforceSeparationOfDuties = new()
    {
        Key = "payment.enforceSeparationOfDuties",
        Type = SettingType.Boolean,
        Owner = "capitation-service",
        Description = "Whether releasing a payment run needs a user other than the one who prepared it (maker-checker).",
        Default = "true",
        AllowedLevels = new HashSet<SettingLevel> { SettingLevel.Tenant },
        ChangeControl = ChangeControl.TwoPersonApproval,
    };

    /// <summary>
    /// Whether activating a personal representative needs a user other than the
    /// one who established it (plan-configuration.md §12 step 2b).
    ///
    /// <para>
    /// The same shape and the same reasoning as
    /// <see cref="PaymentEnforceSeparationOfDuties"/>. What is being decided is
    /// who may act on somebody's protected health information on their behalf,
    /// so the default is <c>true</c> and the fallback answer is <c>true</c>.
    /// </para>
    /// </summary>
    public static readonly SettingDefinition PersonalRepresentativeRequireSecondUserActivation = new()
    {
        Key = "personalRepresentative.requireSecondUserActivation",
        Type = SettingType.Boolean,
        Owner = "personal-representative-service",
        Description = "Whether activating a personal representative needs a user other than the one who established it.",
        Default = "true",
        AllowedLevels = new HashSet<SettingLevel> { SettingLevel.Tenant },
        ChangeControl = ChangeControl.TwoPersonApproval,
    };

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        OrphanGraceDays,
        GroupInpatientDrg,
        InpatientDrgClassification,
        AprDrgVersion,
        PaymentEnforceSeparationOfDuties,
        PersonalRepresentativeRequireSecondUserActivation,
    ];
}
