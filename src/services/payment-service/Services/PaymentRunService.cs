using PaymentService.Models;
using PaymentService.Repositories;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaymentService.Services;

public interface IPaymentRunService
{
    Task<PaymentRun> CreatePaymentRunAsync(PaymentRunCriteria criteria, string? createdBy = null);
    Task<PaymentRun> ExecutePaymentRunAsync(string paymentRunId);
    Task<PaymentRun> GetPaymentRunAsync(string paymentRunId);
    Task<IEnumerable<PaymentRun>> GetPaymentRunsAsync(DateTime? from = null, DateTime? to = null);
    Task CancelPaymentRunAsync(string paymentRunId);
}

public class PaymentRunService : IPaymentRunService
{
    private readonly IPaymentRepository _paymentRepository;
    private readonly IPaymentRunRepository _paymentRunRepository;
    private readonly HttpClient _claimsServiceClient;
    private readonly ILogger<PaymentRunService> _logger;
    private readonly IConfiguration _configuration;

    public PaymentRunService(
        IPaymentRepository paymentRepository,
        IPaymentRunRepository paymentRunRepository,
        IHttpClientFactory httpClientFactory,
        ILogger<PaymentRunService> logger,
        IConfiguration configuration)
    {
        _paymentRepository = paymentRepository;
        _paymentRunRepository = paymentRunRepository;
        _claimsServiceClient = httpClientFactory.CreateClient("ClaimsService");
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<PaymentRun> CreatePaymentRunAsync(PaymentRunCriteria criteria, string? createdBy = null)
    {
        var paymentRun = new PaymentRun
        {
            PaymentRunNumber = $"PR-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper()}",
            Criteria = criteria,
            CreatedBy = createdBy,
            Status = PaymentRunStatus.Pending,
            NextCheckNumber = await GetNextCheckNumberAsync()
        };

        var created = await _paymentRunRepository.CreateAsync(paymentRun);
        
        _logger.LogInformation("Created payment run {PaymentRunNumber}", created.PaymentRunNumber);

        return created;
    }

    public async Task<PaymentRun> ExecutePaymentRunAsync(string paymentRunId)
    {
        var paymentRun = await _paymentRunRepository.GetByIdAsync(paymentRunId);
        
        if (paymentRun == null)
        {
            throw new InvalidOperationException($"Payment run {paymentRunId} not found");
        }

        if (paymentRun.Status != PaymentRunStatus.Pending)
        {
            throw new InvalidOperationException($"Payment run {paymentRunId} is not in Pending status");
        }

        paymentRun.Status = PaymentRunStatus.Running;
        paymentRun.ExecutionStartedAt = DateTime.UtcNow;
        await _paymentRunRepository.UpdateAsync(paymentRun);

        try
        {
            // Step 1: Fetch approved/finalized claims from claims service
            var claims = await FetchApprovedClaimsAsync(paymentRun.Criteria);
            
            _logger.LogInformation("Found {ClaimCount} approved claims for payment run {PaymentRunNumber}",
                claims.Count, paymentRun.PaymentRunNumber);

            if (!claims.Any())
            {
                paymentRun.Warnings.Add("No approved claims found matching criteria");
                paymentRun.Status = PaymentRunStatus.Completed;
                paymentRun.ExecutionCompletedAt = DateTime.UtcNow;
                paymentRun.ExecutionDurationSeconds = (paymentRun.ExecutionCompletedAt.Value - paymentRun.ExecutionStartedAt.Value).TotalSeconds;
                return await _paymentRunRepository.UpdateAsync(paymentRun);
            }

            // Step 2: Group claims by provider if requested
            var claimGroups = GroupClaimsByProvider(claims, paymentRun.Criteria);

            // Step 3: Generate payments for each group
            foreach (var group in claimGroups)
            {
                var payment = await GeneratePaymentForClaimsAsync(
                    group.Value, 
                    paymentRun, 
                    group.Key);

                paymentRun.PaymentIds.Add(payment.Id);
                paymentRun.ClaimIds.AddRange(group.Value.Select(c => c.Id));
                paymentRun.TotalPaymentAmount += payment.TotalPaymentAmount;
            }

            paymentRun.TotalClaims = claims.Count;
            paymentRun.CheckNumberStart = paymentRun.NextCheckNumber.ToString();
            paymentRun.CheckNumberEnd = (paymentRun.NextCheckNumber + paymentRun.PaymentIds.Count - 1).ToString();

            // Step 4: Update claim statuses to Paid
            await UpdateClaimStatusesToPaidAsync(paymentRun.ClaimIds);

            paymentRun.Status = PaymentRunStatus.Completed;
            paymentRun.ExecutionCompletedAt = DateTime.UtcNow;
            paymentRun.ExecutionDurationSeconds = (paymentRun.ExecutionCompletedAt.Value - paymentRun.ExecutionStartedAt.Value).TotalSeconds;

            _logger.LogInformation("Payment run {PaymentRunNumber} completed: {ClaimCount} claims, {PaymentCount} payments, ${TotalAmount:N2}",
                paymentRun.PaymentRunNumber, paymentRun.TotalClaims, paymentRun.PaymentIds.Count, paymentRun.TotalPaymentAmount);

            return await _paymentRunRepository.UpdateAsync(paymentRun);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing payment run {PaymentRunId}", SanitizeForLog(paymentRunId));
            
            paymentRun.Status = PaymentRunStatus.Failed;
            paymentRun.Errors.Add($"Execution failed: {ex.Message}");
            paymentRun.ExecutionCompletedAt = DateTime.UtcNow;
            paymentRun.ExecutionDurationSeconds = paymentRun.ExecutionStartedAt.HasValue 
                ? (paymentRun.ExecutionCompletedAt.Value - paymentRun.ExecutionStartedAt.Value).TotalSeconds 
                : 0;

            await _paymentRunRepository.UpdateAsync(paymentRun);
            
            throw;
        }
    }

    public async Task<PaymentRun> GetPaymentRunAsync(string paymentRunId)
    {
        var paymentRun = await _paymentRunRepository.GetByIdAsync(paymentRunId);
        
        if (paymentRun == null)
        {
            throw new InvalidOperationException($"Payment run {paymentRunId} not found");
        }

        return paymentRun;
    }

    public async Task<IEnumerable<PaymentRun>> GetPaymentRunsAsync(DateTime? from = null, DateTime? to = null)
    {
        return await _paymentRunRepository.SearchAsync(
            from ?? DateTime.UtcNow.AddMonths(-3),
            to ?? DateTime.UtcNow);
    }

    public async Task CancelPaymentRunAsync(string paymentRunId)
    {
        var paymentRun = await _paymentRunRepository.GetByIdAsync(paymentRunId);
        
        if (paymentRun == null)
        {
            throw new InvalidOperationException($"Payment run {paymentRunId} not found");
        }

        if (paymentRun.Status == PaymentRunStatus.Running)
        {
            throw new InvalidOperationException("Cannot cancel a running payment run");
        }

        paymentRun.Status = PaymentRunStatus.Cancelled;
        await _paymentRunRepository.UpdateAsync(paymentRun);
    }

    // Private helper methods

    private async Task<List<ClaimDto>> FetchApprovedClaimsAsync(PaymentRunCriteria criteria)
    {
        var queryParams = new List<string>();

        // Build query string
        if (criteria.LineOfBusiness.HasValue)
            queryParams.Add($"lineOfBusiness={(int)criteria.LineOfBusiness.Value}");
        
        if (!string.IsNullOrEmpty(criteria.ProviderNPI))
            queryParams.Add($"providerNPI={criteria.ProviderNPI}");

        if (criteria.ServiceDateFrom.HasValue)
            queryParams.Add($"serviceDateFrom={criteria.ServiceDateFrom.Value:yyyy-MM-dd}");

        if (criteria.ServiceDateTo.HasValue)
            queryParams.Add($"serviceDateTo={criteria.ServiceDateTo.Value:yyyy-MM-dd}");

        // Fetch claims with Approved or Finalized status
        queryParams.Add("status=4"); // Approved
        
        var queryString = string.Join("&", queryParams);
        var response = await _claimsServiceClient.GetAsync($"/api/claims?{queryString}&pageSize=5000");

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Failed to fetch claims from claims service: {response.StatusCode}");
        }

        var claims = await response.Content.ReadFromJsonAsync<List<ClaimDto>>() ?? new List<ClaimDto>();

        // Apply additional filters
        if (criteria.MinClaimAmount.HasValue)
            claims = claims.Where(c => c.TotalChargeAmount >= criteria.MinClaimAmount.Value).ToList();

        if (criteria.MaxClaimAmount.HasValue)
            claims = claims.Where(c => c.TotalChargeAmount <= criteria.MaxClaimAmount.Value).ToList();

        if (criteria.IncludeClaimIds.Any())
            claims = claims.Where(c => criteria.IncludeClaimIds.Contains(c.Id)).ToList();

        if (criteria.ExcludeClaimIds.Any())
            claims = claims.Where(c => !criteria.ExcludeClaimIds.Contains(c.Id)).ToList();

        if (criteria.MemberIds.Any())
            claims = claims.Where(c => criteria.MemberIds.Contains(c.MemberId)).ToList();

        return claims;
    }

