using System.Text.RegularExpressions;
using PremiumBillingService.Models;
using PremiumBillingService.Rating;
using PremiumBillingService.Repositories;

namespace PremiumBillingService.Services;

public interface IRateTableService
{
    Task<IReadOnlyList<RateTableRecord>> ListCurrentAsync();
    Task<IReadOnlyList<RateTableRecord>> ListVersionsAsync(string rateTableId);
    Task<RateTableRecord?> GetVersionAsync(string rateTableId, int version);
    Task<RateTableRecord> SaveAsync(SaveRateTableRequest request, string? actor);
    Task<RateTableRecord> WithdrawAsync(string rateTableId, WithdrawRateTableRequest request, string? actor);
}

/// <summary>A rate table change that cannot be saved (400).</summary>
public sealed class RateTableRejectedException : Exception
{
    public RateTableRejectedException(string message, IReadOnlyList<string>? errors = null) : base(message)
    {
        Errors = errors ?? new[] { message };
    }

    public IReadOnlyList<string> Errors { get; }
}

/// <summary>
/// Saves rate tables as immutable versions. Each save is validated on its own
/// and together with the tenant's other current tables (no two tables of a
/// plan may cover the same day; proration may change only on a 1st).
/// </summary>
public sealed partial class RateTableService : IRateTableService
{
    private readonly IRateTableRepository _repository;
    private readonly TimeProvider _clock;

    public RateTableService(IRateTableRepository repository, TimeProvider clock)
    {
        _repository = repository;
        _clock = clock;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex IdPattern();

    public async Task<IReadOnlyList<RateTableRecord>> ListCurrentAsync() =>
        RateTableRecord.Current(await _repository.ListVersionsAsync())
            .OrderBy(r => r.PlanId, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Table.EffectiveFrom).ToList();

    public Task<IReadOnlyList<RateTableRecord>> ListVersionsAsync(string rateTableId) => _repository.ListVersionsAsync(rateTableId);

    public Task<RateTableRecord?> GetVersionAsync(string rateTableId, int version) => _repository.GetVersionAsync(rateTableId, version);

    public async Task<RateTableRecord> SaveAsync(SaveRateTableRequest request, string? actor)
    {
        var table = request.Table ?? throw new RateTableRejectedException("A rate table is required");
        if (string.IsNullOrWhiteSpace(table.Id) || !IdPattern().IsMatch(table.Id))
            throw new RateTableRejectedException("Rate table id must be 1–100 letters, digits, '.', '_' or '-'");

        table.EffectiveFrom = DateOnlyUtc(table.EffectiveFrom);
        table.EffectiveTo = table.EffectiveTo.HasValue ? DateOnlyUtc(table.EffectiveTo.Value) : null;
        if (table.AgeDeterminationDate.HasValue)
            table.AgeDeterminationDate = DateOnlyUtc(table.AgeDeterminationDate.Value);
        try
        {
            table.Validate();
        }
        catch (RateTableValidationException ex)
        {
            throw new RateTableRejectedException(ex.Message, ex.Errors);
        }

        var versions = await _repository.ListVersionsAsync(table.Id);
        var latest = versions.OrderByDescending(v => v.Version).FirstOrDefault();
        if (request.ExpectedCurrentVersion != latest?.Version)
            throw new RateTableVersionConflictException(table.Id, (latest?.Version ?? 0) + 1);
        if (latest != null && !string.Equals(latest.PlanId, table.PlanId, StringComparison.OrdinalIgnoreCase))
            throw new RateTableRejectedException($"Rate table {table.Id} is for plan {latest.PlanId}; a table's plan cannot change");

        await CheckAgainstOthersAsync(table);
        return await CreateAsync(table, latest, withdrawn: false, request.ChangeReason, actor);
    }

    public async Task<RateTableRecord> WithdrawAsync(string rateTableId, WithdrawRateTableRequest request, string? actor)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new RateTableRejectedException("A reason is required to withdraw a rate table");
        var versions = await _repository.ListVersionsAsync(rateTableId);
        var latest = versions.OrderByDescending(v => v.Version).FirstOrDefault()
                     ?? throw new KeyNotFoundException($"Rate table {rateTableId} not found");
        if (request.ExpectedCurrentVersion.HasValue && request.ExpectedCurrentVersion != latest.Version)
            throw new RateTableVersionConflictException(rateTableId, latest.Version + 1);
        if (latest.Withdrawn)
            throw new RateTableRejectedException($"Rate table {rateTableId} is already withdrawn");
        return await CreateAsync(latest.Table, latest, withdrawn: true, request.Reason, actor);
    }

    private async Task CheckAgainstOthersAsync(RateTable table)
    {
        var others = RateTableRecord.Current(await _repository.ListVersionsAsync())
            .Where(r => r.RateTableId != table.Id && string.Equals(r.PlanId, table.PlanId, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.ToRatingTable())
            .Append(table);
        try
        {
            _ = new RateTableCatalog(others);
        }
        catch (RateTableValidationException ex)
        {
            throw new RateTableRejectedException(ex.Message, ex.Errors);
        }
    }

    private async Task<RateTableRecord> CreateAsync(RateTable table, RateTableRecord? latest, bool withdrawn, string? reason, string? actor)
    {
        var version = (latest?.Version ?? 0) + 1;
        var stored = Copy(table);
        stored.Version = version;
        stored.ContentHash = null;
        var record = new RateTableRecord
        {
            RateTableId = table.Id,
            Version = version,
            PlanId = table.PlanId,
            Withdrawn = withdrawn,
            ChangeReason = reason is { Length: > 500 } r ? r[..500] : reason,
            ContentHash = RateTableRecord.HashOf(stored),
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            CreatedBy = actor,
            Table = stored
        };
        record.Table.ContentHash = record.ContentHash;
        return await _repository.CreateVersionAsync(record);
    }

    private static RateTable Copy(RateTable table) =>
        System.Text.Json.JsonSerializer.Deserialize<RateTable>(System.Text.Json.JsonSerializer.Serialize(table))!;

    private static DateTime DateOnlyUtc(DateTime value) => DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);
}
