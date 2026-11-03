using System.Globalization;
using ClaimsService.Import.Cpcds;
using CloudHealthOffice.Infrastructure.Import.Cpcds;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ClaimsService.Services.Adjudication.Shadow;

/// <summary>
/// What a member had met toward the deductible and out-of-pocket maximum as of
/// a date, by the core's own accumulators (a starting balance). Shadow
/// adjudication only: these never reach CHO's accumulator store. See
/// <see cref="ShadowAccumulatorHistory"/>.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class ShadowAccumulatorBalance
{
    /// <summary><c>{tenant}|{member}|{asOf:yyyy-MM-dd}|{tier}</c>: reloading the same balance replaces it.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    /// <summary>The CHO member id (after the member crosswalk).</summary>
    public string MemberId { get; set; } = string.Empty;

    /// <summary>The member id as the core sent it.</summary>
    public string SourceMemberId { get; set; } = string.Empty;

    public string? SourceSystem { get; set; }

    /// <summary>The balance includes everything the core adjudicated through the end of this day.</summary>
    public DateTime AsOfDate { get; set; }

    /// <summary><c>InNetwork</c> or <c>OutOfNetwork</c>.</summary>
    public string NetworkTier { get; set; } = "InNetwork";

    public decimal IndividualDeductibleMet { get; set; }
    public decimal IndividualOutOfPocketMet { get; set; }

    /// <summary>The whole family's, including this member. Null when the core sent none.</summary>
    public decimal? FamilyDeductibleMet { get; set; }

    public decimal? FamilyOutOfPocketMet { get; set; }

    public DateTime LoadedAt { get; set; }
    public string LoadedBy { get; set; } = string.Empty;

    public static string KeyOf(string tenantId, string memberId, DateTime asOf, string tier)
        => $"{tenantId}|{memberId}|{asOf:yyyy-MM-dd}|{tier}";
}

public interface IShadowAccumulatorBalanceRepository
{
    Task UpsertAsync(IReadOnlyList<ShadowAccumulatorBalance> balances, CancellationToken ct = default);

    /// <summary>The member's balances with an as-of date in <paramref name="year"/>.</summary>
    Task<IReadOnlyList<ShadowAccumulatorBalance>> ListForMemberAsync(
        string tenantId, string memberId, int year, CancellationToken ct = default);
}

public sealed class ShadowAccumulatorBalanceRepositoryMongo : IShadowAccumulatorBalanceRepository
{
    public const string CollectionName = "ShadowAccumulatorBalances";
    private static int _indexed;
    private readonly IMongoCollection<ShadowAccumulatorBalance> _balances;

    public ShadowAccumulatorBalanceRepositoryMongo(IMongoDatabase database)
    {
        _balances = database.GetCollection<ShadowAccumulatorBalance>(CollectionName);
        if (Interlocked.Exchange(ref _indexed, 1) == 1) return;
        _balances.Indexes.CreateOne(new CreateIndexModel<ShadowAccumulatorBalance>(
            Builders<ShadowAccumulatorBalance>.IndexKeys
                .Ascending(b => b.TenantId).Ascending(b => b.MemberId).Ascending(b => b.AsOfDate),
            new CreateIndexOptions { Name = "tenant_member_asof" }));
    }

    public async Task UpsertAsync(IReadOnlyList<ShadowAccumulatorBalance> balances, CancellationToken ct = default)
    {
        if (balances.Count == 0) return;
        var f = Builders<ShadowAccumulatorBalance>.Filter;
        var writes = balances.Select(b => new ReplaceOneModel<ShadowAccumulatorBalance>(
            f.Eq(x => x.Id, b.Id) & f.Eq(x => x.TenantId, b.TenantId), b) { IsUpsert = true });
        await _balances.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, ct);
    }

    public async Task<IReadOnlyList<ShadowAccumulatorBalance>> ListForMemberAsync(
        string tenantId, string memberId, int year, CancellationToken ct = default)
    {
        var from = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddYears(1);
        return await _balances
            .Find(b => b.TenantId == tenantId && b.MemberId == memberId && b.AsOfDate >= from && b.AsOfDate < to)
            .ToListAsync(ct);
    }
}

