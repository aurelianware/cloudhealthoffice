using EnrollmentImportService.Models;

namespace EnrollmentImportService.Services;

public interface IEnrollmentImportService
{
    Task<ImportResult> ImportEnrollmentAsync(Enrollment834 enrollment, string tenantId);
}

public class EnrollmentImportService : IEnrollmentImportService
{
    private readonly IEnrollmentRepository _repository;
    private readonly ILogger<EnrollmentImportService> _logger;
    
    public EnrollmentImportService(IEnrollmentRepository repository, ILogger<EnrollmentImportService> logger)
    {
        _repository = repository;
        _logger = logger;
    }
    
    public async Task<ImportResult> ImportEnrollmentAsync(Enrollment834 enrollment, string tenantId)
    {
        var result = new ImportResult
        {
            FileName = enrollment.FileName,
            StartedAt = DateTime.UtcNow
        };
        
        foreach (var memberEnrollment in enrollment.Enrollments)
        {
            try
            {
                await ProcessMemberEnrollmentAsync(memberEnrollment, tenantId, result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing enrollment for subscriber {SubscriberId}", 
                    SanitizeForLog(memberEnrollment.SubscriberId));
                result.Errors.Add($"Subscriber {memberEnrollment.SubscriberId}: {ex.Message}");
                result.FailedCount++;
            }
        }
        
        result.CompletedAt = DateTime.UtcNow;
        _logger.LogInformation(
            "Import completed: {SuccessCount} success, {FailedCount} failed, {SkippedCount} skipped",
            result.SuccessCount, result.FailedCount, result.SkippedCount);
        
        return result;
    }
    
    private async Task ProcessMemberEnrollmentAsync(MemberEnrollment enrollment, string tenantId, ImportResult result)
    {
        // 1. Process Sponsor (employer/group)
        SponsorEntity? sponsor = null;
        if (enrollment.Sponsor != null && !string.IsNullOrEmpty(enrollment.Sponsor.Id))
        {
            sponsor = await GetOrCreateSponsorAsync(enrollment.Sponsor, tenantId);
        }
        
        // 2. Process Member (subscriber)
        Member? member = null;
        if (!string.IsNullOrEmpty(enrollment.SubscriberId))
        {
            member = await _repository.GetMemberBySubscriberIdAsync(enrollment.SubscriberId, tenantId);
        }
        
        // Determine action based on maintenance type
        switch (enrollment.MaintenanceType)
        {
            case "021": // Addition
                if (member != null)
                {
                    _logger.LogWarning("Member {SubscriberId} already exists, skipping addition", 
                        SanitizeForLog(enrollment.SubscriberId));
                    result.SkippedCount++;
                    return;
                }
                member = await CreateMemberFromEnrollmentAsync(enrollment, tenantId, sponsor?.SponsorId);
                result.MembersCreated++;
                result.SuccessCount++;
                break;
                
            case "001": // Change
                if (member == null)
                {
                    _logger.LogWarning("Member {SubscriberId} not found for change, creating new", 
                        SanitizeForLog(enrollment.SubscriberId));
                    member = await CreateMemberFromEnrollmentAsync(enrollment, tenantId, sponsor?.SponsorId);
                    result.MembersCreated++;
                }
                else
                {
                    member = await UpdateMemberFromEnrollmentAsync(member, enrollment, sponsor?.SponsorId);
                    result.MembersUpdated++;
                }
                result.SuccessCount++;
                break;
                
            case "024": // Termination
                if (member == null)
                {
                    _logger.LogWarning("Member {SubscriberId} not found for termination, skipping", 
                        SanitizeForLog(enrollment.SubscriberId));
                    result.SkippedCount++;
                    return;
                }
                member.Status = "Terminated";
                member.TerminationDate = ParseDate(enrollment.TerminationDate);
                await _repository.UpdateMemberAsync(member);
                result.MembersTerminated++;
                result.SuccessCount++;
                break;
                
            default:
                _logger.LogWarning("Unknown maintenance type {MaintenanceType}", SanitizeForLog(enrollment.MaintenanceType));
                result.SkippedCount++;
                return;
        }
        
        // 3. Process Coverage (health plans)
        if (member != null && enrollment.MaintenanceType != "024")
        {
            foreach (var coverageDetail in enrollment.Coverage)
            {
                await ProcessCoverageAsync(member.MemberId, tenantId, coverageDetail, enrollment.EnrollmentDate);
                result.CoverageRecordsCreated++;
            }
        }
        
        // 4. Process Dependents
        foreach (var dependent in enrollment.Dependents)
        {
            await ProcessDependentAsync(dependent, tenantId, member?.MemberId, sponsor?.SponsorId, enrollment.GroupNumber);
            result.DependentsCreated++;
        }
    }
    
