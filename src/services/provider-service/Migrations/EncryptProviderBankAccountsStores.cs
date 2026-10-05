using System.Runtime.CompilerServices;
using CloudHealthOffice.FieldProtection;
using Microsoft.Azure.Cosmos;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Security;

namespace ProviderService.Migrations;

/// <summary>A provider row as stored (ciphertext untouched) with the token a conditional replace needs.</summary>
public sealed record StoredProviderRow(Provider Row, string ETag);

/// <summary>Provider rows that carry a bank-account copy, read and replaced as stored.</summary>
public interface IProviderRowStore
{
    /// <summary>Rows of <paramref name="tenant"/> (every tenant when null) whose <see cref="Provider.BankAccount"/> is set.</summary>
    IAsyncEnumerable<StoredProviderRow> ListRowsWithBankAccountAsync(string? tenant, CancellationToken ct);

    /// <summary>Replaces the row only if it is unchanged since it was listed. False when it changed (a conflict).</summary>
    Task<bool> ReplaceIfUnchangedAsync(StoredProviderRow row, CancellationToken ct);
}

/// <summary>ProviderBankAccounts records, read and saved as stored (no decryption).</summary>
public interface IBankAccountRecordStore
{
    IAsyncEnumerable<ProviderBankAccountRecord> ListAsync(string? tenant, CancellationToken ct);

    /// <summary>Saves with the same revision check the service uses. False on a conflict.</summary>
    Task<bool> SaveAsync(ProviderBankAccountRecord record, long expectedRevision, CancellationToken ct);
}

public static partial class EncryptProviderBankAccounts
{
    /// <summary>
    /// The store-neutral migration (used for Cosmos): every bank number of the
    /// listed provider rows and bank-account records not yet bound to its
    /// record (plaintext or <c>enc:v1:</c>) is stored as <c>enc:v2:</c>, each
    /// document replaced only if unchanged since read.
    /// </summary>
    public static async Task<Counts> MigrateStoresAsync(
        IProviderRowStore rows,
        IBankAccountRecordStore records,
        IFieldProtector protector,
        IReadOnlyCollection<string>? tenants,
        bool dryRun,
        TextWriter output,
        CancellationToken ct = default)
    {
        var byTenant = new SortedDictionary<string, Counts>(StringComparer.Ordinal);
        Counts For(string tenant) => byTenant.TryGetValue(tenant, out var c) ? c : byTenant[tenant] = new Counts();

        foreach (var tenant in tenants?.Cast<string?>() ?? new string?[] { null })
        {
            await foreach (var stored in rows.ListRowsWithBankAccountAsync(tenant, ct))
            {
                var counts = For(stored.Row.TenantId ?? string.Empty);
                counts.ProvidersScanned++;
                try
                {
                    var rebound = RebindAccount(protector, stored.Row.BankAccount, stored.Row.TenantId,
                        ProviderBankAccountProtection.RecordIdOf(stored.Row));
                    if (rebound == null) { counts.ProvidersAlreadyEncrypted++; continue; }
                    if (dryRun) { counts.ProvidersEncrypted++; continue; }
                    stored.Row.BankAccount = rebound;
                    if (await rows.ReplaceIfUnchangedAsync(stored, ct)) counts.ProvidersEncrypted++;
                    else counts.ProviderConflicts++;
                }
                catch (Exception ex) when (ex is FieldProtectionException or CosmosException or ArgumentException)
                {
                    counts.Failures++;
                    await Console.Error.WriteLineAsync(
                        $"provider row {stored.Row.Id} in tenant {Display(stored.Row.TenantId ?? string.Empty)} failed: {ex.GetType().Name}");
                }
            }

            await foreach (var record in records.ListAsync(tenant, ct))
            {
                var counts = For(record.TenantId ?? string.Empty);
                counts.RecordsScanned++;
                try
                {
                    var changed = false;
                    record.Active = Rebound(record.Active, ref changed);
                    foreach (var change in record.Changes)
                    {
                        change.Proposed = Rebound(change.Proposed, ref changed);
                        change.PreviousAccount = Rebound(change.PreviousAccount, ref changed);
                    }
                    if (!changed) { counts.RecordsAlreadyEncrypted++; continue; }
                    if (dryRun) { counts.RecordsEncrypted++; continue; }
                    if (await records.SaveAsync(record, record.Revision, ct)) counts.RecordsEncrypted++;
                    else counts.RecordConflicts++;
                }
                catch (Exception ex) when (ex is FieldProtectionException or CosmosException or ArgumentException)
                {
                    counts.Failures++;
                    await Console.Error.WriteLineAsync(
                        $"bank-account record {record.Id} in tenant {Display(record.TenantId ?? string.Empty)} failed: {ex.GetType().Name}");
                }

                ProviderBankAccount? Rebound(ProviderBankAccount? account, ref bool changed)
                {
                    var rebound = RebindAccount(protector, account, record.TenantId, record.ProviderId);
                    if (rebound == null) return account;
                    changed = true;
                    return rebound;
                }
            }
        }

        var total = new Counts();
        foreach (var (tenant, counts) in byTenant)
        {
            await output.WriteLineAsync($"tenant {Display(tenant)}: {counts}");
            total.Add(counts);
        }
        return total;
    }

