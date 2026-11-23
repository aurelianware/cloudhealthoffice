using System.Globalization;
using CloudHealthOffice.Infrastructure.Import.Cpcds;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ClaimsService.Services.Adjudication.Shadow;

/// <summary>
/// What CHO's shadow run says about a claim someone has appealed, in one word.
/// Advisory: the appeal is decided by the plan's reviewer, never by CHO.
/// </summary>
public static class ShadowAppealAdvice
{
    /// <summary>The core denied the claim, or a line, and CHO would have paid it.</summary>
    public const string ChoWouldPay = "ChoWouldPay";

    /// <summary>The core paid, and CHO would have paid more.</summary>
    public const string ChoWouldPayMore = "ChoWouldPayMore";

    /// <summary>Both denied, for different reasons (CARCs).</summary>
    public const string ChoDeniesForAnotherReason = "ChoDeniesForAnotherReason";

    /// <summary>CHO denies it too, with the same CARC; or pays the same.</summary>
    public const string ChoConfirms = "ChoConfirms";

    /// <summary>CHO could not decide (missing contract, member, ...).</summary>
    public const string ChoUndecided = "ChoUndecided";

    /// <summary>The core had not decided the claim when it was shadowed.</summary>
    public const string NotComparable = "NotComparable";

    public static readonly IReadOnlyList<string> All =
        [ChoWouldPay, ChoWouldPayMore, ChoDeniesForAnotherReason, ChoConfirms, ChoUndecided, NotComparable];

    public static string For(ShadowAdjudicationResult r, decimal tolerance = 0.01m)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Kind == ShadowDiscrepancyKind.NotComparable) return NotComparable;
        if (r.Kind == ShadowDiscrepancyKind.ChoUndecided) return ChoUndecided;

        if (r.CoreOutcome == ShadowOutcome.Denied)
        {
            if (r.ChoOutcome == ShadowOutcome.Paid) return ChoWouldPay;
            return SameCode(r.CoreDenialCode, r.ChoDenialCode) ? ChoConfirms : ChoDeniesForAnotherReason;
        }

        // The core paid. A line it denied that CHO would pay is a denial CHO
        // would overturn; a line both denied is compared by code.
        var deniedLines = r.Lines.Where(l => l.CoreDenialCode is not null).ToList();
        if (deniedLines.Any(l => l.ChoDenialCode is null && (l.ChoPaid ?? 0m) > 0m)) return ChoWouldPay;
        if (r.ChoOutcome == ShadowOutcome.Paid && r.PaidVariance > tolerance) return ChoWouldPayMore;
        if (deniedLines.Any(l => l.ChoDenialCode is not null && !SameCode(l.CoreDenialCode, l.ChoDenialCode)))
            return ChoDeniesForAnotherReason;
        return ChoConfirms;
    }

    /// <summary>The appeal is likely to be overturned, by CHO's reading: CHO would have paid what was denied.</summary>
    public static bool LikelyOverturn(string advice) => advice is ChoWouldPay or ChoWouldPayMore;

    private static bool SameCode(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    public static string Describe(string advice) => advice switch
    {
        ChoWouldPay => "CHO would have paid what the core denied.",
        ChoWouldPayMore => "CHO would have paid more than the core did.",
        ChoDeniesForAnotherReason => "CHO denies it too, for a different reason.",
        ChoConfirms => "CHO reaches the same decision as the core.",
        ChoUndecided => "CHO could not decide this claim with the data loaded.",
        _ => "The core had not decided the claim when CHO ran it.",
    };
}

/// <summary>One appeal decision on a claim, from the plan's appeals system (or CHO's).</summary>
[BsonIgnoreExtraElements]
public sealed class ShadowAppealOutcome
{
    /// <summary>"{tenant}:{claim number}:{appeal number}".</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string AppealNumber { get; set; } = string.Empty;
    public string? AppealLevel { get; set; }

