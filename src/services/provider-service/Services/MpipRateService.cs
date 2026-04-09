using MongoDB.Driver;
using ProviderService.Models;

namespace ProviderService.Services;

/// <summary>
/// Service contract for FL SMMC 3.0 MPIP (Managed Medical Assistance
/// Physician Incentive Program) provider qualification and rate calculation.
/// </summary>
public interface IMpipRateService
{
    /// <summary>
    /// Get a provider's MPIP qualification for a specific period.
    /// </summary>
    Task<MpipProviderQualification?> GetQualificationAsync(
        string providerId, string tenantId, string qualificationPeriod);

    /// <summary>
    /// Calculate the MPIP enhanced rate multiplier for a service.
    /// Returns 1.063 for qualified encounters (member under 21 + qualified provider),
    /// or 1.0 otherwise.
    /// </summary>
    Task<decimal> GetEnhancedRateMultiplierAsync(
        string providerId, string tenantId,
        DateTime serviceDate, int memberAgeAtServiceDate);

    /// <summary>
    /// Create or update a provider's MPIP qualification record.
    /// </summary>
    Task UpsertQualificationAsync(MpipProviderQualification qualification);

    /// <summary>
    /// Get all qualified providers for a tenant in a given period.
    /// </summary>
    Task<IEnumerable<MpipProviderQualification>> GetQualifiedProvidersAsync(
        string tenantId, string period);
}

/// <summary>
/// Implements FL SMMC 3.0 MPIP rate logic:
/// <list type="bullet">
///   <item>Member age &gt;= 21 → always 1.0x (no enhancement).</item>
///   <item>Specialist + member age &lt; 21 → auto-qualify → 1.063x.</item>
///   <item>PCP or OB/GYN + IsQualified + member age &lt; 21 → 1.063x.</item>
///   <item>Otherwise → 1.0x.</item>
/// </list>
/// Qualification period follows the FL fiscal year: Oct 1 – Sep 30.
/// </summary>
public class MpipRateService : IMpipRateService
{
    private readonly IMongoCollection<MpipProviderQualification> _collection;
    private readonly ILogger<MpipRateService> _logger;

    /// <summary>
    /// FL SMMC 3.0 enhanced rate: 106.3% of Medicare Physician Fee Schedule.
    /// </summary>
    public const decimal EnhancedMultiplier = 1.063m;

    /// <summary>
    /// Standard rate (no enhancement).
    /// </summary>
    public const decimal StandardMultiplier = 1.0m;

    /// <summary>
    /// MPIP only applies to members under this age.
    /// </summary>
    public const int MaxMemberAge = 21;

    public MpipRateService(
        IMongoDatabase database,
        ILogger<MpipRateService> logger)
    {
        _collection = database.GetCollection<MpipProviderQualification>("mpip_qualifications");
        _logger = logger;
    }

    // ── GetQualification ─────────────────────────────────────────────

