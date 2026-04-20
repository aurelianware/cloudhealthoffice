using System.ComponentModel.DataAnnotations;

namespace MemberService.Models;

/// <summary>
/// Member communication channel preference (notifications, appointment reminders, etc.).
/// </summary>
public class CommunicationPreference
{
    [Required]
    public CommunicationChannel Channel { get; set; }

    /// <summary>
    /// BCP-47 language tag (e.g. "en-US", "es-MX"). Falls back to <c>Member.PreferredLanguage</c>.
    /// </summary>
    [StringLength(16)]
    public string? Language { get; set; }

    public bool OptedIn { get; set; } = true;

    /// <summary>
    /// Preferred contact window in 24h HH:mm form (inclusive start, exclusive end).
    /// </summary>
    [StringLength(5)]
    public string? WindowStart { get; set; }

    [StringLength(5)]
    public string? WindowEnd { get; set; }
}

public enum CommunicationChannel
{
    Email = 1,
    SMS = 2,
    Mail = 3,
    Phone = 4,
    PortalMessage = 5
}
