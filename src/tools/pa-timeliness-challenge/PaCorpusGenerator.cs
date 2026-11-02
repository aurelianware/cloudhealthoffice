namespace CloudHealthOffice.Tools.PaTimelinessChallenge;

/// <summary>
/// Builds a deterministic synthetic prior-authorization corpus and its answer
/// key. The same <see cref="PaCorpusProfile"/> (including <c>AsOf</c>) always
/// yields byte-identical output. All data is synthetic.
/// </summary>
public static class PaCorpusGenerator
{
    public const string Version = "pa-timeliness-challenge/1";
    public const string SourceSystem = "pa-timeliness-challenge";

    private sealed record Service(string Code, string System, string Description);

    private static readonly Service[] MedicalServices =
    [
        new("70553", "CPT", "MRI brain without and with contrast"),
        new("72148", "CPT", "MRI lumbar spine without contrast"),
        new("27447", "CPT", "Total knee arthroplasty"),
        new("29881", "CPT", "Knee arthroscopy with meniscectomy"),
        new("43239", "CPT", "Upper GI endoscopy with biopsy"),
        new("97110", "CPT", "Therapeutic exercise"),
        new("95810", "CPT", "Polysomnography"),
        new("63030", "CPT", "Lumbar laminotomy"),
        new("81211", "CPT", "BRCA1/BRCA2 sequence analysis"),
        new("E0601", "HCPCS", "CPAP device"),
        new("L1832", "HCPCS", "Knee orthosis, adjustable"),
        new("G0283", "HCPCS", "Electrical stimulation, unattended"),
    ];

    private static readonly Service[] DrugServices =
    [
        new("J1745", "HCPCS", "Infliximab injection, 10 mg"),
        new("J9312", "HCPCS", "Rituximab injection, 10 mg"),
        new("0002751001", "NDC", "Synthetic NDC drug product"),
        new("0169413212", "NDC", "Synthetic NDC drug product"),
    ];

    private static readonly string[] Diagnoses = ["M17.11", "M54.16", "G43.909", "G47.33", "K21.9", "M51.26", "C50.911", "J45.40"];

    private static readonly (string Code, string Reason)[] DenialReasons =
    [
        ("PTC-MN01", "Medical necessity criteria not met for the requested service."),
        ("PTC-DOC02", "Submitted documentation does not support the request."),
        ("PTC-NC03", "Service is not a covered benefit under the member's plan."),
        ("PTC-ALT04", "A less intensive alternative has not been tried first."),
        ("PTC-QTY05", "Requested quantity exceeds the plan's limit for this service."),
    ];

    private static readonly string[] FirstNames = ["Avery", "Jordan", "Riley", "Casey", "Morgan", "Quinn", "Rowan", "Sage", "Emerson", "Hayden", "Parker", "Reese"];
    private static readonly string[] LastNames = ["Synthetic", "Sample", "Testcase", "Fixture", "Example", "Placeholder", "Mockwell", "Demoson"];

    public static IReadOnlyList<PaCase> Generate(PaCorpusProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        var random = new Random(profile.Seed);
        var liveCount = (int)Math.Round(profile.Count * profile.LiveFraction, MidpointRounding.AwayFromZero);
        var historyCount = profile.Count - liveCount;

        var members = Enumerable.Range(1, Math.Max(1, profile.Count / 4)).Select(i => Member(random, i)).ToArray();
        var providers = Enumerable.Range(1, Math.Max(2, profile.Count / 50)).Select(i => Provider(random, i)).ToArray();

        var cases = new List<PaCase>(profile.Count);
        var historyPlan = Allocate(profile.HistoryWeights, historyCount);
        var livePlan = Allocate(profile.LiveWeights, liveCount);
        Shuffle(random, historyPlan);
        Shuffle(random, livePlan);

        var index = 0;
        foreach (var scenario in historyPlan)
            cases.Add(Build(random, profile, PaCohort.History, scenario, ++index, members, providers));
        foreach (var scenario in livePlan)
            cases.Add(Build(random, profile, PaCohort.Live, scenario, ++index, members, providers));
        return cases;
    }

    /// <summary>
    /// Apportions <paramref name="count"/> across the weights by largest remainder, so every
    /// scenario with a positive weight appears when the count allows and totals are exact.
    /// </summary>
    internal static List<PaScenario> Allocate(IReadOnlyDictionary<PaScenario, int> weights, int count)
    {
        var positive = weights.Where(w => w.Value > 0).OrderBy(w => w.Key).ToList();
        var result = new List<PaScenario>(count);
        if (count == 0 || positive.Count == 0) return result;
        var total = positive.Sum(w => w.Value);
        var shares = positive.Select(w => (w.Key, Exact: (double)count * w.Value / total)).ToList();
        var counts = shares.ToDictionary(s => s.Key, s => (int)Math.Floor(s.Exact));
        var remaining = count - counts.Values.Sum();
        foreach (var s in shares.OrderByDescending(s => s.Exact - Math.Floor(s.Exact)).ThenBy(s => s.Key).Take(remaining))
            counts[s.Key]++;
        foreach (var (scenario, n) in counts.OrderBy(c => c.Key))
            result.AddRange(Enumerable.Repeat(scenario, n));
        return result;
    }

