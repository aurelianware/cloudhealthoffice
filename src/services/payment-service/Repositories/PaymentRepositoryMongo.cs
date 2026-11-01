using MongoDB.Driver;
using PaymentService.Models;
using CloudHealthOffice.Infrastructure.Logging;

namespace PaymentService.Repositories;

public class PaymentRepositoryMongo : IPaymentRepository
{
    private readonly IMongoCollection<Payment> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<PaymentRepositoryMongo> _logger;

    public PaymentRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<PaymentRepositoryMongo> logger)
    {
        _collection = database.GetCollection<Payment>("Payments");
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

    public async Task<Payment?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Payment>.Filter.And(
            Builders<Payment>.Filter.Eq(x => x.Id, id),
            Builders<Payment>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<Payment?> GetByCheckNumberAsync(string checkNumber)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Payment>.Filter.And(
            Builders<Payment>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<Payment>.Filter.Eq(x => x.CheckNumber, checkNumber));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<Payment>> GetByClaimIdAsync(string claimId)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Payment>.Filter.And(
            Builders<Payment>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<Payment>.Filter.ElemMatch(x => x.ClaimPayments,
                Builders<ClaimPayment>.Filter.Eq(cp => cp.ClaimId, claimId)));
        return await _collection.Find(filter).ToListAsync();
    }

    public async Task<IReadOnlyList<Payment>> GetByPayeeNpiAsync(string payeeNpi, int limit)
    {
        var filter = Builders<Payment>.Filter.And(
            Builders<Payment>.Filter.Eq(x => x.TenantId, GetTenantId()),
            Builders<Payment>.Filter.Eq(x => x.PayeeNPI, payeeNpi.Trim()));
        return await _collection.Find(filter)
            .SortByDescending(x => x.PaymentDate)
            .Limit(Math.Clamp(limit, 1, 500))
            .ToListAsync();
    }

    public async Task<IReadOnlyCollection<string>> GetClaimIdsWithPaymentAsync(IReadOnlyCollection<string> claimIds, bool reversal)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (claimIds.Count == 0)
            return found;

        var tenantId = GetTenantId();
        foreach (var chunk in claimIds.Distinct(StringComparer.Ordinal).Chunk(500))
        {
            var wanted = new HashSet<string>(chunk, StringComparer.Ordinal);
            var filter = Builders<Payment>.Filter.And(
                Builders<Payment>.Filter.Eq(x => x.TenantId, tenantId),
                // Documents written before IsReversal existed count as payments.
                reversal
                    ? Builders<Payment>.Filter.Eq(x => x.IsReversal, true)
                    : Builders<Payment>.Filter.Ne(x => x.IsReversal, true),
                Builders<Payment>.Filter.ElemMatch(x => x.ClaimPayments,
                    Builders<ClaimPayment>.Filter.In(cp => cp.ClaimId, chunk)));
            var payments = await _collection.Find(filter)
                .Project(Builders<Payment>.Projection.Include(x => x.ClaimPayments))
                .As<Payment>()
                .ToListAsync();
            foreach (var payment in payments)
                found.UnionWith(payment.ClaimPayments.Select(cp => cp.ClaimId).Where(wanted.Contains));
        }

        return found;
    }

    public async Task<IEnumerable<Payment>> SearchAsync(
        DateTime? paymentDateFrom,
        DateTime? paymentDateTo,
        string? payerId,
        PaymentStatus? status,
        int page = 1,
        int pageSize = 50)
    {
        var tenantId = GetTenantId();
        var filters = new List<FilterDefinition<Payment>>
        {
            Builders<Payment>.Filter.Eq(x => x.TenantId, tenantId)
        };

        if (paymentDateFrom.HasValue)
            filters.Add(Builders<Payment>.Filter.Gte(x => x.PaymentDate, paymentDateFrom.Value));
        if (paymentDateTo.HasValue)
            filters.Add(Builders<Payment>.Filter.Lte(x => x.PaymentDate, paymentDateTo.Value));
        if (!string.IsNullOrEmpty(payerId))
            filters.Add(Builders<Payment>.Filter.Eq(x => x.PayerId, payerId));
        if (status.HasValue)
            filters.Add(Builders<Payment>.Filter.Eq(x => x.Status, status.Value));

        return await _collection
            .Find(Builders<Payment>.Filter.And(filters))
            .SortByDescending(x => x.PaymentDate)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<PaymentsSummary> GetPaymentsSummaryAsync(DateTime from, DateTime to)
    {
        var tenantId = GetTenantId();
        var payments = (await SearchAsync(from, to, null, null, 1, 10000)).ToList();

        var summary = new PaymentsSummary
        {
            TotalPayments = payments.Count,
            TotalPaymentAmount = payments.Sum(p => p.TotalPaymentAmount),
            TotalClaims = payments.Sum(p => p.ClaimPayments.Count)
        };

        foreach (var payment in payments)
        {
            var statusKey = payment.Status.ToString();
            summary.ClaimsByStatus.TryAdd(statusKey, 0);
            summary.ClaimsByStatus[statusKey]++;

            if (payment.Status == PaymentStatus.Posted) summary.PostedPayments++;
            else if (payment.Status == PaymentStatus.Exception) summary.ExceptionPayments++;
            else if (payment.Status is PaymentStatus.Received or PaymentStatus.Validated) summary.UnpostedPayments++;

            summary.PaymentsByPayer.TryAdd(payment.PayerName, 0);
            summary.PaymentsByPayer[payment.PayerName] += payment.TotalPaymentAmount;
        }

        return summary;
    }

    public async Task<Payment> CreateAsync(Payment payment)
    {
        payment.TenantId = GetTenantId();
        payment.ReceivedAt = DateTime.UtcNow;
        payment.Revision = 0;
        await _collection.InsertOneAsync(payment);
        _logger.LogInformation("Created payment {PaymentId} for check {CheckNumber}",
            LogSanitizer.Sanitize(payment.Id), LogSanitizer.Sanitize(payment.CheckNumber));
        return payment;
    }

    /// <summary>The document still has the revision the write was based on (0 matches documents written before the field existed).</summary>
    private static FilterDefinition<Payment> RevisionIs(long expected)
    {
        var b = Builders<Payment>.Filter;
        return expected == 0
            ? b.Or(b.Eq(x => x.Revision, 0L), b.Exists(x => x.Revision, false))
            : b.Eq(x => x.Revision, expected);
    }

    public async Task<Payment> UpdateAsync(Payment payment)
    {
        var filter = Builders<Payment>.Filter.And(
            Builders<Payment>.Filter.Eq(x => x.Id, payment.Id),
            Builders<Payment>.Filter.Eq(x => x.TenantId, payment.TenantId));

        // Conditional on the revision the caller read: a concurrent save in
        // between (a request and a run finalizing the same payment) makes this
        // one fail instead of silently overwriting it.
        var expected = payment.Revision;
        payment.Revision = expected + 1;
        ReplaceOneResult result;
        try
        {
            result = await _collection.ReplaceOneAsync(filter & RevisionIs(expected), payment);
        }
        catch
        {
            payment.Revision = expected;
            throw;
        }

        if (result.MatchedCount == 0)
        {
            payment.Revision = expected;
            if (await _collection.Find(filter).AnyAsync())
                throw new PaymentConcurrencyException("Payment", payment.Id, expected);
            // Missing: as before, nothing is written and the payment is returned.
        }

        _logger.LogInformation("Updated payment {PaymentId}", payment.Id);
        return payment;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Payment>.Filter.And(
            Builders<Payment>.Filter.Eq(x => x.Id, id),
            Builders<Payment>.Filter.Eq(x => x.TenantId, tenantId));
        await _collection.DeleteOneAsync(filter);
        _logger.LogInformation("Deleted payment {PaymentId}", id);
    }

    public async Task<IReadOnlyList<Services.InformationReturns.IssuedPayment>> GetIssuedPaymentsAsync(DateTime from, DateTime before, CancellationToken ct = default)
    {
        var b = Builders<Payment>.Filter;
        var filter = b.Eq(x => x.TenantId, GetTenantId())
                     & b.Gte(x => x.PaymentDate, from)
                     & b.Lt(x => x.PaymentDate, before)
                     & b.Ne(x => x.RunId, null)
                     & b.Eq(x => x.VoidedBeforePaymentAt, null);
        var rows = await _collection.Find(filter)
            .Project(x => new { x.PayeeNPI, x.PayeeName, x.TotalPaymentAmount, x.PaymentDate, x.IsReversal })
            .ToListAsync(ct);
        return rows.Select(r => new Services.InformationReturns.IssuedPayment(
            r.PayeeNPI, r.PayeeName, r.TotalPaymentAmount, r.PaymentDate, r.IsReversal)).ToList();
    }
}