public sealed class InMemoryShadowAccumulatorBalanceRepository : IShadowAccumulatorBalanceRepository
{
    private readonly Dictionary<string, ShadowAccumulatorBalance> _balances = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public Task UpsertAsync(IReadOnlyList<ShadowAccumulatorBalance> balances, CancellationToken ct = default)
    {
        lock (_sync)
            foreach (var b in balances) _balances[b.Id] = b;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ShadowAccumulatorBalance>> ListForMemberAsync(
        string tenantId, string memberId, int year, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult<IReadOnlyList<ShadowAccumulatorBalance>>(_balances.Values
                .Where(b => b.TenantId == tenantId && b.MemberId == memberId && b.AsOfDate.Year == year)
                .ToList());
    }
}

/// <summary>The report of one balance file.</summary>
public sealed class ShadowAccumulatorBalanceImportResult
{
    public bool DryRun { get; set; }
    public int RowsInFile { get; set; }
    public int Loaded { get; set; }
    public int Rejected { get; set; }

    /// <summary>File-level problems (no header, a required column missing).</summary>
    public List<string> FileErrors { get; set; } = [];

    /// <summary>The first row errors, as "line N: message".</summary>
    public List<string> Errors { get; set; } = [];
}

/// <summary>
/// Loads a core's accumulator balances from a CSV file (header row first; comma,
/// pipe or tab). Columns, in any order (case, spaces and underscores ignored):
/// <c>member_id</c> and <c>as_of_date</c> (required), <c>network</c>
/// (in or out of network; default in network), <c>individual_deductible_met</c>,
/// <c>individual_oop_met</c>, <c>family_deductible_met</c>, <c>family_oop_met</c>.
/// </summary>
public sealed class ShadowAccumulatorBalanceImporter(
    IShadowAccumulatorBalanceRepository store, ICoreMemberCrosswalk crosswalk, TimeProvider? clock = null)
{
    public const int MaxErrors = 50;

    private static readonly Dictionary<string, string> Columns = new(StringComparer.Ordinal)
    {
        ["memberid"] = "member", ["member"] = "member",
        ["asofdate"] = "asof", ["asof"] = "asof", ["balancedate"] = "asof",
        ["network"] = "network", ["networktier"] = "network", ["networkstatus"] = "network",
        ["individualdeductiblemet"] = "inddeductible", ["deductiblemet"] = "inddeductible",
        ["individualoopmet"] = "indoop", ["individualoutofpocketmet"] = "indoop", ["oopmet"] = "indoop", ["outofpocketmet"] = "indoop",
        ["familydeductiblemet"] = "famdeductible",
        ["familyoopmet"] = "famoop", ["familyoutofpocketmet"] = "famoop",
    };

    public async Task<ShadowAccumulatorBalanceImportResult> ImportAsync(
        string tenantId, string content, string? sourceSystem, bool dryRun, string actorId, CancellationToken ct)
    {
        var result = new ShadowAccumulatorBalanceImportResult { DryRun = dryRun };
        if (string.IsNullOrWhiteSpace(content))
        {
            result.FileErrors.Add("The file is empty.");
            return result;
        }

        using var records = CpcdsDelimitedReader.Read(content, CpcdsDelimitedReader.DetectDelimiter(content)).GetEnumerator();
        if (!records.MoveNext())
        {
            result.FileErrors.Add("The file is empty.");
            return result;
        }
        var header = records.Current.Fields
            .Select(h => Columns.GetValueOrDefault(CpcdsHeaders.Normalize(h)))
            .ToList();
        foreach (var required in new[] { "member", "asof" })
        {
            if (!header.Contains(required))
                result.FileErrors.Add($"The header has no {(required == "member" ? "member_id" : "as_of_date")} column.");
        }
        if (!header.Any(h => h is "inddeductible" or "indoop" or "famdeductible" or "famoop"))
            result.FileErrors.Add("The header names no amount: individual_deductible_met, individual_oop_met, family_deductible_met or family_oop_met.");
        if (result.FileErrors.Count > 0) return result;

        var balances = new Dictionary<string, ShadowAccumulatorBalance>(StringComparer.Ordinal);
        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        while (records.MoveNext())
        {
            var cells = records.Current.Fields;
            if (cells.All(c => c.Trim().Length == 0)) continue;
            ct.ThrowIfCancellationRequested();
            result.RowsInFile++;
            var lineNumber = records.Current.LineNumber;
            string? Cell(string column)
            {
                var at = header.IndexOf(column);
                return at >= 0 && at < cells.Count && cells[at].Trim().Length > 0 ? cells[at].Trim() : null;
            }

            var errors = new List<string>();
            var sourceMember = Cell("member");
            if (sourceMember is null) errors.Add("member_id is empty");

            DateTime asOf = default;
            if (Cell("asof") is not { } asOfText)
                errors.Add("as_of_date is empty");
            else if (!DateTime.TryParseExact(asOfText, ["yyyy-MM-dd", "yyyyMMdd", "M/d/yyyy"], CultureInfo.InvariantCulture,
                         DateTimeStyles.None, out var parsed))
                errors.Add($"as_of_date '{asOfText}' is not a date (yyyy-MM-dd)");
            else
                asOf = DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);

            var tier = "InNetwork";
            if (Cell("network") is { } network)
            {
                if (CpcdsClaimMapper.NetworkTier(network) is { } mapped) tier = mapped;
                else errors.Add($"network '{network}' is neither in nor out of network");
            }

            decimal? Amount(string column, string name)
            {
                if (Cell(column) is not { } text) return null;
                if (decimal.TryParse(text.TrimStart('$'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value >= 0)
                    return value;
                errors.Add($"{name} '{text}' is not an amount of zero or more");
                return null;
            }
            var indDeductible = Amount("inddeductible", "individual_deductible_met");
            var indOop = Amount("indoop", "individual_oop_met");
            var famDeductible = Amount("famdeductible", "family_deductible_met");
            var famOop = Amount("famoop", "family_oop_met");
            if (famDeductible < indDeductible) errors.Add("family_deductible_met is less than individual_deductible_met");
            if (famOop < indOop) errors.Add("family_oop_met is less than individual_oop_met");

            string? memberId = null;
            if (errors.Count == 0)
            {
                memberId = await crosswalk.ResolveAsync(tenantId, sourceSystem, sourceMember!, ct);
                if (memberId is null)
                    errors.Add($"member '{sourceMember}' is not in the member crosswalk for source system '{sourceSystem ?? "(none)"}'");
            }

            if (errors.Count > 0)
            {
                result.Rejected++;
                if (result.Errors.Count < MaxErrors) result.Errors.Add($"line {lineNumber}: {string.Join("; ", errors)}");
                continue;
            }

            var balance = new ShadowAccumulatorBalance
            {
                Id = ShadowAccumulatorBalance.KeyOf(tenantId, memberId!, asOf, tier),
                TenantId = tenantId,
                MemberId = memberId!,
                SourceMemberId = sourceMember!,
                SourceSystem = sourceSystem,
                AsOfDate = asOf,
                NetworkTier = tier,
                IndividualDeductibleMet = indDeductible ?? 0m,
                IndividualOutOfPocketMet = indOop ?? 0m,
                FamilyDeductibleMet = famDeductible,
                FamilyOutOfPocketMet = famOop,
                LoadedAt = now,
                LoadedBy = actorId,
            };
            balances[balance.Id] = balance; // a later row for the same member, date and network wins
        }

        result.Loaded = balances.Count;
        if (!dryRun) await store.UpsertAsync(balances.Values.ToList(), ct);
        return result;
    }
}
