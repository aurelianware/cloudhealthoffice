using System.Globalization;
using CloudHealthOffice.Tools.PaTimelinessChallenge;

// PA Timeliness Challenge corpus generator.
//   pa-timeliness-challenge generate --out <dir> [--count 10000] [--seed 57]
//       [--as-of 2026-11-02T00:00:00Z] [--period-start ...] [--period-end ...]
//       [--live-fraction 0.05] [--expedited-fraction 0.25]
// Data class: synthetic. Nothing here is a CMS attestation.

if (args.Length == 0 || args[0] is "-h" or "--help" || args[0] != "generate")
{
    Console.Error.WriteLine("usage: pa-timeliness-challenge generate --out <dir> [--count N] [--seed N] [--as-of ISO] " +
        "[--period-start ISO] [--period-end ISO] [--live-fraction F] [--expedited-fraction F]");
    return args.Length > 0 && args[0] is "-h" or "--help" ? 0 : 2;
}

string? output = null;
var profile = new PaCorpusProfile();
try
{
    for (var i = 1; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
        switch (args[i])
        {
            case "--out": output = Next(); break;
            case "--count": profile = profile with { Count = int.Parse(Next(), CultureInfo.InvariantCulture) }; break;
            case "--seed": profile = profile with { Seed = int.Parse(Next(), CultureInfo.InvariantCulture) }; break;
            case "--as-of": profile = profile with { AsOf = Instant(Next()) }; break;
            case "--period-start": profile = profile with { PeriodStart = Instant(Next()) }; break;
            case "--period-end": profile = profile with { PeriodEnd = Instant(Next()) }; break;
            case "--live-fraction": profile = profile with { LiveFraction = double.Parse(Next(), CultureInfo.InvariantCulture) }; break;
            case "--expedited-fraction": profile = profile with { ExpeditedFraction = double.Parse(Next(), CultureInfo.InvariantCulture) }; break;
            default: throw new ArgumentException($"Unknown option {args[i]}.");
        }
    }
    if (output is null) throw new ArgumentException("--out is required.");

    var cases = PaCorpusGenerator.Generate(profile);
    PaCorpusWriter.WriteAll(output, profile, cases);
    var key = PaCorpusWriter.AnswerKey.From(profile, cases);
    var all = key.ExpectedHistoryMetrics[0];
    Console.WriteLine($"Wrote {cases.Count} synthetic PA cases to {output} (seed {profile.Seed}, as of {profile.AsOf.UtcDateTime:O}).");
    Console.WriteLine($"History {key.PeriodStart:yyyy-MM-dd}..{key.PeriodEnd:yyyy-MM-dd}: " +
        $"{cases.Count(c => c.Cohort == PaCohort.History)} cases, {all.TotalRequests} in scope; " +
        $"standard on time {all.Standard.DecidedWithinTimeframePercent}%, expedited on time {all.Expedited.DecidedWithinTimeframePercent}%.");
    Console.WriteLine($"Live: {cases.Count(c => c.Cohort == PaCohort.Live)} cases for PAS $submit.");
    return 0;
}
catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

static DateTimeOffset Instant(string value) =>
    DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
