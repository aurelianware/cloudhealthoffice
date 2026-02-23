using Microsoft.EntityFrameworkCore;
using ReferenceDataService.Models;

namespace ReferenceDataService.Repositories;

public interface IReferenceDataRepository
{
    // CPT
    Task<CptCode?> GetCptCodeAsync(string code);
    Task<IEnumerable<CptCode>> SearchCptCodesAsync(string? searchTerm, string? section, int page, int pageSize);
    
    // ICD-10
    Task<Icd10Code?> GetIcd10CodeAsync(string code);
    Task<IEnumerable<Icd10Code>> SearchIcd10CodesAsync(string? searchTerm, string? category, bool? billableOnly, int page, int pageSize);
    
    // HCPCS
    Task<HcpcsCode?> GetHcpcsCodeAsync(string code);
    Task<IEnumerable<HcpcsCode>> SearchHcpcsCodesAsync(string? searchTerm, string? category, int page, int pageSize);
    
    // Modifiers
    Task<Modifier?> GetModifierAsync(string code);
    Task<IEnumerable<Modifier>> GetModifiersAsync();
    
    // DRG
    Task<DrgCode?> GetDrgCodeAsync(string code);
    Task<IEnumerable<DrgCode>> SearchDrgCodesAsync(string? searchTerm, string? mdc, int? fiscalYear, int page, int pageSize);
    
    // Place of Service
    Task<PlaceOfService?> GetPlaceOfServiceAsync(string code);
    Task<IEnumerable<PlaceOfService>> GetPlacesOfServiceAsync();
    
    // Revenue Codes
    Task<RevenueCode?> GetRevenueCodeAsync(string code);
    Task<IEnumerable<RevenueCode>> SearchRevenueCodesAsync(string? searchTerm, string? category, int page, int pageSize);
    
    // Stats
    Task<ReferenceDataStats> GetStatsAsync();
}

public class ReferenceDataRepository : IReferenceDataRepository
{
    private readonly ReferenceDataContext _context;
    private readonly ILogger<ReferenceDataRepository> _logger;

