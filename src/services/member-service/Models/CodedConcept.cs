using System.ComponentModel.DataAnnotations;

namespace MemberService.Models;

/// <summary>
/// Minimal FHIR Coding value object: system + code + optional display.
/// Reused for Race, Ethnicity, MaritalStatus, BirthSex, etc.
/// </summary>
public class CodedConcept
{
    [Required]
    [StringLength(256)]
    public string System { get; set; } = string.Empty;

    [Required]
    [StringLength(64)]
    public string Code { get; set; } = string.Empty;

    [StringLength(256)]
    public string? Display { get; set; }
}
