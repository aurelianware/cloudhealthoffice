using Microsoft.AspNetCore.Mvc;
using SponsorService.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace SponsorService.Controllers;

/// <summary>
/// Sponsor management API - manages employer groups purchasing health coverage.
/// Data populated by X12 834 Enrollment transactions.
/// </summary>
[ApiController]
[Route("api/v1/sponsors")]
public class SponsorsController : ControllerBase
{
    // Tenant context from middleware
    private string TenantId => HttpContext.GetTenantId();

    // TODO: Replace with actual repository/service injection
    // private readonly ISponsorRepository _sponsorRepository;
    // public SponsorsController(ISponsorRepository sponsorRepository)
    // {
    //     _sponsorRepository = sponsorRepository;
    // }

    /// <summary>
    /// List all sponsors for the current tenant
    /// </summary>
    /// <param name="status">Filter by sponsor status</param>
    /// <param name="activeOnly">Return only active sponsors</param>
    /// <param name="pageSize">Page size (max 100)</param>
    /// <param name="continuationToken">Continuation token for pagination</param>
    [HttpGet]
    [ProducesResponseType(typeof(SponsorListResponse), 200)]
    public async Task<IActionResult> GetSponsors(
        [FromQuery] SponsorStatus? status = null,
        [FromQuery] bool activeOnly = false,
        [FromQuery][Range(1, 100)] int pageSize = 20,
        [FromQuery] string? continuationToken = null)
    {
        // TODO: Implement with Cosmos DB query
        // var query = _sponsorRepository.Query()
        //     .Where(s => s.TenantId == TenantId);
        //
        // if (activeOnly)
        //     query = query.Where(s => s.Status == SponsorStatus.Active);
        //
        // if (status.HasValue)
        //     query = query.Where(s => s.Status == status.Value);
        //
        // var result = await _sponsorRepository.GetPagedAsync(query, pageSize, continuationToken);

        // Mock response for now
        var mockSponsors = new List<Sponsor>
        {
            new Sponsor
            {
                TenantId = TenantId,
                Id = "sponsor-001",
                GroupNumber = "GRP-12345",
                EmployerName = "Acme Corporation",
                TaxId = "12-3456789",
                EffectiveDate = DateTime.UtcNow.AddMonths(-6),
                Status = SponsorStatus.Active,
                TotalMembers = 150,
                TotalDependents = 220
            }
        };

        return Ok(new SponsorListResponse
        {
            Sponsors = mockSponsors,
            ContinuationToken = null,
            TotalCount = 1
        });
    }

