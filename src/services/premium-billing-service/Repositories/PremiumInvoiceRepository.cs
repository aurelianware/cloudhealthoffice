using Microsoft.Azure.Cosmos;
using PremiumBillingService.Models;

namespace PremiumBillingService.Repositories;

public interface IPremiumInvoiceRepository
{
    Task<PremiumInvoice?> GetByIdAsync(string id);
    Task<IEnumerable<PremiumInvoice>> GetByGroupNumberAsync(string groupNumber);

    /// <summary>
    /// Invoices with this invoice number (normally one; a reissued period can
    /// leave a voided one beside it). Used to match remittance references.
    /// </summary>
    Task<IEnumerable<PremiumInvoice>> GetByInvoiceNumberAsync(string invoiceNumber);
    Task<IEnumerable<PremiumInvoice>> GetByBillingPeriodAsync(DateTime billingPeriodStart);
    Task<IEnumerable<PremiumInvoice>> GetByStatusAsync(InvoiceStatus status);
    Task<IEnumerable<PremiumInvoice>> SearchAsync(
        string? groupNumber = null,
        DateTime? periodFrom = null,
        DateTime? periodTo = null,
        InvoiceStatus? status = null,
        int page = 1,
        int pageSize = 50);
    Task<IEnumerable<PremiumInvoice>> GetOverdueAsync();

    /// <summary>
    /// Return invoices whose <c>LineItems</c> include at least one line for
    /// the given <paramref name="memberId"/>, newest first. Limited to
    /// <paramref name="take"/> invoices — the portal Member Details Premium
    /// tab shows the last 12 billing periods.
    /// </summary>
    Task<IEnumerable<PremiumInvoice>> ListByMemberAsync(string memberId, int take = 12);

    /// <summary>
    /// Inserts the invoice. Throws <see cref="InvoiceAlreadyExistsException"/>
    /// when an invoice with the same id exists (rated invoices have a
    /// deterministic id per group and period, so a second generation of the
    /// same invoice cannot create a duplicate).
    /// </summary>
    Task<PremiumInvoice> CreateAsync(PremiumInvoice invoice);

    /// <summary>
    /// Saves the invoice if nobody saved it since it was read (Mongo: same
    /// <see cref="PremiumInvoice.Version"/>; Cosmos: same ETag). Otherwise
    /// throws <see cref="ConcurrencyConflictException"/>: re-read and retry.
    /// </summary>
    Task<PremiumInvoice> UpdateAsync(PremiumInvoice invoice);
    Task DeleteAsync(string id);

    /// <summary>
    /// <see cref="CreateAsync"/> fenced by the group's billing lease: nothing is
    /// written unless <paramref name="lease"/> still holds it (it is renewed).
    /// Throws <see cref="BillingLeaseLostException"/> otherwise. Stores that can
    /// do both writes atomically override this (Cosmos: transactional batch;
    /// Mongo on a replica set: a transaction); the default renews, then writes.
    /// </summary>
    async Task<PremiumInvoice> CreateFencedAsync(PremiumInvoice invoice, IBillingLeaseRepository leases, BillingLeaseHandle lease,
        DateTime now, TimeSpan duration)
    {
        if (!await leases.RenewAsync(lease, now, duration))
            throw new BillingLeaseLostException(lease.GroupNumber);
        return await CreateAsync(invoice);
    }

    /// <summary><see cref="UpdateAsync"/> fenced by the group's billing lease (see <see cref="CreateFencedAsync"/>).</summary>
    async Task<PremiumInvoice> UpdateFencedAsync(PremiumInvoice invoice, IBillingLeaseRepository leases, BillingLeaseHandle lease,
        DateTime now, TimeSpan duration)
    {
        if (!await leases.RenewAsync(lease, now, duration))
            throw new BillingLeaseLostException(lease.GroupNumber);
        return await UpdateAsync(invoice);
    }
}

/// <summary>An invoice with this id already exists.</summary>
public sealed class InvoiceAlreadyExistsException : Exception
{
    public InvoiceAlreadyExistsException(string invoiceId)
        : base($"Invoice {invoiceId} already exists")
    {
        InvoiceId = invoiceId;
    }

    public string InvoiceId { get; }
}

public class PremiumInvoiceRepository : IPremiumInvoiceRepository
{
    /// <summary>
    /// Status as stored: <see cref="Middleware.CosmosSystemTextJsonSerializer"/> writes enums as
    /// camelCase strings, so queries must compare with the same string (an int never matches).
    /// </summary>
    public static string StoredStatus(InvoiceStatus status) =>
        System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(status.ToString());

    private readonly Container _container;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<PremiumInvoiceRepository> _logger;