    private async Task<SponsorEntity> GetOrCreateSponsorAsync(Sponsor sponsor, string tenantId)
    {
        if (string.IsNullOrEmpty(sponsor.Id))
        {
            throw new ArgumentException("Sponsor ID is required");
        }
        
        var existing = await _repository.GetSponsorByIdAsync(sponsor.Id, tenantId);
        if (existing != null)
        {
            return existing;
        }
        
        var newSponsor = new SponsorEntity
        {
            TenantId = tenantId,
            SponsorId = sponsor.Id,
            Name = sponsor.Name,
            FederalTaxId = sponsor.IdQualifier == "FI" ? sponsor.Id : null,
            Status = "Active"
        };
        
        return await _repository.CreateSponsorAsync(newSponsor);
    }
    
    private async Task<Member> CreateMemberFromEnrollmentAsync(MemberEnrollment enrollment, string tenantId, string? sponsorId)
    {
        var member = new Member
        {
            TenantId = tenantId,
            MemberId = GenerateMemberId(enrollment),
            SubscriberId = enrollment.SubscriberId,
            FirstName = enrollment.Demographics?.FirstName ?? "",
            LastName = enrollment.Demographics?.LastName ?? "",
            MiddleName = enrollment.Demographics?.MiddleName,
            Suffix = enrollment.Demographics?.Suffix,
            DateOfBirth = ParseDate(enrollment.Demographics?.DateOfBirth),
            Gender = enrollment.Demographics?.Gender,
            SSN = enrollment.Demographics?.IdQualifier == "34" ? enrollment.Demographics.Id : null,
            Address = new Address
            {
                Line1 = enrollment.Demographics?.Address1,
                Line2 = enrollment.Demographics?.Address2,
                City = enrollment.Demographics?.City,
                State = enrollment.Demographics?.State,
                Zip = enrollment.Demographics?.Zip
            },
            Status = enrollment.BenefitStatus == "A" ? "Active" : 
                     enrollment.BenefitStatus == "C" ? "COBRA" : "Terminated",
            EnrollmentDate = ParseDate(enrollment.EnrollmentDate),
            TerminationDate = ParseDate(enrollment.TerminationDate),
            SponsorId = sponsorId,
            GroupNumber = enrollment.GroupNumber,
            EmployeeId = enrollment.EmployeeId,
            Relationship = enrollment.Relationship
        };
        
        return await _repository.CreateMemberAsync(member);
    }
    
    private async Task<Member> UpdateMemberFromEnrollmentAsync(Member member, MemberEnrollment enrollment, string? sponsorId)
    {
        // Update demographics if provided
        if (enrollment.Demographics != null)
        {
            member.FirstName = enrollment.Demographics.FirstName ?? member.FirstName;
            member.LastName = enrollment.Demographics.LastName ?? member.LastName;
            member.MiddleName = enrollment.Demographics.MiddleName ?? member.MiddleName;
            member.DateOfBirth = ParseDate(enrollment.Demographics.DateOfBirth) ?? member.DateOfBirth;
            member.Gender = enrollment.Demographics.Gender ?? member.Gender;
            
            if (!string.IsNullOrEmpty(enrollment.Demographics.Address1))
            {
                member.Address = new Address
                {
                    Line1 = enrollment.Demographics.Address1,
                    Line2 = enrollment.Demographics.Address2,
                    City = enrollment.Demographics.City,
                    State = enrollment.Demographics.State,
                    Zip = enrollment.Demographics.Zip
                };
            }
        }
        
        // Update status
        member.Status = enrollment.BenefitStatus == "A" ? "Active" :
                       enrollment.BenefitStatus == "C" ? "COBRA" : "Terminated";
        
        // Update dates
        if (!string.IsNullOrEmpty(enrollment.EnrollmentDate))
        {
            member.EnrollmentDate = ParseDate(enrollment.EnrollmentDate);
        }
        
        if (!string.IsNullOrEmpty(enrollment.TerminationDate))
        {
            member.TerminationDate = ParseDate(enrollment.TerminationDate);
        }
        
        // Update sponsor/group info
        member.SponsorId = sponsorId ?? member.SponsorId;
        member.GroupNumber = enrollment.GroupNumber ?? member.GroupNumber;
        member.EmployeeId = enrollment.EmployeeId ?? member.EmployeeId;
        
        return await _repository.UpdateMemberAsync(member);
    }
    
