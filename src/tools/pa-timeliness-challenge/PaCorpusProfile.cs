namespace CloudHealthOffice.Tools.PaTimelinessChallenge;

/// <summary>Size, seed, period and mix of a generated corpus. The same profile always produces the same corpus.</summary>
public sealed record PaCorpusProfile
{
    public int Count { get; init; } = 10_000;
    public int Seed { get; init; } = 57;

    /// <summary>Earliest history receipt (UTC). Default: 1 January of <see cref="AsOf"/>'s year.</summary>
    public DateTimeOffset? PeriodStart { get; init; }

    /// <summary>
    /// Latest history receipt (UTC). Default: <see cref="AsOf"/> minus
    /// <see cref="MinimumSettlingDays"/>, so every scripted decision, extension
    /// and appeal lands before <see cref="AsOf"/> (CHO refuses future evidence).
    /// </summary>
    public DateTimeOffset? PeriodEnd { get; init; }

    /// <summary>The time the corpus is generated for. Pass a fixed value for a reproducible corpus.</summary>
    public DateTimeOffset AsOf { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The share of cases submitted live through PAS instead of imported.</summary>
    public double LiveFraction { get; init; } = 0.05;

    /// <summary>The share of requests that are expedited (level of service U).</summary>
    public double ExpeditedFraction { get; init; } = 0.25;

    /// <summary>Room after the last receipt for the longest script (extension, late decision, appeal).</summary>
    public const int MinimumSettlingDays = 60;

    public static readonly IReadOnlyDictionary<PaLineOfBusiness, int> DefaultLineOfBusinessWeights =
        new Dictionary<PaLineOfBusiness, int>
        {
            [PaLineOfBusiness.Medicare] = 30,
            [PaLineOfBusiness.Medicaid] = 35,
            [PaLineOfBusiness.Commercial] = 20,
            [PaLineOfBusiness.Exchange] = 15,
        };

    public static readonly IReadOnlyDictionary<PaScenario, int> DefaultHistoryWeights =
        new Dictionary<PaScenario, int>
        {
            [PaScenario.Approved] = 40,
            [PaScenario.Denied] = 12,
            [PaScenario.PartiallyApproved] = 8,
            [PaScenario.LateDecision] = 6,
            [PaScenario.DecidedAtDeadline] = 3,
            [PaScenario.DecidedOneMinuteLate] = 3,
            [PaScenario.ExtendedDecidedInExtension] = 6,
            [PaScenario.ExtendedDecidedLate] = 2,
            [PaScenario.DrugExcluded] = 8,
            [PaScenario.Undecided] = 4,
            [PaScenario.DeniedThenApprovedOnAppeal] = 4,
        };

    /// <summary>Live cases cannot be days late on a real clock, so timing scenarios stay in history.</summary>
    public static readonly IReadOnlyDictionary<PaScenario, int> DefaultLiveWeights =
        new Dictionary<PaScenario, int>
        {
            [PaScenario.Approved] = 40,
            [PaScenario.Denied] = 15,
            [PaScenario.PartiallyApproved] = 10,
            [PaScenario.ExtendedDecidedInExtension] = 10,
            [PaScenario.ExtensionRefusedForLineOfBusiness] = 10,
            [PaScenario.DrugExcluded] = 10,
            [PaScenario.Undecided] = 5,
        };

    public IReadOnlyDictionary<PaLineOfBusiness, int> LineOfBusinessWeights { get; init; } = DefaultLineOfBusinessWeights;
    public IReadOnlyDictionary<PaScenario, int> HistoryWeights { get; init; } = DefaultHistoryWeights;
    public IReadOnlyDictionary<PaScenario, int> LiveWeights { get; init; } = DefaultLiveWeights;

    public DateTimeOffset ResolvedPeriodStart =>
        (PeriodStart ?? new DateTimeOffset(AsOf.UtcDateTime.Year, 1, 1, 0, 0, 0, TimeSpan.Zero)).ToUniversalTime();

    public DateTimeOffset ResolvedPeriodEnd =>
        (PeriodEnd ?? AsOf.ToUniversalTime().AddDays(-MinimumSettlingDays)).ToUniversalTime();

    public void Validate()
    {
        if (Count <= 0) throw new ArgumentException("Count must be positive.");
        if (LiveFraction is < 0 or > 1) throw new ArgumentException("LiveFraction must be between 0 and 1.");
        if (ExpeditedFraction is < 0 or > 1) throw new ArgumentException("ExpeditedFraction must be between 0 and 1.");
        if (ResolvedPeriodEnd <= ResolvedPeriodStart)
            throw new ArgumentException(
                $"The history period is empty ({ResolvedPeriodStart:O} to {ResolvedPeriodEnd:O}). " +
                $"Receipts must end at least {MinimumSettlingDays} days before as-of; pass an earlier period start or a later as-of.");
        if (ResolvedPeriodEnd > AsOf.ToUniversalTime().AddDays(-MinimumSettlingDays))
            throw new ArgumentException($"The history period must end at least {MinimumSettlingDays} days before as-of.");
        if (LiveWeights.ContainsKey(PaScenario.LateDecision) || LiveWeights.ContainsKey(PaScenario.DecidedAtDeadline)
            || LiveWeights.ContainsKey(PaScenario.DecidedOneMinuteLate) || LiveWeights.ContainsKey(PaScenario.ExtendedDecidedLate)
            || LiveWeights.ContainsKey(PaScenario.DeniedThenApprovedOnAppeal))
            throw new ArgumentException("Timing and appeal scenarios are history-only: a live clock cannot be scripted.");
        if (HistoryWeights.ContainsKey(PaScenario.ExtensionRefusedForLineOfBusiness))
            throw new ArgumentException("An imported record cannot be extended in CHO, so the refusal scenario is live-only.");
        if (HistoryWeights.Values.Concat(LiveWeights.Values).Concat(LineOfBusinessWeights.Values).Any(w => w < 0))
            throw new ArgumentException("Weights cannot be negative.");
    }
}