    private static PaCase Build(Random random, PaCorpusProfile profile, PaCohort cohort, PaScenario scenario, int index,
        PaMember[] members, PaProvider[] providers)
    {
        var lob = scenario switch
        {
            PaScenario.ExtendedDecidedInExtension or PaScenario.ExtendedDecidedLate =>
                random.Next(2) == 0 ? PaLineOfBusiness.Medicare : PaLineOfBusiness.Medicaid,
            PaScenario.ExtensionRefusedForLineOfBusiness =>
                random.Next(2) == 0 ? PaLineOfBusiness.Commercial : PaLineOfBusiness.Exchange,
            _ => Pick(random, profile.LineOfBusinessWeights),
        };
        var levelOfService = random.NextDouble() < profile.ExpeditedFraction ? "U" : "E";
        var baseDeadline = TimeSpan.FromHours(PaTimeframes.BaseDeadlineHours(levelOfService));

        var receivedAt = cohort == PaCohort.History
            ? RandomInstant(random, profile.ResolvedPeriodStart, profile.ResolvedPeriodEnd)
            : profile.AsOf.ToUniversalTime();

        var drug = scenario == PaScenario.DrugExcluded;
        var lineCount = scenario == PaScenario.PartiallyApproved ? random.Next(2, 4) : random.Next(1, 3);
        var catalog = drug ? DrugServices : MedicalServices;
        var services = Enumerable.Range(0, lineCount).Select(_ => catalog[random.Next(catalog.Length)]).ToList();

        // Initial decision, and when (live cases: the reviewer acts promptly; timing is observed).
        var initial = scenario switch
        {
            PaScenario.Undecided => PaOutcome.Open,
            PaScenario.Denied or PaScenario.DeniedThenApprovedOnAppeal => PaOutcome.Denied,
            PaScenario.PartiallyApproved => PaOutcome.Modified,
            PaScenario.DrugExcluded or PaScenario.LateDecision or PaScenario.DecidedOneMinuteLate
                or PaScenario.ExtendedDecidedLate => random.Next(4) == 0 ? PaOutcome.Denied : PaOutcome.Approved,
            _ => PaOutcome.Approved,
        };

        PaExtensionScript? extension = null;
        if (scenario is PaScenario.ExtendedDecidedInExtension or PaScenario.ExtendedDecidedLate
            or PaScenario.ExtensionRefusedForLineOfBusiness)
        {
            var grantedAfter = cohort == PaCohort.History
                ? baseDeadline * (0.4 + 0.5 * random.NextDouble())
                : TimeSpan.Zero;
            extension = new PaExtensionScript(
                RoundToMinute(grantedAfter),
                PaTimeframes.MaxExtension,
                "Additional clinical information requested from the provider is needed to decide.");
        }
        var lawfulExtension = extension is not null && scenario != PaScenario.ExtensionRefusedForLineOfBusiness;
        var deadline = baseDeadline + (lawfulExtension ? extension!.ExtendedBy : TimeSpan.Zero);

        TimeSpan? decidedAfter = initial == PaOutcome.Open ? null : cohort == PaCohort.Live ? TimeSpan.Zero : scenario switch
        {
            PaScenario.DecidedAtDeadline => baseDeadline,
            PaScenario.DecidedOneMinuteLate => baseDeadline + TimeSpan.FromMinutes(1),
            PaScenario.LateDecision => baseDeadline + TimeSpan.FromHours(2 + 70 * random.NextDouble()),
            PaScenario.ExtendedDecidedInExtension =>
                baseDeadline + TimeSpan.FromHours(1 + (extension!.ExtendedBy.TotalHours - 2) * random.NextDouble()),
            PaScenario.ExtendedDecidedLate => deadline + TimeSpan.FromHours(1 + 47 * random.NextDouble()),
            _ => TimeSpan.FromHours(0.5) + (baseDeadline - TimeSpan.FromHours(1)) * random.NextDouble(),
        };
        if (decidedAfter is { } d && scenario is not (PaScenario.DecidedAtDeadline or PaScenario.DecidedOneMinuteLate))
            decidedAfter = RoundToMinute(d);

        var reason = DenialReasons[random.Next(DenialReasons.Length)];
        var lines = new List<PaServiceLine>(lineCount);
        for (var i = 0; i < lineCount; i++)
        {
            var units = random.Next(1, 13);
            var lineOutcome = initial switch
            {
                PaOutcome.Open => PaOutcome.Open,
                PaOutcome.Modified => i == 0 ? PaOutcome.Approved : (i == 1 || random.Next(2) == 0 ? PaOutcome.Denied : PaOutcome.Approved),
                _ => initial,
            };
            var lineReason = lineOutcome == PaOutcome.Denied ? DenialReasons[(Array.IndexOf(DenialReasons, reason) + i) % DenialReasons.Length] : default;
            lines.Add(new PaServiceLine(
                i + 1, services[i].Code, services[i].System, services[i].Description, units, lineOutcome,
                lineOutcome == PaOutcome.Approved ? units : lineOutcome == PaOutcome.Denied ? 0 : null,
                lineReason.Code, lineReason.Reason));
        }

        PaAppealScript? appeal = scenario == PaScenario.DeniedThenApprovedOnAppeal
            ? new PaAppealScript(RoundToMinute(decidedAfter!.Value + TimeSpan.FromDays(5 + 20 * random.NextDouble())), PaOutcome.Approved)
            : null;

        var decision = decidedAfter is { } after
            ? new PaDecisionScript(initial, after,
                initial == PaOutcome.Denied ? reason.Code : null,
                initial == PaOutcome.Denied ? reason.Reason : null)
            : null;

        var adverse = initial is PaOutcome.Denied or PaOutcome.Modified
            ? lines.Where(l => l.Decision == PaOutcome.Denied)
                .Select(l => new PaExpectedAdverseReason(l.LineNumber, l.DenialReasonCode!)).ToList()
            : new List<PaExpectedAdverseReason>();

        var expected = new PaExpectation
        {
            InScope = !drug,
            Expedited = PaTimeframes.IsExpedited(levelOfService),
            DeadlineHours = deadline.TotalHours,
            InitialOutcome = initial,
            FinalOutcome = appeal?.Outcome ?? initial,
            DecisionHours = cohort == PaCohort.History ? decidedAfter?.TotalHours : null,
            DecidedWithinTimeframe = cohort == PaCohort.History && decidedAfter is { } x ? x <= deadline : null,
            Extended = lawfulExtension,
            ExtensionMustBeRefused = scenario == PaScenario.ExtensionRefusedForLineOfBusiness,
            ApprovedAfterAppeal = appeal is { Outcome: PaOutcome.Approved or PaOutcome.Modified },
            AdverseReasons = adverse,
        };

        var requesting = providers[random.Next(providers.Length)];
        return new PaCase
        {
            CaseId = $"PTC-{index:D6}",
            AuthorizationNumber = $"PTC{profile.Seed}-{index:D6}",
            Cohort = cohort,
            Scenario = scenario,
            LineOfBusiness = lob,
            LevelOfService = levelOfService,
            ReceivedAt = receivedAt,
            Member = members[random.Next(members.Length)],
            RequestingProvider = requesting,
            ServicingProvider = random.Next(3) == 0 ? requesting : providers[random.Next(providers.Length)],
            ServiceTypeCode = drug ? "88" : "1",
            DiagnosisCodes = [Diagnoses[random.Next(Diagnoses.Length)]],
            Lines = lines,
            Decision = decision,
            Extension = extension,
            Appeal = appeal,
            Expected = expected,
        };
    }