    private async Task ProcessCoverageAsync(string memberId, string tenantId, CoverageDetail coverageDetail, string? effectiveDate)
    {
        var coverage = new Coverage
        {
            TenantId = tenantId,
            MemberId = memberId,
            PlanId = coverageDetail.PlanCoverageDescription ?? "DEFAULT",
            InsuranceType = coverageDetail.InsuranceLineCode,
            CoverageLevel = coverageDetail.CoverageLevel ?? "EMP",
            EffectiveDate = ParseDate(effectiveDate) ?? DateTime.UtcNow,
            Status = "Active"
        };
        
        await _repository.CreateCoverageAsync(coverage);
    }
    
    private async Task ProcessDependentAsync(Dependent dependent, string tenantId, string? subscriberMemberId, 
        string? sponsorId, string? groupNumber)
    {
        var dependentMember = new Member
        {
            TenantId = tenantId,
            MemberId = $"D-{Guid.NewGuid():N}".Substring(0, 20),
            FirstName = dependent.FirstName,
            LastName = dependent.LastName,
            MiddleName = dependent.MiddleName,
            Suffix = dependent.Suffix,
            DateOfBirth = ParseDate(dependent.DateOfBirth),
            Gender = dependent.Gender,
            SSN = dependent.IdQualifier == "34" ? dependent.Id : null,
            Address = new Address
            {
                Line1 = dependent.Address1,
                Line2 = dependent.Address2,
                City = dependent.City,
                State = dependent.State,
                Zip = dependent.Zip
            },
            Status = "Active",
            SponsorId = sponsorId,
            GroupNumber = groupNumber,
            Relationship = "19" // Dependent
        };
        
        await _repository.CreateMemberAsync(dependentMember);
        
        // Link dependent to subscriber
        if (!string.IsNullOrEmpty(subscriberMemberId))
        {
            var subscriber = await _repository.GetMemberByIdAsync(subscriberMemberId, tenantId);
            if (subscriber != null)
            {
                subscriber.DependentIds.Add(dependentMember.MemberId);
                await _repository.UpdateMemberAsync(subscriber);
            }
        }
        
        // Create coverage for dependent
        if (dependent.Coverage != null)
        {
            foreach (var coverageDetail in dependent.Coverage)
            {
                await ProcessCoverageAsync(dependentMember.MemberId, tenantId, coverageDetail, null);
            }
        }
    }
    
    private string GenerateMemberId(MemberEnrollment enrollment)
    {
        // Use SubscriberId if available, otherwise generate
        if (!string.IsNullOrEmpty(enrollment.SubscriberId))
        {
            return enrollment.SubscriberId;
        }
        
        // Generate from demographics
        var lastName = enrollment.Demographics?.LastName?.Substring(0, Math.Min(3, enrollment.Demographics.LastName.Length)).ToUpper() ?? "UNK";
        var dob = enrollment.Demographics?.DateOfBirth?.Replace("-", "").Substring(2, 6) ?? "000000"; // YYMMDD
        var random = Guid.NewGuid().ToString("N").Substring(0, 4).ToUpper();
        
        return $"M{lastName}{dob}{random}";
    }
    
    private DateTime? ParseDate(string? dateString)
    {
        if (string.IsNullOrEmpty(dateString))
            return null;
        
        if (DateTime.TryParse(dateString, out var date))
            return date;
        
        return null;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

public class ImportResult
{
    public string FileName { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }
    public int MembersCreated { get; set; }
    public int MembersUpdated { get; set; }
    public int MembersTerminated { get; set; }
    public int DependentsCreated { get; set; }
    public int CoverageRecordsCreated { get; set; }
    public List<string> Errors { get; set; } = new();
}
