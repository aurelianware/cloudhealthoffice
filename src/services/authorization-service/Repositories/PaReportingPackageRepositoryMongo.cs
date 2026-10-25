using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AuthorizationService.Models;
using AuthorizationService.Services.Cms0057;
using CloudHealthOffice.Infrastructure.Data;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace AuthorizationService.Repositories;

public sealed class PaReportingPackageOptions
{
    public int MaxRecords { get; set; } = 100_000;
    public long MaxInputBytes { get; set; } = 128L * 1024 * 1024;
}
public sealed class PaReportingPackageUnavailableException : Exception { }
public sealed class PaReportingPackageLimitException : Exception { }
public sealed class PaReportingPackageIntegrityException : Exception { }

public interface IPaReportingPackageRepository
{
    Task<PaReportingPackage> FreezeAsync(FreezePaReportingPackage command, string actor, CancellationToken ct = default);
    Task<PaReportingPackage?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<PaReportingPackageInput>?> ReadInputsAsync(string id, int afterOrdinal, int pageSize, CancellationToken ct = default);
}

/// <summary>Insert-only private evidence packages, atomically frozen with their exact retained inputs.</summary>
public sealed class PaReportingPackageRepositoryMongo : IPaReportingPackageRepository
{
    private readonly IMongoDatabase _db;
    private readonly IHttpContextAccessor _http;
    private readonly PaReportingPackageOptions _limits;
    private readonly IMongoCollection<Authorization> _authorizations;
    private readonly IMongoCollection<PaReportingPackage> _packages;
    private readonly IMongoCollection<PaReportingPackageInput> _inputs;
    public PaReportingPackageRepositoryMongo(IMongoDatabase db, IHttpContextAccessor http, IOptions<PaReportingPackageOptions> options)
    {
        _db = db; _http = http; _limits = options.Value;
        if (_limits.MaxRecords <= 0 || _limits.MaxInputBytes <= 0) throw new ArgumentException("Positive reporting package limits are required.");
        _authorizations = db.GetCollection<Authorization>("Authorizations");
        _packages = db.GetCollection<PaReportingPackage>("PaReportingPackages");
        _inputs = db.GetCollection<PaReportingPackageInput>("PaReportingPackageInputs");
    }
    private async Task EnsureStoresAsync(CancellationToken ct)
    {
        await _packages.Indexes.CreateOneAsync(new CreateIndexModel<PaReportingPackage>(Builders<PaReportingPackage>.IndexKeys
            .Ascending(p => p.TenantId).Ascending(p => p.Id)), cancellationToken: ct);
        await _inputs.Indexes.CreateOneAsync(new CreateIndexModel<PaReportingPackageInput>(Builders<PaReportingPackageInput>.IndexKeys
            .Ascending(p => p.TenantId).Ascending(p => p.PackageId).Ascending(p => p.Ordinal), new CreateIndexOptions { Unique = true }), cancellationToken: ct);
    }
    private string Tenant => _http.HttpContext?.Items["TenantId"] is string tenant && !string.IsNullOrWhiteSpace(tenant)
        ? tenant : throw new InvalidOperationException("A token-scoped tenant is required.");