    private Dictionary<string, List<ClaimDto>> GroupClaimsByProvider(List<ClaimDto> claims, PaymentRunCriteria criteria)
    {
        if (!criteria.GroupByProvider)
        {
            return new Dictionary<string, List<ClaimDto>> { { "ALL", claims } };
        }

        var groups = claims.GroupBy(c => c.PayToProviderNPI ?? c.BillingProviderNPI)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Apply max claims per payment limit if specified
        if (criteria.MaxClaimsPerPayment.HasValue)
        {
            var result = new Dictionary<string, List<ClaimDto>>();
            int batchNumber = 0;

            foreach (var group in groups)
            {
                var chunks = group.Value.Chunk(criteria.MaxClaimsPerPayment.Value);
                foreach (var chunk in chunks)
                {
                    result[$"{group.Key}-{++batchNumber}"] = chunk.ToList();
                }
            }

            return result;
        }

        return groups;
    }

    private async Task<Payment> GeneratePaymentForClaimsAsync(
        List<ClaimDto> claims, 
        PaymentRun paymentRun,
        string providerKey)
    {
        var firstClaim = claims.First();
        var checkNumber = (paymentRun.NextCheckNumber++).ToString().PadLeft(10, '0');

        var payment = new Payment
        {
            CheckNumber = checkNumber,
            PaymentMethod = paymentRun.PaymentMethod,
            TotalPaymentAmount = claims.Sum(c => c.ApprovedAmount ?? c.TotalChargeAmount),
            PaymentDate = paymentRun.PaymentDate,
            PayerName = _configuration["Payer:Name"] ?? "Cloud Health Office",
            PayerId = _configuration["Payer:Id"] ?? "CHO",
            PayeeName = firstClaim.ProviderName ?? providerKey,
            PayeeNPI = firstClaim.PayToProviderNPI ?? firstClaim.BillingProviderNPI,
            Status = PaymentStatus.Posted,
            ClaimPayments = claims.Select(claim => new ClaimPayment
            {
                ClaimId = claim.Id,
                PatientControlNumber = claim.ClaimNumber,
                ClaimStatusCode = "1", // Processed as primary
                ChargeAmount = claim.TotalChargeAmount,
                PaymentAmount = claim.ApprovedAmount ?? claim.TotalChargeAmount,
                PatientResponsibilityAmount = claim.PatientResponsibility ?? 0,
                PayerClaimControlNumber = claim.PayerClaimControlNumber,
                MemberId = claim.MemberId,
                RenderingProviderNPI = claim.RenderingProviderNPI,
                ServiceLines = new List<ServiceLinePayment>() // Populated from claim service lines
            }).ToList()
        };

        return await _paymentRepository.CreateAsync(payment);
    }