    /// <summary>One of <see cref="ShadowAppealDecision"/>.</summary>
    public string Decision { get; set; } = string.Empty;
    public DateTime? DecidedDate { get; set; }

    /// <summary>The CARC the appeal was about, as the file gives it.</summary>
    public string? DenialCode { get; set; }

    public DateTime ImportedAtUtc { get; set; }
    public string? ImportedBy { get; set; }

    public static string IdFor(string tenantId, string claimNumber, string appealNumber) =>
        $"{tenantId}:{claimNumber}:{appealNumber}";
}

public static class ShadowAppealDecision
{
    public const string Overturned = "Overturned";
    public const string PartiallyOverturned = "PartiallyOverturned";
    public const string Upheld = "Upheld";
    public const string Withdrawn = "Withdrawn";

    public static readonly IReadOnlyList<string> All = [Overturned, PartiallyOverturned, Upheld, Withdrawn];

    /// <summary>The usual spellings: overturned / reversed / approved, partial, upheld / affirmed / denied, withdrawn / dismissed.</summary>
    public static string? Parse(string? value) =>
        value?.Trim().ToLowerInvariant().Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty) switch
        {
            "overturned" or "overturn" or "reversed" or "approved" or "favorable" or "fullyoverturned" => Overturned,
            "partiallyoverturned" or "partial" or "partialapproval" or "partiallyfavorable" or "modified" => PartiallyOverturned,
            "upheld" or "uphold" or "affirmed" or "denied" or "unfavorable" or "sustained" => Upheld,
            "withdrawn" or "dismissed" or "closed" => Withdrawn,
            _ => null,
        };

    public static bool IsDecided(string decision) => decision is Overturned or PartiallyOverturned or Upheld;

    public static bool IsOverturned(string decision) => decision is Overturned or PartiallyOverturned;
}

public sealed class ShadowAppealOutcomeImportResult
{
    public int Rows { get; set; }
    public int Loaded { get; set; }
    public int Rejected { get; set; }
    public bool DryRun { get; set; }
    public List<string> FileErrors { get; set; } = [];

    /// <summary>"Line n: why", for the first rows refused.</summary>
    public List<string> RowErrors { get; set; } = [];
}

/// <summary>Reads the plan's appeal outcomes file.</summary>
public static class ShadowAppealOutcomeFile
{
    private static readonly Dictionary<string, string> Columns = new(StringComparer.Ordinal)
    {
        [CpcdsHeaders.Normalize("Claim number")] = "claim",
        [CpcdsHeaders.Normalize("Claim unique identifier")] = "claim",
        [CpcdsHeaders.Normalize("Claim id")] = "claim",
        [CpcdsHeaders.Normalize("Appeal number")] = "appeal",
        [CpcdsHeaders.Normalize("Appeal id")] = "appeal",
        [CpcdsHeaders.Normalize("Appeal level")] = "level",
        [CpcdsHeaders.Normalize("Decision")] = "decision",
        [CpcdsHeaders.Normalize("Appeal decision")] = "decision",
        [CpcdsHeaders.Normalize("Outcome")] = "decision",
        [CpcdsHeaders.Normalize("Decision date")] = "date",
        [CpcdsHeaders.Normalize("Decided date")] = "date",
        [CpcdsHeaders.Normalize("Denial code")] = "carc",
        [CpcdsHeaders.Normalize("CARC")] = "carc",
        [CpcdsHeaders.Normalize("Claim payment denial code")] = "carc",
    };

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyyMMdd", "MM/dd/yyyy", "M/d/yyyy"];

    public const int MaxRowErrors = 50;

