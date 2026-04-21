using Microsoft.AspNetCore.Mvc;
using SponsorService.Middleware;
using SponsorService.Models;
using SponsorService.Repositories;
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

    private readonly ISponsorRepository _sponsorRepository;
    private readonly ILogger<SponsorsController> _logger;

    public SponsorsController(
        ISponsorRepository sponsorRepository,
        ILogger<SponsorsController> logger)
    {
        _sponsorRepository = sponsorRepository;
        _logger = logger;
    }

    /// <summary>
    /// List all sponsors for the current tenant
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(SponsorListResponse), 200)]
    public async Task<IActionResult> GetSponsors(
        [FromQuery] SponsorStatus? status = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] bool activeOnly = false,
        [FromQuery][Range(1, 100)] int pageSize = 20,
        [FromQuery] string? continuationToken = null)
    {
        // lineOfBusiness is pushed into the repo query (not filtered in-memory
        // after paging) so TotalCount and ContinuationToken reflect the
        // filtered set and pagination produces stable, correctly-sized pages.
        var (items, token, total) = await _sponsorRepository.GetPagedAsync(
            TenantId, status, activeOnly, lineOfBusiness, pageSize, continuationToken);

        return Ok(new SponsorListResponse
        {
            Sponsors = items.ToList(),
            ContinuationToken = token,
            TotalCount = total
        });
    }

    /// <summary>
    /// Get sponsor details by group number
    /// </summary>
    [HttpGet("{groupNumber}")]
    [ProducesResponseType(typeof(Sponsor), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetSponsor([FromRoute] string groupNumber)
    {
        var sponsor = await _sponsorRepository.GetByGroupNumberAsync(TenantId, groupNumber);
        if (sponsor == null)
            return NotFound(new { error = $"Sponsor with group number '{groupNumber}' not found" });
        return Ok(sponsor);
    }

    /// <summary>
    /// Compact sponsor view for the portal Member Details dialog — Coverage
    /// tab's Sponsor sub-section. Returns sponsor name, type, primary contact,
    /// broker, and open-enrollment window.
    /// </summary>
    [HttpGet("{groupNumber}/member-view")]
    [ProducesResponseType(typeof(SponsorMemberView), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetMemberView([FromRoute] string groupNumber)
    {
        var sponsor = await _sponsorRepository.GetByGroupNumberAsync(TenantId, groupNumber);
        if (sponsor == null)
            return NotFound(new { error = $"Sponsor with group number '{groupNumber}' not found" });

        return Ok(ProjectMemberView(sponsor, DateTime.UtcNow));
    }

    /// <summary>
    /// Build the compact member view. Public + static so unit tests can
    /// project without hitting Cosmos/Mongo.
    /// </summary>
    public static SponsorMemberView ProjectMemberView(Sponsor sponsor, DateTime asOfUtc) => new()
    {
        GroupNumber = sponsor.GroupNumber,
        SponsorName = sponsor.EmployerName,
        LineOfBusiness = sponsor.LineOfBusiness,
        Status = sponsor.Status,
        PrimaryContact = (sponsor.ContactName is null && sponsor.ContactPhone is null && sponsor.ContactEmail is null)
            ? null
            : new ContactCard
            {
                Name = sponsor.ContactName,
                Phone = sponsor.ContactPhone,
                Email = sponsor.ContactEmail
            },
        Broker = sponsor.Broker is null ? null : new BrokerCard
        {
            AgencyName = sponsor.Broker.AgencyName,
            Name = sponsor.Broker.Name,
            Phone = sponsor.Broker.Phone,
            Email = sponsor.Broker.Email,
            Npn = sponsor.Broker.Npn
        },
        OpenEnrollment = sponsor.OpenEnrollment is null ? null : new OpenEnrollmentCard
        {
            Start = sponsor.OpenEnrollment.Start,
            End = sponsor.OpenEnrollment.End,
            Status = sponsor.OpenEnrollment.Status(asOfUtc).ToString()
        }
    };

    /// <summary>
    /// Create a new sponsor (typically from 834 transaction)
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(Sponsor), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> CreateSponsor([FromBody] CreateSponsorRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (await _sponsorRepository.ExistsAsync(TenantId, request.GroupNumber))
            return Conflict(new { error = $"Sponsor with group number '{request.GroupNumber}' already exists" });

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
            Broker = request.Broker,
            OpenEnrollment = request.OpenEnrollment,
            CreatedBy = User.Identity?.Name ?? "System"
        };

        var created = await _sponsorRepository.CreateAsync(sponsor);
        _logger.LogInformation("Created sponsor {GroupNumber} ({EmployerName})",
            SanitizeForLog(created.GroupNumber), SanitizeForLog(created.EmployerName));
        return CreatedAtAction(nameof(GetSponsor), new { groupNumber = created.GroupNumber }, created);
    }

    /// <summary>
    /// Update sponsor information
    /// </summary>
    [HttpPut("{groupNumber}")]
    [ProducesResponseType(typeof(Sponsor), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> UpdateSponsor(
        [FromRoute] string groupNumber,
        [FromBody] UpdateSponsorRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var sponsor = await _sponsorRepository.GetByGroupNumberAsync(TenantId, groupNumber);
        if (sponsor == null)
            return NotFound(new { error = $"Sponsor with group number '{groupNumber}' not found" });

        if (request.EmployerName != null) sponsor.EmployerName = request.EmployerName;
        if (request.ContactName != null) sponsor.ContactName = request.ContactName;
        if (request.ContactPhone != null) sponsor.ContactPhone = request.ContactPhone;
        if (request.ContactEmail != null) sponsor.ContactEmail = request.ContactEmail;
        if (request.Status.HasValue) sponsor.Status = request.Status.Value;
        if (request.BillingInfo != null) sponsor.BillingInfo = request.BillingInfo;
        if (request.Broker != null) sponsor.Broker = request.Broker;
        if (request.OpenEnrollment != null) sponsor.OpenEnrollment = request.OpenEnrollment;

        sponsor.LastUpdatedBy = User.Identity?.Name ?? "System";
        var updated = await _sponsorRepository.UpdateAsync(sponsor);
        _logger.LogInformation("Updated sponsor {GroupNumber}", SanitizeForLog(updated.GroupNumber));
        return Ok(updated);
    }

    /// <summary>
    /// Terminate sponsor (soft delete)
    /// </summary>
    [HttpDelete("{groupNumber}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> TerminateSponsor(
        [FromRoute] string groupNumber,
        [FromQuery] DateTime? terminationDate = null)
    {
        var sponsor = await _sponsorRepository.GetByGroupNumberAsync(TenantId, groupNumber);
        if (sponsor == null)
            return NotFound(new { error = $"Sponsor with group number '{groupNumber}' not found" });

        sponsor.Status = SponsorStatus.Terminated;
        sponsor.TerminationDate = terminationDate ?? DateTime.UtcNow;
        await _sponsorRepository.UpdateAsync(sponsor);
        _logger.LogInformation("Terminated sponsor {GroupNumber} (effective {TerminationDate:O})",
            SanitizeForLog(groupNumber), sponsor.TerminationDate);
        return NoContent();
    }

    /// <summary>
    /// Get coverage summary for a sponsor (member counts, premium totals).
    /// Member counts are denormalized onto the Sponsor document by the 834
    /// ingestion pipeline; premium total comes from BillingInfo.
    /// </summary>
    [HttpGet("{groupNumber}/coverage-summary")]
    [ProducesResponseType(typeof(CoverageSummary), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetCoverageSummary([FromRoute] string groupNumber)
    {
        var sponsor = await _sponsorRepository.GetByGroupNumberAsync(TenantId, groupNumber);
        if (sponsor == null)
            return NotFound(new { error = $"Sponsor with group number '{groupNumber}' not found" });

        var summary = new CoverageSummary
        {
            GroupNumber = sponsor.GroupNumber,
            TotalMembers = sponsor.TotalMembers,
            TotalDependents = sponsor.TotalDependents,
            TotalCovered = sponsor.TotalMembers + sponsor.TotalDependents,
            ActiveSubscribers = sponsor.Status == SponsorStatus.Active ? sponsor.TotalMembers : 0,
            TerminatedMembers = 0,
            MonthlyPremium = sponsor.BillingInfo?.PremiumAmount ?? 0m
        };
        return Ok(summary);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
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
    public BrokerInfo? Broker { get; set; }
    public OpenEnrollmentWindow? OpenEnrollment { get; set; }
}

public class UpdateSponsorRequest
{
    public string? EmployerName { get; set; }
    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }
    public string? ContactEmail { get; set; }
    public SponsorStatus? Status { get; set; }
    public BillingInfo? BillingInfo { get; set; }
    public BrokerInfo? Broker { get; set; }
    public OpenEnrollmentWindow? OpenEnrollment { get; set; }
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
