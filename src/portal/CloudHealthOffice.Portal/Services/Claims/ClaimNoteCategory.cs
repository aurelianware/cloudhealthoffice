namespace CloudHealthOffice.Portal.Services;

/// <summary>The note categories claims-service accepts, with their display labels.</summary>
public static class ClaimNoteCategory
{
    public const string Decision = "Decision";
    public const string ProviderContact = "ProviderContact";
    public const string MemberContact = "MemberContact";
    public const string HandOff = "HandOff";
    public const string Internal = "Internal";

    public static readonly IReadOnlyList<(string Value, string Label)> All =
    [
        (Internal, "Internal"),
        (Decision, "Decision"),
        (ProviderContact, "Provider contact"),
        (MemberContact, "Member contact"),
        (HandOff, "Hand-off"),
    ];

    public static string Label(string? category) =>
        All.FirstOrDefault(c => string.Equals(c.Value, category, StringComparison.OrdinalIgnoreCase)).Label
        ?? category ?? Internal;

    /// <summary>The longest note claims-service accepts.</summary>
    public const int MaxLength = 4000;
}