    public static (List<ShadowAppealOutcome> Outcomes, ShadowAppealOutcomeImportResult Result) Parse(
        string tenantId, string content, string? actor, DateTime nowUtc)
    {
        var result = new ShadowAppealOutcomeImportResult();
        var outcomes = new Dictionary<string, ShadowAppealOutcome>(StringComparer.Ordinal);
        content ??= string.Empty;
        using var records = CpcdsDelimitedReader.Read(content, CpcdsDelimitedReader.DetectDelimiter(content)).GetEnumerator();
        if (!records.MoveNext())
        {
            result.FileErrors.Add("The file is empty.");
            return ([], result);
        }

        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < records.Current.Fields.Count; i++)
        {
            if (Columns.TryGetValue(CpcdsHeaders.Normalize(records.Current.Fields[i]), out var key))
                positions.TryAdd(key, i);
        }
        foreach (var (key, name) in new[] { ("claim", "Claim number"), ("decision", "Decision") })
        {
            if (!positions.ContainsKey(key)) result.FileErrors.Add($"The file has no \"{name}\" column.");
        }
        if (result.FileErrors.Count > 0) return ([], result);

        while (records.MoveNext())
        {
            var record = records.Current;
            string? Get(string key) =>
                positions.TryGetValue(key, out var i) && i < record.Fields.Count && !string.IsNullOrWhiteSpace(record.Fields[i])
                    ? record.Fields[i].Trim()
                    : null;
            if (record.Fields.All(string.IsNullOrWhiteSpace)) continue;
            result.Rows++;

            var claim = Get("claim");
            var decision = ShadowAppealDecision.Parse(Get("decision"));
            DateTime? date = null;
            string? error = null;
            if (claim is null) error = "\"Claim number\" is empty.";
            else if (decision is null)
                error = $"\"Decision\" '{Get("decision")}' is not overturned, partially overturned, upheld or withdrawn.";
            else if (Get("date") is { } d)
            {
                if (DateTime.TryParseExact(d, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    date = parsed;
                else
                    error = $"\"Decision date\" '{d}' is not a date.";
            }

            if (error is not null)
            {
                result.Rejected++;
                if (result.RowErrors.Count < MaxRowErrors) result.RowErrors.Add($"Line {record.LineNumber}: {error}");
                continue;
            }

            var appeal = Get("appeal") ?? "1";
            var id = ShadowAppealOutcome.IdFor(tenantId, claim!, appeal);
            outcomes[id] = new ShadowAppealOutcome
            {
                Id = id,
                TenantId = tenantId,
                ClaimNumber = claim!,
                AppealNumber = appeal,
                AppealLevel = Get("level"),
                Decision = decision!,
                DecidedDate = date,
                DenialCode = Get("carc"),
                ImportedAtUtc = nowUtc,
                ImportedBy = actor,
            };
        }

        result.Loaded = outcomes.Count;
        return (outcomes.Values.ToList(), result);
    }
}

/// <summary>Appeal outcomes against CHO's shadow advice: the evidence for how often CHO's reading of a denial holds up.</summary>
public sealed class ShadowAppealSummary
{
    public DateTime AsOfUtc { get; set; }

    /// <summary>Decided appeals (overturned, partially overturned or upheld) in the file(s).</summary>
    public int Decided { get; set; }
    public int Overturned { get; set; }
    public decimal? OverturnRate { get; set; }

    /// <summary>Decided appeals whose claim has no shadow result yet.</summary>
    public int NotShadowed { get; set; }

    /// <summary>By CHO's advice on the claim: how often each was overturned.</summary>
    public List<ShadowAppealRow> ByAdvice { get; set; } = [];

    /// <summary>By the denial's CARC, most appeals first.</summary>
    public List<ShadowAppealRow> ByDenialCode { get; set; } = [];

    public bool Truncated { get; set; }
}

public sealed class ShadowAppealRow
{
    public string Key { get; set; } = string.Empty;
    public int Decided { get; set; }
    public int Overturned { get; set; }
    public decimal? OverturnRate { get; set; }

    /// <summary>Of these, the appeals CHO would have paid (likely overturns), and how many were.</summary>
    public int ChoWouldPay { get; set; }
    public int ChoWouldPayOverturned { get; set; }

