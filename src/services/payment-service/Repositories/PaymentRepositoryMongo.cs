using MongoDB.Driver;
using PaymentService.Models;

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
        await _collection.InsertOneAsync(payment);
        _logger.LogInformation("Created payment {PaymentId} for check {CheckNumber}",
            SanitizeForLog(payment.Id), SanitizeForLog(payment.CheckNumber));
        return payment;
    }

    public async Task<Payment> UpdateAsync(Payment payment)
    {
        var filter = Builders<Payment>.Filter.And(
            Builders<Payment>.Filter.Eq(x => x.Id, payment.Id),
            Builders<Payment>.Filter.Eq(x => x.TenantId, payment.TenantId));
        await _collection.ReplaceOneAsync(filter, payment);
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

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