    private async Task UpdateClaimStatusesToPaidAsync(List<string> claimIds)
    {
        foreach (var claimId in claimIds)
        {
            try
            {
                var updateRequest = new { Status = ClaimStatus.Paid };
                var response = await _claimsServiceClient.PutAsJsonAsync($"/api/claims/{claimId}/status", updateRequest);
                
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to update claim {ClaimId} status to Paid: {StatusCode}", 
                        SanitizeForLog(claimId), response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating claim {ClaimId} status", SanitizeForLog(claimId));
            }
        }
    }

    private async Task<int> GetNextCheckNumberAsync()
    {
        // Get last payment run's next check number, or start from configured base
        var recentRuns = await _paymentRunRepository.SearchAsync(
            DateTime.UtcNow.AddYears(-1), 
            DateTime.UtcNow);

        var lastRun = recentRuns.OrderByDescending(r => r.CreatedAt).FirstOrDefault();
        
        if (lastRun != null && lastRun.NextCheckNumber > 0)
        {
            return lastRun.NextCheckNumber;
        }

        // Default starting check number
        return int.Parse(_configuration["Payment:StartingCheckNumber"] ?? "1000000");
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

// DTOs for claims service integration

public class ClaimDto
{
    public string Id { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string BillingProviderNPI { get; set; } = string.Empty;
    public string? PayToProviderNPI { get; set; }
    public string? RenderingProviderNPI { get; set; }
    public string? ProviderName { get; set; }
    public string? PayerClaimControlNumber { get; set; }
    public decimal TotalChargeAmount { get; set; }
    public decimal? ApprovedAmount { get; set; }
    public decimal? PatientResponsibility { get; set; }
    public ClaimStatus Status { get; set; }
    public DateTime ServiceDateFrom { get; set; }
    public DateTime? SubmittedDate { get; set; }
}
