using System.Globalization;
using CloudHealthOffice.Tools.PaTimelinessChallenge;

// PA Timeliness Challenge.
//   pa-timeliness-challenge generate --out <dir> [corpus options]
//   pa-timeliness-challenge run --out <dir> --auth-url <url> --fhir-url <url> [--tenant pa-timeliness]
//       [--history-only | --live-only] [--parallelism 8] [corpus options]
// Corpus options: --count 10000 --seed 57 --as-of <ISO> --period-start <ISO> --period-end <ISO>
//       --live-fraction 0.05 --expedited-fraction 0.25
// run reads the bearer token from CHO_BEARER_TOKEN; its tenant must match --tenant.
// Data class: synthetic. Nothing here is a CMS attestation.

const string Usage =
    "usage:\n" +
    "  pa-timeliness-challenge generate --out <dir> [--count N] [--seed N] [--as-of ISO] [--period-start ISO] [--period-end ISO]\n" +
    "      [--live-fraction F] [--expedited-fraction F]\n" +
    "  pa-timeliness-challenge run --out <dir> --auth-url URL --fhir-url URL [--tenant ID] [--history-only|--live-only]\n" +
    "      [--parallelism N] [corpus options as for generate]   (token from CHO_BEARER_TOKEN)";

if (args.Length == 0 || args[0] is "-h" or "--help" || args[0] is not ("generate" or "run"))
{
    Console.Error.WriteLine(Usage);
    return args.Length > 0 && args[0] is "-h" or "--help" ? 0 : 2;
}

var command = args[0];
string? output = null, authUrl = null, fhirUrl = null;
var tenant = "pa-timeliness";
bool history = true, live = true;
var parallelism = 8;
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
            case "--auth-url" when command == "run": authUrl = Next(); break;
            case "--fhir-url" when command == "run": fhirUrl = Next(); break;
            case "--tenant" when command == "run": tenant = Next(); break;
            case "--parallelism" when command == "run": parallelism = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--history-only" when command == "run": live = false; break;
            case "--live-only" when command == "run": history = false; break;
            default: throw new ArgumentException($"Unknown option {args[i]} for {command}.");
        }
    }
    if (output is null) throw new ArgumentException("--out is required.");

    if (command == "generate")
    {
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

    if (authUrl is null || fhirUrl is null) throw new ArgumentException("run needs --auth-url and --fhir-url.");
    var token = Environment.GetEnvironmentVariable("CHO_BEARER_TOKEN");
    if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Set CHO_BEARER_TOKEN to a token for the tenant.");
    profile.Validate();

    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    var api = new ChoApi(http, BaseUri(authUrl), BaseUri(fhirUrl), tenant, token);
    var runner = new PaChallengeRunner(api, new PaRunOptions
    {
        AuthorizationUrl = BaseUri(authUrl),
        FhirUrl = BaseUri(fhirUrl),
        TenantId = tenant,
        BearerToken = token,
        OutputDirectory = output,
        History = history,
        Live = live,
        Parallelism = parallelism,
    });
    var summary = await runner.RunAsync(profile, CancellationToken.None);
    Console.WriteLine(summary.ToMarkdown());
    Console.WriteLine($"Summary: {Path.Combine(output, "run-summary.md")} and run-summary.json");
    return summary.Failed == 0 ? 0 : 1;
}
catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or UriFormatException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(Usage);
    return 2;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"CHO could not be reached: {ex.Message}");
    return 2;
}

static DateTimeOffset Instant(string value) =>
    DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

static Uri BaseUri(string value) => new(value.EndsWith('/') ? value : value + "/");