    /// <summary>Of these, the appeals where CHO agreed with the core's denial, and how many were overturned anyway.</summary>
    public int ChoConfirms { get; set; }
    public int ChoConfirmsOverturned { get; set; }
}

public static class ShadowAppealSummaryBuilder
{
    /// <summary>A CARC's overturn rate is shown on an appeal once it has this many decided appeals.</summary>
    public const int MinAppealsForRate = 10;

    public static ShadowAppealSummary Build(
        IEnumerable<ShadowAppealOutcome> outcomes,
        IReadOnlyDictionary<string, ShadowAdjudicationResult> latestByClaimNumber,
        decimal tolerance,
        DateTime asOfUtc)
    {
        var rows = outcomes
            .Where(o => ShadowAppealDecision.IsDecided(o.Decision))
            .Select(o =>
            {
                latestByClaimNumber.TryGetValue(o.ClaimNumber, out var r);
                var advice = r is null ? null : ShadowAppealAdvice.For(r, tolerance);
                var code = o.DenialCode ?? r?.CoreDenialCode ?? r?.Lines.Select(l => l.CoreDenialCode).FirstOrDefault(c => c is not null);
                return (Outcome: o, Advice: advice, Code: code ?? "(none)");
            })
            .ToList();

        var summary = new ShadowAppealSummary
        {
            AsOfUtc = asOfUtc,
            Decided = rows.Count,
            Overturned = rows.Count(x => ShadowAppealDecision.IsOverturned(x.Outcome.Decision)),
            NotShadowed = rows.Count(x => x.Advice is null),
        };
        summary.OverturnRate = Rate(summary.Overturned, summary.Decided);
        summary.ByAdvice = rows.Where(x => x.Advice is not null)
            .GroupBy(x => x.Advice!, StringComparer.Ordinal)
            .Select(g => Row(g.Key, g))
            .OrderBy(r => ShadowAppealAdvice.All.ToList().IndexOf(r.Key))
            .ToList();
        summary.ByDenialCode = rows
            .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => Row(g.Key, g))
            .OrderByDescending(r => r.Decided).ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToList();
        return summary;
    }

    private static ShadowAppealRow Row(string key, IEnumerable<(ShadowAppealOutcome Outcome, string? Advice, string Code)> items)
    {
        var list = items.ToList();
        bool Over((ShadowAppealOutcome Outcome, string? Advice, string Code) x) => ShadowAppealDecision.IsOverturned(x.Outcome.Decision);
        var pay = list.Where(x => x.Advice is not null && ShadowAppealAdvice.LikelyOverturn(x.Advice)).ToList();
        var confirms = list.Where(x => x.Advice == ShadowAppealAdvice.ChoConfirms).ToList();
        var row = new ShadowAppealRow
        {
            Key = key,
            Decided = list.Count,
            Overturned = list.Count(Over),
            ChoWouldPay = pay.Count,
            ChoWouldPayOverturned = pay.Count(Over),
            ChoConfirms = confirms.Count,
            ChoConfirmsOverturned = confirms.Count(Over),
        };
        row.OverturnRate = Rate(row.Overturned, row.Decided);
        return row;
    }

    internal static decimal? Rate(int part, int whole) => whole == 0 ? null : Math.Round((decimal)part / whole, 4);
}

/// <summary>CHO's shadow reading of one appealed claim, for the appeal's reviewer.</summary>
public sealed class ShadowAppealEvidence
{
    public string ClaimNumber { get; set; } = string.Empty;

    /// <summary>One of <see cref="ShadowAppealAdvice"/>.</summary>
    public string Advice { get; set; } = string.Empty;
    public string AdviceText { get; set; } = string.Empty;

    /// <summary>CHO would have paid what was denied. A lead, not a decision.</summary>
    public bool LikelyOverturn { get; set; }

    /// <summary>The latest shadow result for the claim: outcomes, amounts, codes, lines, stage findings, authorization evidence.</summary>
    public ShadowAdjudicationResult Result { get; set; } = new();