    private static PaMember Member(Random random, int i) => new(
        $"PTCM{i:D7}",
        FirstNames[random.Next(FirstNames.Length)],
        LastNames[random.Next(LastNames.Length)],
        new DateOnly(1940, 1, 1).AddDays(random.Next(0, 365 * 80)));

    private static PaProvider Provider(Random random, int i)
    {
        // Nine-digit base starting with 9 (not a range NPPES has issued), then the Luhn check digit.
        var body = $"9{random.Next(10_000_000, 99_999_999)}";
        return new PaProvider(body + NpiCheckDigit(body), $"Synthetic Clinic {i:D4}");
    }

    /// <summary>The NPI check digit: Luhn over the 80840-prefixed identifier.</summary>
    internal static int NpiCheckDigit(string nineDigits)
    {
        var digits = "80840" + nineDigits;
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var n = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 0) { n *= 2; if (n > 9) n -= 9; }
            sum += n;
        }
        return (10 - sum % 10) % 10;
    }

    private static T Pick<T>(Random random, IReadOnlyDictionary<T, int> weights) where T : notnull
    {
        var ordered = weights.Where(w => w.Value > 0).OrderBy(w => w.Key).ToList();
        var roll = random.Next(ordered.Sum(w => w.Value));
        foreach (var (key, weight) in ordered)
        {
            if (roll < weight) return key;
            roll -= weight;
        }
        return ordered[^1].Key;
    }

    private static DateTimeOffset RandomInstant(Random random, DateTimeOffset from, DateTimeOffset to)
    {
        var minutes = (long)(to - from).TotalMinutes;
        return from.AddMinutes((long)(random.NextDouble() * minutes));
    }

    private static TimeSpan RoundToMinute(TimeSpan value) => TimeSpan.FromMinutes(Math.Round(value.TotalMinutes));

    private static void Shuffle<T>(Random random, List<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