    public async Task<MpipProviderQualification?> GetQualificationAsync(
        string providerId, string tenantId, string qualificationPeriod)
    {
        var filter = Builders<MpipProviderQualification>.Filter.And(
            Builders<MpipProviderQualification>.Filter.Eq(q => q.ProviderId, providerId),
            Builders<MpipProviderQualification>.Filter.Eq(q => q.TenantId, tenantId),
            Builders<MpipProviderQualification>.Filter.Eq(q => q.QualificationPeriod, qualificationPeriod)
        );

        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    // ── GetEnhancedRateMultiplier ────────────────────────────────────

    public async Task<decimal> GetEnhancedRateMultiplierAsync(
        string providerId, string tenantId,
        DateTime serviceDate, int memberAgeAtServiceDate)
    {
        // Rule: member must be under 21 for any enhancement
        if (memberAgeAtServiceDate >= MaxMemberAge)
        {
            _logger.LogDebug(
                "MPIP: member age {Age} >= {MaxAge}, returning standard multiplier for provider {ProviderId}",
                memberAgeAtServiceDate, MaxMemberAge, Sanitize(providerId));
            return StandardMultiplier;
        }

        // Determine the FL fiscal year period from the service date
        var period = GetFiscalYearPeriod(serviceDate);

        var qualification = await GetQualificationAsync(providerId, tenantId, period);

        if (qualification is null)
        {
            _logger.LogDebug(
                "MPIP: no qualification record for provider {ProviderId} in period {Period}, " +
                "returning standard multiplier",
                Sanitize(providerId), period);
            return StandardMultiplier;
        }

        // Specialist auto-qualifies for members under 21
        if (qualification.ProviderType == MpipProviderType.Specialist)
        {
            _logger.LogInformation(
                "MPIP: specialist provider {ProviderId} auto-qualifies for {Multiplier}x " +
                "(member age {Age}, period {Period})",
                Sanitize(providerId), EnhancedMultiplier, memberAgeAtServiceDate, period);
            return EnhancedMultiplier;
        }

        // PCP or OB/GYN must be explicitly qualified via AHCA benchmarks
        if (qualification.ProviderType is MpipProviderType.PrimaryCare or MpipProviderType.ObGyn
            && qualification.IsQualified)
        {
            _logger.LogInformation(
                "MPIP: {ProviderType} provider {ProviderId} qualified via {Method}, " +
                "returning {Multiplier}x (member age {Age}, period {Period})",
                qualification.ProviderType, Sanitize(providerId), qualification.QualificationMethod,
                EnhancedMultiplier, memberAgeAtServiceDate, period);
            return EnhancedMultiplier;
        }

        _logger.LogDebug(
            "MPIP: provider {ProviderId} ({ProviderType}) not qualified for enhanced rate " +
            "in period {Period}",
            Sanitize(providerId), qualification.ProviderType, period);
        return StandardMultiplier;
    }

    // ── UpsertQualification ──────────────────────────────────────────

    public async Task UpsertQualificationAsync(MpipProviderQualification qualification)
    {
        qualification.UpdatedAt = DateTime.UtcNow;

        // Set multiplier based on qualification status
        qualification.EnhancedRateMultiplier = qualification.IsQualified
            ? EnhancedMultiplier
            : StandardMultiplier;

        var filter = Builders<MpipProviderQualification>.Filter.And(
            Builders<MpipProviderQualification>.Filter.Eq(q => q.ProviderId, qualification.ProviderId),
            Builders<MpipProviderQualification>.Filter.Eq(q => q.TenantId, qualification.TenantId),
            Builders<MpipProviderQualification>.Filter.Eq(q => q.QualificationPeriod, qualification.QualificationPeriod)
        );

        var existing = await _collection.Find(filter).FirstOrDefaultAsync();

        if (existing is not null)
        {
            qualification.Id = existing.Id;
            qualification.CreatedAt = existing.CreatedAt;
            await _collection.ReplaceOneAsync(filter, qualification);

            _logger.LogInformation(
                "Updated MPIP qualification for provider {ProviderId} (period {Period}): " +
                "qualified={Qualified}, method={Method}, multiplier={Multiplier}",
                Sanitize(qualification.ProviderId), Sanitize(qualification.QualificationPeriod),
                qualification.IsQualified, qualification.QualificationMethod,
                qualification.EnhancedRateMultiplier);
        }
        else
        {
            qualification.CreatedAt = DateTime.UtcNow;
            await _collection.InsertOneAsync(qualification);

            _logger.LogInformation(
                "Created MPIP qualification for provider {ProviderId} (period {Period}): " +
                "type={Type}, qualified={Qualified}, method={Method}",
                Sanitize(qualification.ProviderId), Sanitize(qualification.QualificationPeriod),
                qualification.ProviderType, qualification.IsQualified,
                qualification.QualificationMethod);
        }
    }

    // ── GetQualifiedProviders ────────────────────────────────────────

    public async Task<IEnumerable<MpipProviderQualification>> GetQualifiedProvidersAsync(
        string tenantId, string period)
    {
        var filter = Builders<MpipProviderQualification>.Filter.And(
            Builders<MpipProviderQualification>.Filter.Eq(q => q.TenantId, tenantId),
            Builders<MpipProviderQualification>.Filter.Eq(q => q.QualificationPeriod, period),
            Builders<MpipProviderQualification>.Filter.Eq(q => q.IsQualified, true)
        );

        var results = await _collection.Find(filter).ToListAsync();

        _logger.LogInformation(
            "Found {Count} qualified MPIP providers for tenant {TenantId}, period {Period}",
            results.Count, Sanitize(tenantId), Sanitize(period));

        return results;
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Determine the FL fiscal year qualification period from a service date.
    /// FL fiscal year runs Oct 1 – Sep 30.
    /// A service date of Jan 15 2026 → period "2025-2026" (Oct 1 2025 – Sep 30 2026).
    /// A service date of Nov 1 2025 → period "2025-2026" (Oct 1 2025 – Sep 30 2026).
    /// </summary>
    public static string GetFiscalYearPeriod(DateTime serviceDate)
    {
        // If the month is Oct (10) or later, the fiscal year starts in that calendar year
        // If the month is Jan–Sep, the fiscal year started in the prior calendar year
        var fiscalYearStart = serviceDate.Month >= 10
            ? serviceDate.Year
            : serviceDate.Year - 1;

        return $"{fiscalYearStart}-{fiscalYearStart + 1}";
    }
}