    /// <summary>
    /// Get sponsor details by group number
    /// </summary>
    /// <param name="groupNumber">Sponsor group number (834 REF*1L)</param>
    [HttpGet("{groupNumber}")]
    [ProducesResponseType(typeof(Sponsor), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetSponsor([FromRoute] string groupNumber)
    {
        // TODO: Implement with Cosmos DB query
        // var sponsor = await _sponsorRepository.GetByGroupNumberAsync(TenantId, groupNumber);
        // if (sponsor == null)
        //     return NotFound($"Sponsor with group number '{groupNumber}' not found");

        // Mock response
        var sponsor = new Sponsor
        {
            TenantId = TenantId,
            Id = "sponsor-001",
            GroupNumber = groupNumber,
            EmployerName = "Acme Corporation",
            TaxId = "12-3456789",
            Address = "123 Main St",
            City = "Dallas",
            State = "TX",
            ZipCode = "75201",
            ContactName = "Jane Smith",
            ContactPhone = "214-555-0100",
            ContactEmail = "benefits@acme.com",
            EffectiveDate = DateTime.UtcNow.AddMonths(-6),
            Status = SponsorStatus.Active,
            TotalMembers = 150,
            TotalDependents = 220,
            BillingInfo = new BillingInfo
            {
                PremiumAmount = 75000.00m,
                Frequency = BillingFrequency.Monthly,
                BillingDay = 1,
                BillingAccountNumber = "ACH-98765",
                PaymentMethod = "ACH",
                GracePeriodDays = 30
            }
        };

        return Ok(sponsor);
    }

    /// <summary>
    /// Create a new sponsor (typically from 834 transaction)
    /// </summary>
    /// <param name="request">Sponsor creation request</param>
    [HttpPost]
    [ProducesResponseType(typeof(Sponsor), 201)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> CreateSponsor([FromBody] CreateSponsorRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // TODO: Check for duplicate group number
        // var exists = await _sponsorRepository.ExistsByGroupNumberAsync(TenantId, request.GroupNumber);
        // if (exists)
        //     return Conflict($"Sponsor with group number '{request.GroupNumber}' already exists");

        var sponsor = new Sponsor
        {
            TenantId = TenantId,
            GroupNumber = request.GroupNumber,
            EmployerName = request.EmployerName,
            TaxId = request.TaxId,
            Address = request.Address,
            City = request.City,
            State = request.State,
            ZipCode = request.ZipCode,
            ContactName = request.ContactName,
            ContactPhone = request.ContactPhone,
            ContactEmail = request.ContactEmail,
            EffectiveDate = request.EffectiveDate,
            TerminationDate = request.TerminationDate,
            Status = SponsorStatus.PendingActivation,
            BillingInfo = request.BillingInfo,
            CreatedDate = DateTime.UtcNow,
            LastUpdatedDate = DateTime.UtcNow,
            CreatedBy = User.Identity?.Name ?? "System"
        };

        // TODO: Save to Cosmos DB
        // await _sponsorRepository.CreateAsync(sponsor);

        return CreatedAtAction(nameof(GetSponsor), new { groupNumber = sponsor.GroupNumber }, sponsor);
    }

    /// <summary>
    /// Update sponsor information
    /// </summary>
    /// <param name="groupNumber">Sponsor group number</param>
    /// <param name="request">Update request</param>
    [HttpPut("{groupNumber}")]
    [ProducesResponseType(typeof(Sponsor), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> UpdateSponsor(
        [FromRoute] string groupNumber,
        [FromBody] UpdateSponsorRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // TODO: Fetch existing sponsor
        // var sponsor = await _sponsorRepository.GetByGroupNumberAsync(TenantId, groupNumber);
        // if (sponsor == null)
        //     return NotFound();

        // Mock existing sponsor
        var sponsor = new Sponsor { TenantId = TenantId, GroupNumber = groupNumber };

        // Update fields
        if (request.EmployerName != null) sponsor.EmployerName = request.EmployerName;
        if (request.ContactName != null) sponsor.ContactName = request.ContactName;
        if (request.ContactPhone != null) sponsor.ContactPhone = request.ContactPhone;
        if (request.ContactEmail != null) sponsor.ContactEmail = request.ContactEmail;
        if (request.Status.HasValue) sponsor.Status = request.Status.Value;
        if (request.BillingInfo != null) sponsor.BillingInfo = request.BillingInfo;

        sponsor.LastUpdatedDate = DateTime.UtcNow;
        sponsor.LastUpdatedBy = User.Identity?.Name ?? "System";

        // TODO: Save to Cosmos DB
        // await _sponsorRepository.UpdateAsync(sponsor);

        return Ok(sponsor);
    }

    /// <summary>
    /// Terminate sponsor (soft delete)
    /// </summary>
    /// <param name="groupNumber">Sponsor group number</param>
    /// <param name="terminationDate">Termination effective date</param>
    [HttpDelete("{groupNumber}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> TerminateSponsor(
        [FromRoute] string groupNumber,
        [FromQuery] DateTime? terminationDate = null)
    {
        // TODO: Fetch sponsor and update status
        // var sponsor = await _sponsorRepository.GetByGroupNumberAsync(TenantId, groupNumber);
        // if (sponsor == null)
        //     return NotFound();
        //
        // sponsor.Status = SponsorStatus.Terminated;
        // sponsor.TerminationDate = terminationDate ?? DateTime.UtcNow;
        // sponsor.LastUpdatedDate = DateTime.UtcNow;
        // await _sponsorRepository.UpdateAsync(sponsor);

        return NoContent();
    }

    /// <summary>
    /// Get coverage summary for a sponsor (member counts, premium totals)
    /// </summary>
    /// <param name="groupNumber">Sponsor group number</param>
    [HttpGet("{groupNumber}/coverage-summary")]
    [ProducesResponseType(typeof(CoverageSummary), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetCoverageSummary([FromRoute] string groupNumber)
    {
        // TODO: Query Coverage Service API for member counts
        // var summary = await _coverageService.GetSponsorSummaryAsync(TenantId, groupNumber);

        var summary = new CoverageSummary
        {
            GroupNumber = groupNumber,
            TotalMembers = 150,
            TotalDependents = 220,
            TotalCovered = 370,
            ActiveSubscribers = 150,
            TerminatedMembers = 12,
            MonthlyPremium = 75000.00m
        };

        return Ok(summary);
    }
}

#region Request/Response Models

public class CreateSponsorRequest
{
    [Required]
    [StringLength(50)]
    public string GroupNumber { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string EmployerName { get; set; } = string.Empty;

    [StringLength(20)]
    public string? TaxId { get; set; }

    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }
    public string? ContactEmail { get; set; }

    [Required]
    public DateTime EffectiveDate { get; set; }

    public DateTime? TerminationDate { get; set; }
    public BillingInfo? BillingInfo { get; set; }
}

public class UpdateSponsorRequest
{
    public string? EmployerName { get; set; }
    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }
    public string? ContactEmail { get; set; }
    public SponsorStatus? Status { get; set; }
    public BillingInfo? BillingInfo { get; set; }
}

public class SponsorListResponse
{
    public List<Sponsor> Sponsors { get; set; } = new();
    public string? ContinuationToken { get; set; }
    public int TotalCount { get; set; }
}

public class CoverageSummary
{
    public string GroupNumber { get; set; } = string.Empty;
    public int TotalMembers { get; set; }
    public int TotalDependents { get; set; }
    public int TotalCovered { get; set; }
    public int ActiveSubscribers { get; set; }
    public int TerminatedMembers { get; set; }
    public decimal MonthlyPremium { get; set; }
}

#endregion