    public ReferenceDataRepository(
        ReferenceDataContext context,
        ILogger<ReferenceDataRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    // CPT
    public async Task<CptCode?> GetCptCodeAsync(string code)
    {
        return await _context.CptCodes.FindAsync(code);
    }

    public async Task<IEnumerable<CptCode>> SearchCptCodesAsync(string? searchTerm, string? section, int page, int pageSize)
    {
        var query = _context.CptCodes.AsQueryable();

        if (!string.IsNullOrEmpty(searchTerm))
        {
            query = query.Where(c => 
                EF.Functions.Like(c.Code, $"%{searchTerm}%") ||
                EF.Functions.ILike(c.ShortDescription, $"%{searchTerm}%") ||
                EF.Functions.ILike(c.LongDescription ?? "", $"%{searchTerm}%"));
        }

        if (!string.IsNullOrEmpty(section))
        {
            query = query.Where(c => c.Section == section);
        }

        query = query.Where(c => c.StatusCode == "A");

        return await query
            .OrderBy(c => c.Code)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    // ICD-10
    public async Task<Icd10Code?> GetIcd10CodeAsync(string code)
    {
        return await _context.Icd10Codes.FindAsync(code);
    }

    public async Task<IEnumerable<Icd10Code>> SearchIcd10CodesAsync(
        string? searchTerm, string? category, bool? billableOnly, int page, int pageSize)
    {
        var query = _context.Icd10Codes.AsQueryable();

        if (!string.IsNullOrEmpty(searchTerm))
        {
            query = query.Where(c => 
                EF.Functions.Like(c.Code, $"%{searchTerm}%") ||
                EF.Functions.ILike(c.ShortDescription, $"%{searchTerm}%") ||
                EF.Functions.ILike(c.LongDescription ?? "", $"%{searchTerm}%"));
        }

        if (!string.IsNullOrEmpty(category))
        {
            query = query.Where(c => c.CategoryChapter == category);
        }

        if (billableOnly.HasValue && billableOnly.Value)
        {
            query = query.Where(c => c.Billable);
        }

        query = query.Where(c => c.StatusCode == "A");

        return await query
            .OrderBy(c => c.Code)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    // HCPCS
    public async Task<HcpcsCode?> GetHcpcsCodeAsync(string code)
    {
        return await _context.HcpcsCodes.FindAsync(code);
    }

    public async Task<IEnumerable<HcpcsCode>> SearchHcpcsCodesAsync(
        string? searchTerm, string? category, int page, int pageSize)
    {
        var query = _context.HcpcsCodes.AsQueryable();

        if (!string.IsNullOrEmpty(searchTerm))
        {
            query = query.Where(c => 
                EF.Functions.Like(c.Code, $"%{searchTerm}%") ||
                EF.Functions.ILike(c.ShortDescription, $"%{searchTerm}%") ||
                EF.Functions.ILike(c.LongDescription ?? "", $"%{searchTerm}%"));
        }

        if (!string.IsNullOrEmpty(category))
        {
            query = query.Where(c => c.Category == category);
        }

        query = query.Where(c => c.StatusCode == "A");

        return await query
            .OrderBy(c => c.Code)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    // Modifiers
    public async Task<Modifier?> GetModifierAsync(string code)
    {
        return await _context.Modifiers.FindAsync(code);
    }

    public async Task<IEnumerable<Modifier>> GetModifiersAsync()
    {
        return await _context.Modifiers
            .Where(m => m.Status == "A")
            .OrderBy(m => m.Code)
            .ToListAsync();
    }

    // DRG
    public async Task<DrgCode?> GetDrgCodeAsync(string code)
    {
        return await _context.DrgCodes.FindAsync(code);
    }

    public async Task<IEnumerable<DrgCode>> SearchDrgCodesAsync(
        string? searchTerm, string? mdc, int? fiscalYear, int page, int pageSize)
    {
        var query = _context.DrgCodes.AsQueryable();

        if (!string.IsNullOrEmpty(searchTerm))
        {
            query = query.Where(c => 
                EF.Functions.Like(c.Code, $"%{searchTerm}%") ||
                EF.Functions.ILike(c.Description, $"%{searchTerm}%"));
        }

        if (!string.IsNullOrEmpty(mdc))
        {
            query = query.Where(c => c.MDC == mdc);
        }

        if (fiscalYear.HasValue)
        {
            query = query.Where(c => c.FiscalYear == fiscalYear.Value);
        }

        query = query.Where(c => c.Status == "A");

        return await query
            .OrderBy(c => c.Code)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    // Place of Service
    public async Task<PlaceOfService?> GetPlaceOfServiceAsync(string code)
    {
        return await _context.PlacesOfService.FindAsync(code);
    }

    public async Task<IEnumerable<PlaceOfService>> GetPlacesOfServiceAsync()
    {
        return await _context.PlacesOfService
            .Where(p => p.Status == "A")
            .OrderBy(p => p.Code)
            .ToListAsync();
    }

    // Revenue Codes
    public async Task<RevenueCode?> GetRevenueCodeAsync(string code)
    {
        return await _context.RevenueCodes.FindAsync(code);
    }

    public async Task<IEnumerable<RevenueCode>> SearchRevenueCodesAsync(
        string? searchTerm, string? category, int page, int pageSize)
    {
        var query = _context.RevenueCodes.AsQueryable();

        if (!string.IsNullOrEmpty(searchTerm))
        {
            query = query.Where(c => 
                EF.Functions.Like(c.Code, $"%{searchTerm}%") ||
                EF.Functions.ILike(c.Description, $"%{searchTerm}%"));
        }

        if (!string.IsNullOrEmpty(category))
        {
            query = query.Where(c => c.Category == category);
        }

        query = query.Where(c => c.Status == "A");

        return await query
            .OrderBy(c => c.Code)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    // Stats
    public async Task<ReferenceDataStats> GetStatsAsync()
    {
        return new ReferenceDataStats
        {
            TotalCptCodes = await _context.CptCodes.CountAsync(),
            ActiveCptCodes = await _context.CptCodes.CountAsync(c => c.StatusCode == "A"),
            TotalIcd10Codes = await _context.Icd10Codes.CountAsync(),
            BillableIcd10Codes = await _context.Icd10Codes.CountAsync(c => c.Billable && c.StatusCode == "A"),
            TotalHcpcsCodes = await _context.HcpcsCodes.CountAsync(),
            TotalModifiers = await _context.Modifiers.CountAsync(),
            TotalDrgCodes = await _context.DrgCodes.CountAsync(),
            TotalPlacesOfService = await _context.PlacesOfService.CountAsync(),
            TotalRevenueCodes = await _context.RevenueCodes.CountAsync(),
            LastUpdated = DateTime.UtcNow
        };
    }
}

/// <summary>
/// EF Core DbContext for reference data
/// </summary>
public class ReferenceDataContext : DbContext
{
    public ReferenceDataContext(DbContextOptions<ReferenceDataContext> options)
        : base(options)
    {
    }

    public DbSet<CptCode> CptCodes { get; set; } = null!;
    public DbSet<Icd10Code> Icd10Codes { get; set; } = null!;
    public DbSet<HcpcsCode> HcpcsCodes { get; set; } = null!;
    public DbSet<Modifier> Modifiers { get; set; } = null!;
    public DbSet<DrgCode> DrgCodes { get; set; } = null!;
    public DbSet<PlaceOfService> PlacesOfService { get; set; } = null!;
    public DbSet<RevenueCode> RevenueCodes { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Create indexes for frequently searched fields
        modelBuilder.Entity<CptCode>()
            .HasIndex(c => c.StatusCode);
        
        modelBuilder.Entity<CptCode>()
            .HasIndex(c => c.Section);

        modelBuilder.Entity<Icd10Code>()
            .HasIndex(c => c.StatusCode);
        
        modelBuilder.Entity<Icd10Code>()
            .HasIndex(c => c.Billable);
        
        modelBuilder.Entity<Icd10Code>()
            .HasIndex(c => c.CategoryChapter);

        modelBuilder.Entity<HcpcsCode>()
            .HasIndex(c => c.StatusCode);
        
        modelBuilder.Entity<HcpcsCode>()
            .HasIndex(c => c.Category);

        modelBuilder.Entity<DrgCode>()
            .HasIndex(c => c.MDC);
        
        modelBuilder.Entity<DrgCode>()
            .HasIndex(c => c.FiscalYear);
    }
}