    /// <summary>Earlier appeals on the same CARC, once there are enough (see <see cref="ShadowAppealSummaryBuilder.MinAppealsForRate"/>).</summary>
    public ShadowAppealRow? DenialCodeHistory { get; set; }
}

public interface IShadowAppealOutcomeRepository
{
    Task SaveManyAsync(IReadOnlyList<ShadowAppealOutcome> outcomes, CancellationToken ct = default);

    /// <summary>The tenant's outcomes, newest decision first; only one CARC when <paramref name="denialCode"/> is given.</summary>
    Task<IReadOnlyList<ShadowAppealOutcome>> ListAsync(string tenantId, string? denialCode, int limit, CancellationToken ct = default);
}

public sealed class ShadowAppealOutcomeRepositoryMongo : IShadowAppealOutcomeRepository
{
    public const string CollectionName = "ShadowAppealOutcomes";
    private readonly IMongoCollection<ShadowAppealOutcome> _outcomes;

    public ShadowAppealOutcomeRepositoryMongo(IMongoDatabase database) =>
        _outcomes = database.GetCollection<ShadowAppealOutcome>(CollectionName);

    public static void EnsureIndexes(IMongoDatabase database, CancellationToken ct = default) =>
        database.GetCollection<ShadowAppealOutcome>(CollectionName).Indexes.CreateOne(
            new CreateIndexModel<ShadowAppealOutcome>(
                Builders<ShadowAppealOutcome>.IndexKeys.Ascending(o => o.TenantId).Ascending(o => o.DenialCode).Descending(o => o.DecidedDate),
                new CreateIndexOptions { Name = "tenant_carc_decided" }),
            cancellationToken: ct);

    public async Task SaveManyAsync(IReadOnlyList<ShadowAppealOutcome> outcomes, CancellationToken ct = default)
    {
        if (outcomes.Count == 0) return;
        var writes = outcomes.Select(o => new ReplaceOneModel<ShadowAppealOutcome>(
            Builders<ShadowAppealOutcome>.Filter.Eq(x => x.Id, o.Id) & Builders<ShadowAppealOutcome>.Filter.Eq(x => x.TenantId, o.TenantId), o)
        { IsUpsert = true });
        foreach (var chunk in writes.Chunk(500))
            await _outcomes.BulkWriteAsync(chunk, new BulkWriteOptions { IsOrdered = false }, ct);
    }

    public async Task<IReadOnlyList<ShadowAppealOutcome>> ListAsync(
        string tenantId, string? denialCode, int limit, CancellationToken ct = default)
    {
        var b = Builders<ShadowAppealOutcome>.Filter;
        var filter = b.Eq(o => o.TenantId, tenantId);
        if (!string.IsNullOrWhiteSpace(denialCode)) filter &= b.Eq(o => o.DenialCode, denialCode.Trim());
        return await _outcomes.Find(filter).SortByDescending(o => o.DecidedDate).Limit(limit).ToListAsync(ct);
    }
}

/// <summary>For tests and hosts without MongoDB.</summary>
public sealed class InMemoryShadowAppealOutcomeRepository : IShadowAppealOutcomeRepository
{
    private readonly Dictionary<string, ShadowAppealOutcome> _outcomes = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task SaveManyAsync(IReadOnlyList<ShadowAppealOutcome> outcomes, CancellationToken ct = default)
    {
        lock (_gate)
            foreach (var o in outcomes) _outcomes[o.Id] = o;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ShadowAppealOutcome>> ListAsync(
        string tenantId, string? denialCode, int limit, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<ShadowAppealOutcome>>(_outcomes.Values
                .Where(o => o.TenantId == tenantId
                            && (string.IsNullOrWhiteSpace(denialCode) || string.Equals(o.DenialCode, denialCode.Trim(), StringComparison.Ordinal)))
                .OrderByDescending(o => o.DecidedDate).Take(limit).ToList());
    }
}

/// <summary>Appeal evidence for a claim, the outcomes import, and the outcomes summary.</summary>
public sealed class ShadowAppealService
{
    /// <summary>Most outcomes a summary reads.</summary>
    public const int MaxOutcomes = 20_000;