    public PremiumInvoiceRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<PremiumInvoiceRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        _container = cosmosClient.GetContainer(databaseName, "PremiumInvoices");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
            throw new InvalidOperationException("TenantId not found in request context");
        return tenantId;
    }

    public async Task<PremiumInvoice?> GetByIdAsync(string id)
    {
        // Billing leases share this container: a lease id is never an invoice.
        if (BillingLease.IsLeaseId(id))
            return null;
        var tenantId = GetTenantId();
        try
        {
            var response = await _container.ReadItemAsync<PremiumInvoice>(id, new PartitionKey(tenantId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IEnumerable<PremiumInvoice>> GetByGroupNumberAsync(string groupNumber)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.groupNumber = @groupNumber ORDER BY c.billingPeriodStart DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@groupNumber", groupNumber);

        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<PremiumInvoice>> GetByInvoiceNumberAsync(string invoiceNumber)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.invoiceNumber = @invoiceNumber")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@invoiceNumber", invoiceNumber);

        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<PremiumInvoice>> GetByBillingPeriodAsync(DateTime billingPeriodStart)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.billingPeriodStart = @period ORDER BY c.groupNumber")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@period", billingPeriodStart);

        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<PremiumInvoice>> GetByStatusAsync(InvoiceStatus status)
    {
        var tenantId = GetTenantId();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.status = @status ORDER BY c.dueDate")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@status", StoredStatus(status));

        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<PremiumInvoice>> SearchAsync(
        string? groupNumber = null,
        DateTime? periodFrom = null,
        DateTime? periodTo = null,
        InvoiceStatus? status = null,
        int page = 1,
        int pageSize = 50)
    {
        var tenantId = GetTenantId();
        // Billing leases share this container (docType billingLease): never list them as invoices.
        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId AND NOT IS_DEFINED(c.docType)";
        var parameters = new List<(string, object)> { ("@tenantId", tenantId) };

        if (!string.IsNullOrEmpty(groupNumber))
        {
            queryText += " AND c.groupNumber = @groupNumber";
            parameters.Add(("@groupNumber", groupNumber));
        }
        if (periodFrom.HasValue)
        {
            queryText += " AND c.billingPeriodStart >= @periodFrom";
            parameters.Add(("@periodFrom", periodFrom.Value));
        }
        if (periodTo.HasValue)
        {
            queryText += " AND c.billingPeriodStart <= @periodTo";
            parameters.Add(("@periodTo", periodTo.Value));
        }
        if (status.HasValue)
        {
            queryText += " AND c.status = @status";
            parameters.Add(("@status", StoredStatus(status.Value)));
        }

        queryText += " ORDER BY c.billingPeriodStart DESC";
        queryText += $" OFFSET {(page - 1) * pageSize} LIMIT {pageSize}";

        var queryDefinition = new QueryDefinition(queryText);
        foreach (var param in parameters)
            queryDefinition = queryDefinition.WithParameter(param.Item1, param.Item2);

        return await ExecuteQueryAsync(queryDefinition);
    }

    public async Task<IEnumerable<PremiumInvoice>> GetOverdueAsync()
    {
        var tenantId = GetTenantId();
        var now = DateTime.UtcNow;

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.balanceDue > 0 AND c.dueDate < @now " +
            "AND c.status != @voided AND c.status != @writeOff AND c.status != @paid AND c.status != @draft ORDER BY c.dueDate")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@now", now)
            .WithParameter("@voided", StoredStatus(InvoiceStatus.Voided))
            .WithParameter("@writeOff", StoredStatus(InvoiceStatus.WriteOff))
            .WithParameter("@paid", StoredStatus(InvoiceStatus.Paid))
            .WithParameter("@draft", StoredStatus(InvoiceStatus.Draft));

        return await ExecuteQueryAsync(query);
    }

    public async Task<IEnumerable<PremiumInvoice>> ListByMemberAsync(string memberId, int take = 12)
    {
        var tenantId = GetTenantId();
        // EXISTS over a JOINed sub-query into LineItems to keep the predicate
        // on the indexed (tenantId, memberId) path. Cosmos can't index inside
        // arrays without explicit composite indexes, so pull recent invoices
        // for the tenant and filter to those that mention the member.
        var query = new QueryDefinition(
            "SELECT TOP @take * FROM c " +
            "WHERE c.tenantId = @tenantId AND c.status != @draft " +
            "AND EXISTS(SELECT VALUE l FROM l IN c.lineItems WHERE l.memberId = @memberId) " +
            "ORDER BY c.billingPeriodStart DESC")
            .WithParameter("@take", take)
            .WithParameter("@draft", StoredStatus(InvoiceStatus.Draft))
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@memberId", memberId);

        return await ExecuteQueryAsync(query);
    }

    public async Task<PremiumInvoice> CreateAsync(PremiumInvoice invoice)
    {
        BillingLease.RefuseLeaseId(invoice.Id);
        invoice.TenantId = GetTenantId();
        invoice.CreatedAt = DateTime.UtcNow;
        invoice.LastUpdatedAt = DateTime.UtcNow;
        ItemResponse<PremiumInvoice> response;
        try
        {
            response = await _container.CreateItemAsync(invoice, new PartitionKey(invoice.TenantId));
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            throw new InvoiceAlreadyExistsException(invoice.Id);
        }
        response.Resource.ETag = response.ETag;
        _logger.LogInformation("Created premium invoice {InvoiceNumber} for group {GroupNumber}",
            invoice.InvoiceNumber, invoice.GroupNumber);
        return response.Resource;
    }

    public async Task<PremiumInvoice> UpdateAsync(PremiumInvoice invoice)
    {
        BillingLease.RefuseLeaseId(invoice.Id);
        invoice.LastUpdatedAt = DateTime.UtcNow;
        invoice.Version++;
        try
        {
            var options = invoice.ETag == null ? null : new ItemRequestOptions { IfMatchEtag = invoice.ETag };
            var response = await _container.ReplaceItemAsync(invoice, invoice.Id, new PartitionKey(invoice.TenantId), options);
            response.Resource.ETag = response.ETag;
            _logger.LogInformation("Updated premium invoice {InvoiceNumber}", invoice.InvoiceNumber);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            invoice.Version--;
            throw new ConcurrencyConflictException($"Invoice {invoice.InvoiceNumber} was changed by someone else; re-read it and retry");
        }
    }

    public Task<PremiumInvoice> CreateFencedAsync(PremiumInvoice invoice, IBillingLeaseRepository leases, BillingLeaseHandle lease,
        DateTime now, TimeSpan duration) => WriteFencedAsync(invoice, create: true, leases, lease, now, duration);

    public Task<PremiumInvoice> UpdateFencedAsync(PremiumInvoice invoice, IBillingLeaseRepository leases, BillingLeaseHandle lease,
        DateTime now, TimeSpan duration) => WriteFencedAsync(invoice, create: false, leases, lease, now, duration);

    /// <summary>
    /// True fencing: one transactional batch in the tenant's partition replaces the lease
    /// document only if it is still the version this holder wrote (renewing it) and
    /// creates or replaces the invoice (ETag-checked). Either both are written or neither.
    /// </summary>
    private async Task<PremiumInvoice> WriteFencedAsync(PremiumInvoice invoice, bool create, IBillingLeaseRepository leases,
        BillingLeaseHandle lease, DateTime now, TimeSpan duration)
    {
        BillingLease.RefuseLeaseId(invoice.Id);
        if (leases is not BillingLeaseRepositoryCosmos || lease.ETag == null)
        {
            if (!await leases.RenewAsync(lease, now, duration))
                throw new BillingLeaseLostException(lease.GroupNumber);
            return create ? await CreateAsync(invoice) : await UpdateAsync(invoice);
        }

        var tenantId = GetTenantId();
        var key = new PartitionKey(tenantId);
        var leaseDocument = new BillingLease
        {
            Id = BillingLease.IdFor(tenantId, lease.GroupNumber), TenantId = tenantId, LeaseGroupNumber = lease.GroupNumber,
            LeaseOwner = lease.Owner, AcquiredAt = now, ExpiresAt = now + duration
        };
        invoice.TenantId = tenantId;
        invoice.LastUpdatedAt = DateTime.UtcNow;
        if (create)
            invoice.CreatedAt = invoice.LastUpdatedAt;
        else
            invoice.Version++;

        var batch = _container.CreateTransactionalBatch(key)
            .ReplaceItem(leaseDocument.Id, leaseDocument, new TransactionalBatchItemRequestOptions { IfMatchEtag = lease.ETag });
        batch = create
            ? batch.CreateItem(invoice)
            : batch.ReplaceItem(invoice.Id, invoice, new TransactionalBatchItemRequestOptions { IfMatchEtag = invoice.ETag });

        using var response = await batch.ExecuteAsync();
        if (!response.IsSuccessStatusCode)
        {
            if (!create)
                invoice.Version--;
            if (response.Count > 0 && response[0].StatusCode is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.NotFound)
                throw new BillingLeaseLostException(lease.GroupNumber);
            var invoiceStatus = response.Count > 1 ? response[1].StatusCode : response.StatusCode;
            if (invoiceStatus == System.Net.HttpStatusCode.Conflict)
                throw new InvoiceAlreadyExistsException(invoice.Id);
            if (invoiceStatus is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.NotFound)
                throw new ConcurrencyConflictException($"Invoice {invoice.InvoiceNumber} was changed by someone else; re-read it and retry");
            throw new InvalidOperationException($"Fenced write of invoice {invoice.InvoiceNumber} failed: {response.StatusCode} {response.ErrorMessage}");
        }

        lease.ETag = response[0].ETag;
        var saved = response.GetOperationResultAtIndex<PremiumInvoice>(1).Resource;
        saved.ETag = response[1].ETag;
        _logger.LogInformation("Wrote premium invoice {InvoiceNumber} under the billing lease of group {GroupNumber}",
            saved.InvoiceNumber, lease.GroupNumber);
        return saved;
    }

    public async Task DeleteAsync(string id)
    {
        BillingLease.RefuseLeaseId(id);
        var tenantId = GetTenantId();
        await _container.DeleteItemAsync<PremiumInvoice>(id, new PartitionKey(tenantId));
        _logger.LogInformation("Deleted premium invoice {Id}", SanitizeForLog(id));
    }

    private async Task<List<PremiumInvoice>> ExecuteQueryAsync(QueryDefinition query)
    {
        var iterator = _container.GetItemQueryIterator<PremiumInvoice>(query);
        var results = new List<PremiumInvoice>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }
        return results;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