    public static PaReportingPackageScope Validate(FreezePaReportingPackage command, DateTime now)
    {
        Validator.ValidateObject(command, new ValidationContext(command), true);
        var scope = new PaReportingPackageScope
        {
            PlanId = command.PlanId, PlanScopeReference = command.PlanScopeReference, Year = command.Year,
            TimeZone = TimeZoneInfo.FindSystemTimeZoneById(command.TimeZone).Id, LineOfBusiness = command.LineOfBusiness,
            OutcomeCutoffUtc = Milliseconds(command.OutcomeCutoff.UtcDateTime),
            KnowledgeCutoffUtc = Milliseconds(command.KnowledgeCutoff.UtcDateTime),
        };
        var report = Calculator(scope);
        if (scope.OutcomeCutoffUtc > now || scope.KnowledgeCutoffUtc > now
            || scope.OutcomeCutoffUtc < report.FromUtc || scope.KnowledgeCutoffUtc < report.FromUtc)
            throw new ArgumentException("Explicit cutoffs must lie between cohort start and server time.");
        return scope;
    }
    private static DateTime Milliseconds(DateTime value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    private static AnnualPaReporting Calculator(PaReportingPackageScope scope) => new(scope.Year, scope.TimeZone,
        scope.LineOfBusiness, scope.OutcomeCutoffUtc, scope.KnowledgeCutoffUtc);

    public async Task<PaReportingPackage> FreezeAsync(FreezePaReportingPackage command, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("An authenticated recorder is required.");
        var tenant = Tenant;
        var scope = Validate(command, DateTime.UtcNow);
        var id = PaReportingPackageHash.Identity(tenant, command.RequestKey);
        var requestHash = PaReportingPackageHash.Of(scope);
        var existing = await GetForTenantAsync(tenant, id, ct);
        if (existing is not null) return Replay(existing, requestHash);
        try
        {
            await EnsureStoresAsync(ct);
            using var session = await _db.Client.StartSessionAsync(cancellationToken: ct);
            return await session.WithTransactionAsync(async (active, token) =>
            {
                var report = Calculator(scope);
                var package = new PaReportingPackage
                {
                    Id = id, TenantId = tenant, RequestHash = requestHash, Scope = scope,
                    CreatedBy = actor, CreatedAt = Milliseconds(DateTime.UtcNow),
                    BuildVersion = typeof(PaReportingPackageRepositoryMongo).Assembly
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
                };
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var filter = AuthorizationRepositoryMongo.ReportingCohortFilter(tenant, report.FromUtc, report.ToExclusiveUtc, scope.LineOfBusiness);
                var projection = Builders<Authorization>.Projection.Expression(a => new PaReportingFrozenInput
                {
                    AuthorizationId = a.Id, Revision = a.Revision, Ledger = a.ReportingLedger,
                    LegacyLineOfBusiness = a.LineOfBusiness, LegacySubmittedAt = a.SubmittedDate,
                });
                using var cursor = await _authorizations.FindAsync(active, filter, new FindOptions<Authorization, PaReportingFrozenInput>
                { BatchSize = 250, Sort = Builders<Authorization>.Sort.Ascending(a => a.Id), Projection = projection }, token);
                while (await cursor.MoveNextAsync(token))
                {
                    var batch = new List<PaReportingPackageInput>();
                    foreach (var input in cursor.Current)
                    {
                        var bytes = PaReportingPackageHash.Bytes(input);
                        package.InputBytes += bytes.Length;
                        if (package.InputCount >= _limits.MaxRecords || package.InputBytes > _limits.MaxInputBytes || bytes.Length > 8 * 1024 * 1024)
                            throw new PaReportingPackageLimitException(); // abort all writes; never silently truncate
                        var entry = new PaReportingPackageInput
                        {
                            Id = id + ":" + package.InputCount, TenantId = tenant, PackageId = id,
                            Ordinal = package.InputCount++, Input = input,
                            InputHash = Convert.ToHexString(SHA256.HashData(bytes)),
                        };
                        digest.AppendData(Encoding.ASCII.GetBytes(entry.InputHash));
                        batch.Add(entry);
                        report.Add(new Authorization { ReportingLedger = input.Ledger,
                            LineOfBusiness = input.LegacyLineOfBusiness, SubmittedDate = input.LegacySubmittedAt });
                    }
                    if (batch.Count > 0) await _inputs.InsertManyAsync(active, batch, cancellationToken: token);
                }
                package.InputsHash = Convert.ToHexString(digest.GetHashAndReset());
                package.Report = report.Finish();
                package.PackageHash = PaReportingPackageHash.Header(package);
                await _packages.InsertOneAsync(active, package, cancellationToken: token);
                return package;
            }, new TransactionOptions(readConcern: ReadConcern.Snapshot, readPreference: ReadPreference.Primary,
                writeConcern: WriteConcern.WMajority), ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another same-key freeze may have won; only its committed package can be returned.
            var winner = await GetForTenantAsync(tenant, id, ct) ?? throw new PaReportingPackageUnavailableException();
            return Replay(winner, requestHash);
        }
        catch (MongoBulkWriteException<PaReportingPackageInput> ex) when (ex.WriteErrors.Count > 0 && ex.WriteErrors.All(e => e.Category == ServerErrorCategory.DuplicateKey))
        {
            var winner = await GetForTenantAsync(tenant, id, ct) ?? throw new PaReportingPackageUnavailableException();
            return Replay(winner, requestHash);
        }
        catch (MongoException) { throw new PaReportingPackageUnavailableException(); }
        catch (NotSupportedException) { throw new PaReportingPackageUnavailableException(); }
    }
    private static PaReportingPackage Replay(PaReportingPackage package, string hash) => package.RequestHash == hash
        ? package : throw new ConcurrencyConflictException("Reporting request key already identifies different scope/cutoffs.");

    public Task<PaReportingPackage?> GetAsync(string id, CancellationToken ct = default)
        => GetForTenantAsync(Tenant, id, ct);

    private async Task<PaReportingPackage?> GetForTenantAsync(string tenant, string id, CancellationToken ct)
    {
        var package = await _packages.Find(p => p.TenantId == tenant && p.Id == id).FirstOrDefaultAsync(ct);
        if (package is not null && (package.Version != PaReportingPackage.FormatVersion || package.PackageHash != PaReportingPackageHash.Header(package)))
            throw new PaReportingPackageIntegrityException();
        return package;
    }
    public async Task<IReadOnlyList<PaReportingPackageInput>?> ReadInputsAsync(string id, int afterOrdinal, int pageSize, CancellationToken ct = default)
    {
        if (afterOrdinal < -1 || pageSize is < 1 or > 250) throw new ArgumentException("Use an ordinal >= -1 and page size 1–250.");
        var tenant = Tenant;
        var package = await GetForTenantAsync(tenant, id, ct);
        if (package is null) return null;
        if (afterOrdinal >= package.InputCount && !(afterOrdinal == -1 && package.InputCount == 0))
            throw new ArgumentException("Ordinal exceeds the frozen input count.");
        var rows = await _inputs.Find(i => i.TenantId == tenant && i.PackageId == id && i.Ordinal > afterOrdinal)
            .SortBy(i => i.Ordinal).Limit(pageSize).ToListAsync(ct);
        if (rows.Count != Math.Min(pageSize, package.InputCount - afterOrdinal - 1)) throw new PaReportingPackageIntegrityException();
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Ordinal != afterOrdinal + i + 1 || rows[i].InputHash != PaReportingPackageHash.Of(rows[i].Input))
                throw new PaReportingPackageIntegrityException();
        return rows;
    }
}