    private readonly IShadowAdjudicationRepository _results;
    private readonly IShadowAppealOutcomeRepository _outcomes;
    private readonly decimal _tolerance;
    private readonly TimeProvider _clock;

    public ShadowAppealService(
        IShadowAdjudicationRepository results, IShadowAppealOutcomeRepository outcomes,
        Microsoft.Extensions.Options.IOptions<ShadowAdjudicationOptions> options, TimeProvider? clock = null)
    {
        _results = results;
        _outcomes = outcomes;
        _tolerance = options.Value.Tolerance;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<ShadowAppealEvidence?> EvidenceAsync(string tenantId, string claimNumber, CancellationToken ct = default)
    {
        var latest = await _results.ListLatestByClaimNumbersAsync(tenantId, [claimNumber.Trim()], ct).ConfigureAwait(false);
        if (!latest.TryGetValue(claimNumber.Trim(), out var result)) return null;

        var advice = ShadowAppealAdvice.For(result, _tolerance);
        var evidence = new ShadowAppealEvidence
        {
            ClaimNumber = result.ClaimNumber,
            Advice = advice,
            AdviceText = ShadowAppealAdvice.Describe(advice),
            LikelyOverturn = ShadowAppealAdvice.LikelyOverturn(advice),
            Result = result,
        };

        var code = result.CoreDenialCode ?? result.Lines.Select(l => l.CoreDenialCode).FirstOrDefault(c => c is not null);
        if (code is not null)
        {
            var history = (await _outcomes.ListAsync(tenantId, code, MaxOutcomes, ct).ConfigureAwait(false))
                .Where(o => ShadowAppealDecision.IsDecided(o.Decision) && o.ClaimNumber != result.ClaimNumber)
                .ToList();
            if (history.Count >= ShadowAppealSummaryBuilder.MinAppealsForRate)
            {
                var overturned = history.Count(o => ShadowAppealDecision.IsOverturned(o.Decision));
                evidence.DenialCodeHistory = new ShadowAppealRow
                {
                    Key = code,
                    Decided = history.Count,
                    Overturned = overturned,
                    OverturnRate = ShadowAppealSummaryBuilder.Rate(overturned, history.Count),
                };
            }
        }
        return evidence;
    }

    public async Task<ShadowAppealOutcomeImportResult> ImportAsync(
        string tenantId, string content, bool dryRun, string? actor, CancellationToken ct = default)
    {
        var (outcomes, result) = ShadowAppealOutcomeFile.Parse(tenantId, content, actor, _clock.GetUtcNow().UtcDateTime);
        result.DryRun = dryRun;
        if (!dryRun && result.FileErrors.Count == 0)
            await _outcomes.SaveManyAsync(outcomes, ct).ConfigureAwait(false);
        return result;
    }

    public async Task<ShadowAppealSummary> SummaryAsync(string tenantId, CancellationToken ct = default)
    {
        var outcomes = await _outcomes.ListAsync(tenantId, null, MaxOutcomes + 1, ct).ConfigureAwait(false);
        var truncated = outcomes.Count > MaxOutcomes;
        var kept = outcomes.Take(MaxOutcomes).ToList();
        var latest = new Dictionary<string, ShadowAdjudicationResult>(StringComparer.Ordinal);
        foreach (var chunk in kept.Select(o => o.ClaimNumber).Distinct(StringComparer.Ordinal).Chunk(1000))
        {
            foreach (var (number, r) in await _results.ListLatestByClaimNumbersAsync(tenantId, chunk, ct).ConfigureAwait(false))
                latest[number] = r;
        }
        var summary = ShadowAppealSummaryBuilder.Build(kept, latest, _tolerance, _clock.GetUtcNow().UtcDateTime);
        summary.Truncated = truncated;
        return summary;
    }
}
