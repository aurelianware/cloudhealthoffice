using ClaimsScrubbingService.Models;
using ClaimsScrubbingService.Repositories;

namespace ClaimsScrubbingService.Services;

public interface IClaimsScrubberService
{
    Task<ValidateClaimResponse> ValidateClaimAsync(ValidateClaimRequest request);
    Task<BatchValidateResponse> ValidateBatchAsync(BatchValidateRequest request);
    ServiceMetrics GetMetrics();
}

public class ClaimsScrubberService : IClaimsScrubberService
{
    private readonly IValidationRuleEngine _ruleEngine;
    private readonly IClaimAuditRepository _auditRepository;
    private readonly IScrubRuleRepository _scrubRuleRepository;
    private readonly IKafkaProducerService? _kafka;
    private readonly IBlobArchiveService? _blob;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClaimsScrubberService> _logger;

    // Metrics (thread-safe with Interlocked)
    private long _claimsProcessed;
    private long _claimsClean;
    private long _claimsFlagged;
    private long _claimsRejected;
    private long _totalValidationTimeMs;

    public ClaimsScrubberService(
        IValidationRuleEngine ruleEngine,
        IClaimAuditRepository auditRepository,
        IScrubRuleRepository scrubRuleRepository,
        IConfiguration configuration,
        ILogger<ClaimsScrubberService> logger,
        IKafkaProducerService? kafka = null,
        IBlobArchiveService? blob = null)
    {
        _ruleEngine          = ruleEngine;
        _auditRepository     = auditRepository;
        _scrubRuleRepository = scrubRuleRepository;
        _configuration       = configuration;
        _logger              = logger;
        _kafka               = kafka;
        _blob                = blob;
    }

    public async Task<ValidateClaimResponse> ValidateClaimAsync(ValidateClaimRequest request)
    {
        var correlationId = request.CorrelationId ?? Guid.NewGuid().ToString();
        var parallelExecution = _configuration.GetValue<bool>("RuleEngine:ParallelExecution");

        var result = await _ruleEngine.ValidateClaimAsync(
            request.Claim,
            request.SkipRules,
            request.OnlyRules,
            parallelExecution);

        UpdateMetrics(result);

        // Fire-and-forget non-critical side effects (audit, archive, route)
        // Errors are swallowed inside each method to avoid failing the response.
        await _auditRepository.InsertAuditAsync(request.Claim, result, correlationId);

        if (_blob != null)
            await _blob.ArchiveClaimResultAsync(request.Claim, result);

        if (_kafka != null)
            await _kafka.RouteClaimAsync(request.Claim, result);

        return new ValidateClaimResponse
        {
            Result         = result,
            CorrectedClaim = null, // autoCorrect accepted but not implemented
            CorrelationId  = correlationId,
            Timestamp      = DateTime.UtcNow.ToString("O")
        };
    }

    public async Task<BatchValidateResponse> ValidateBatchAsync(BatchValidateRequest request)
    {
        var sw            = System.Diagnostics.Stopwatch.StartNew();
        var correlationId = request.CorrelationId ?? Guid.NewGuid().ToString();
        var parallelExecution = _configuration.GetValue<bool>("RuleEngine:ParallelExecution");
        var results       = new List<ClaimValidationResult>(request.Claims.Count);

        foreach (var claim in request.Claims)
        {
            var result = await _ruleEngine.ValidateClaimAsync(
                claim,
                request.SkipRules,
                parallelExecution: parallelExecution);

            results.Add(result);
            UpdateMetrics(result);
        }

        sw.Stop();

        int cleanCount    = results.Count(r => r.Status == "clean");
        int flaggedCount  = results.Count(r => r.Status == "flagged");
        int rejectedCount = results.Count(r => r.Status == "rejected");
        double firstPassRate = results.Count > 0 ? (double)cleanCount / results.Count * 100.0 : 100.0;

        return new BatchValidateResponse
        {
            TotalClaims          = results.Count,
            CleanClaims          = cleanCount,
            FlaggedClaims        = flaggedCount,
            RejectedClaims       = rejectedCount,
            Results              = results,
            FirstPassRate        = firstPassRate,
            TotalProcessingTimeMs = sw.ElapsedMilliseconds,
            CorrelationId        = correlationId
        };
    }

    public ServiceMetrics GetMetrics()
    {
        long processed = Interlocked.Read(ref _claimsProcessed);
        long totalTime = Interlocked.Read(ref _totalValidationTimeMs);
        long clean     = Interlocked.Read(ref _claimsClean);

        return new ServiceMetrics
        {
            ClaimsProcessed       = processed,
            ClaimsClean           = clean,
            ClaimsFlagged         = Interlocked.Read(ref _claimsFlagged),
            ClaimsRejected        = Interlocked.Read(ref _claimsRejected),
            AverageValidationTimeMs = processed > 0 ? (double)totalTime / processed : 0.0,
            FirstPassRate         = processed > 0 ? (double)clean / processed * 100.0 : 100.0
        };
    }

    private void UpdateMetrics(ClaimValidationResult result)
    {
        Interlocked.Increment(ref _claimsProcessed);
        Interlocked.Add(ref _totalValidationTimeMs, result.TotalValidationTimeMs);

        switch (result.Status)
        {
            case "clean":    Interlocked.Increment(ref _claimsClean);    break;
            case "flagged":  Interlocked.Increment(ref _claimsFlagged);  break;
            case "rejected": Interlocked.Increment(ref _claimsRejected); break;
        }
    }
}
