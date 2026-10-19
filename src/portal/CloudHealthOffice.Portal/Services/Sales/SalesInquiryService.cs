using MongoDB.Driver;

namespace CloudHealthOffice.Portal.Services;

public class SalesInquiryService : ISalesInquiryService
{
    private readonly IMongoCollection<SalesInquiry> _inquiriesCollection;
    private readonly IEmailNotificationService _emailNotificationService;
    private readonly ILogger<SalesInquiryService> _logger;

    public SalesInquiryService(IMongoClient mongoClient, IConfiguration configuration,
        IEmailNotificationService emailNotificationService, ILogger<SalesInquiryService> logger)
    {
        _logger = logger;
        _emailNotificationService = emailNotificationService;
        var databaseName = configuration["MongoDB:DatabaseName"] ?? "CloudHealthOffice";
        var db = mongoClient.GetDatabase(databaseName);
        _inquiriesCollection = db.GetCollection<SalesInquiry>(
            configuration["MongoDB:SalesInquiriesCollection"] ?? "SalesInquiries");
    }

    public async Task<string> CreateInquiryAsync(CreateSalesInquiryRequest request)
    {
        try
        {
            var inquiryId = $"inquiry-{Guid.NewGuid():N}";
            var now = DateTime.UtcNow;

            var inquiry = new SalesInquiry
            {
                Id = inquiryId,
                FirstName = request.FirstName,
                LastName = request.LastName,
                Email = request.Email,
                Phone = request.Phone,
                CompanyName = request.CompanyName,
                JobTitle = request.JobTitle,
                InquiryType = request.InquiryType,
                Message = request.Message,
                Status = "New",
                Source = request.Source,
                CreatedAt = now,
                ContactedAt = null,
                Notes = null
            };

            _logger.LogInformation("Creating sales inquiry {InquiryId} from {Email} at {Company}",
                inquiryId, request.Email, request.CompanyName);

            await _inquiriesCollection.InsertOneAsync(inquiry);

            _logger.LogInformation("Successfully created sales inquiry {InquiryId}", inquiryId);

            await _emailNotificationService.SendSalesInquiryNotificationAsync(inquiry);

            return inquiryId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create sales inquiry from {Email}", request.Email);
            throw;
        }
    }

    public async Task<List<SalesInquiry>> GetInquiriesAsync(string? status = null, int limit = 100)
    {
        try
        {
            FilterDefinition<SalesInquiry> filter = status == null
                ? Builders<SalesInquiry>.Filter.Empty
                : Builders<SalesInquiry>.Filter.Eq(i => i.Status, status);

            var results = await _inquiriesCollection
                .Find(filter)
                .SortByDescending(i => i.CreatedAt)
                .Limit(limit)
                .ToListAsync();

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching sales inquiries");
            return new List<SalesInquiry>();
        }
    }

    public async Task<SalesInquiry?> GetInquiryByIdAsync(string inquiryId)
    {
        try
        {
            var filter = Builders<SalesInquiry>.Filter.Eq(i => i.Id, inquiryId);
            return await _inquiriesCollection.Find(filter).FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching sales inquiry {InquiryId}", inquiryId);
            return null;
        }
    }

    public async Task UpdateInquiryStatusAsync(string inquiryId, string status, string? notes = null)
    {
        try
        {
            var filter = Builders<SalesInquiry>.Filter.Eq(i => i.Id, inquiryId);
            var inquiry = await _inquiriesCollection.Find(filter).FirstOrDefaultAsync();

            if (inquiry == null)
                throw new InvalidOperationException($"Inquiry {inquiryId} not found");

            var updates = new List<UpdateDefinition<SalesInquiry>>
            {
                Builders<SalesInquiry>.Update.Set(i => i.Status, status)
            };

            if (notes != null)
                updates.Add(Builders<SalesInquiry>.Update.Set(i => i.Notes, notes));

            if (status == "Contacted" && inquiry.ContactedAt == null)
                updates.Add(Builders<SalesInquiry>.Update.Set(i => i.ContactedAt, DateTime.UtcNow));

            var combinedUpdate = Builders<SalesInquiry>.Update.Combine(updates);
            await _inquiriesCollection.UpdateOneAsync(filter, combinedUpdate);

            _logger.LogInformation("Updated sales inquiry {InquiryId} status to {Status}", inquiryId, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating sales inquiry {InquiryId}", inquiryId);
            throw;
        }
    }
}
