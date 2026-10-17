namespace CloudHealthOffice.Portal.Services;

public class AddDependentMember
{
    public string MemberId { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? MiddleName { get; set; }
    public DateTime DateOfBirth { get; set; }
    public string? Gender { get; set; }
    public string? SSN { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public DateTime EffectiveDate { get; set; }
}