    /// <summary>
    /// A copy of a stored account with every secret bound to
    /// (<paramref name="tenant"/>, <paramref name="recordId"/>), last 4 filled
    /// in when missing; null when nothing needs to change.
    /// </summary>
    internal static ProviderBankAccount? RebindAccount(IFieldProtector protector, ProviderBankAccount? account, string? tenant, string recordId)
    {
        if (account == null || !ProviderBankAccountProtection.NeedsRebinding(account)) return null;
        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var copy = System.Text.Json.JsonSerializer.Deserialize<ProviderBankAccount>(System.Text.Json.JsonSerializer.Serialize(account, web), web)!;
        tenant ??= string.Empty;
        if (Rebind(protector, copy.RoutingNumber, tenant, recordId, "RoutingNumber") is { } routing)
        {
            copy.RoutingNumber = routing.Stored;
            copy.RoutingNumberLast4 ??= Last4(routing.Plaintext);
        }
        if (Rebind(protector, copy.AccountNumber, tenant, recordId, "AccountNumber") is { } number)
        {
            copy.AccountNumber = number.Stored;
            copy.AccountNumberLast4 ??= Last4(number.Plaintext);
        }
        if (Rebind(protector, copy.TaxId, tenant, recordId, "TaxId") is { } taxId)
            copy.TaxId = taxId.Stored;
        return copy;
    }
}

/// <summary>Provider rows in the Cosmos <c>Providers</c> container (partition key: the tenant id).</summary>
public sealed class CosmosProviderRowStore : IProviderRowStore
{
    private readonly Container _container;

    public CosmosProviderRowStore(CosmosClient client, IConfiguration configuration)
        => _container = client.GetContainer(
            configuration["CosmosDb:DatabaseName"] ?? "ProviderDB", configuration["CosmosDb:ContainerName"] ?? "Providers");

    public async IAsyncEnumerable<StoredProviderRow> ListRowsWithBankAccountAsync(
        string? tenant, [EnumeratorCancellation] CancellationToken ct)
    {
        // Typed reads and writes, as the service's own repository does, so the
        // document shape is whatever the service stores.
        var options = tenant == null ? new QueryRequestOptions() : new QueryRequestOptions { PartitionKey = new PartitionKey(tenant) };
        using var iterator = _container.GetItemQueryIterator<Provider>(new QueryDefinition("SELECT * FROM c"), requestOptions: options);
        while (iterator.HasMoreResults)
        {
            foreach (var listed in await iterator.ReadNextAsync(ct))
            {
                if (listed.BankAccount == null || !ProviderBankAccountProtection.NeedsRebinding(listed.BankAccount)) continue;
                if (tenant != null && !string.Equals(listed.TenantId, tenant, StringComparison.Ordinal)) continue;
                // Re-read for the ETag the conditional replace needs.
                ItemResponse<Provider> current;
                try
                {
                    current = await _container.ReadItemAsync<Provider>(listed.Id, new PartitionKey(listed.TenantId), cancellationToken: ct);
                }
                catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    continue;
                }
                yield return new StoredProviderRow(current.Resource, current.ETag);
            }
        }
    }

    public async Task<bool> ReplaceIfUnchangedAsync(StoredProviderRow row, CancellationToken ct)
    {
        try
        {
            await _container.ReplaceItemAsync(row.Row, row.Row.Id, new PartitionKey(row.Row.TenantId),
                new ItemRequestOptions { IfMatchEtag = row.ETag }, ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}

/// <summary>Bank-account records in the Cosmos <c>ProviderBankAccounts</c> container.</summary>
public sealed class CosmosBankAccountRecordStore : IBankAccountRecordStore
{
    private readonly Container _container;
    private readonly CosmosProviderBankAccountRepository _repository;

    public CosmosBankAccountRecordStore(CosmosClient client, IConfiguration configuration)
    {
        _container = client.GetContainer(
            configuration["CosmosDb:DatabaseName"] ?? "ProviderDB",
            configuration["CosmosDb:ProviderBankAccountsContainer"] ?? CosmosProviderBankAccountRepository.DefaultContainerName);
        _repository = new CosmosProviderBankAccountRepository(client, configuration);
    }

    public async IAsyncEnumerable<ProviderBankAccountRecord> ListAsync(string? tenant, [EnumeratorCancellation] CancellationToken ct)
    {
        var options = tenant == null ? new QueryRequestOptions() : new QueryRequestOptions { PartitionKey = new PartitionKey(tenant) };
        using var iterator = _container.GetItemQueryIterator<ProviderBankAccountRecord>(new QueryDefinition("SELECT * FROM c"), requestOptions: options);
        while (iterator.HasMoreResults)
        {
            foreach (var record in await iterator.ReadNextAsync(ct))
            {
                if (tenant == null || string.Equals(record.TenantId, tenant, StringComparison.Ordinal)) yield return record;
            }
        }
    }

    /// <summary>The service's own revision-checked save (ETag-conditional replace).</summary>
    public Task<bool> SaveAsync(ProviderBankAccountRecord record, long expectedRevision, CancellationToken ct)
        => _repository.SaveAsync(record, expectedRevision, ct);
}
