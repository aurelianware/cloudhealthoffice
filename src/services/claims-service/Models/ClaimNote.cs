using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace ClaimsService.Models;

/// <summary>
/// One note a person wrote on a claim while working it: why they decided what
/// they decided, who they called, what the next person needs to know.
///
/// Append-only: a note is never edited or deleted. A correction is a new note.
/// The author and time come from the authenticated caller and the server
/// clock, never from the request body, so the note history can stand as a
/// record of who said what and when.
/// </summary>
[BsonIgnoreExtraElements]
public class ClaimNote
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string TenantId { get; set; } = string.Empty;

    public string ClaimId { get; set; } = string.Empty;

    public string? ClaimNumber { get; set; }

    /// <summary>One of <see cref="ClaimNoteCategories.All"/>.</summary>
    public string Category { get; set; } = ClaimNoteCategories.Internal;

    public string Text { get; set; } = string.Empty;

    /// <summary>The token subject of the person who wrote the note.</summary>
    public string AuthorId { get; set; } = string.Empty;

    /// <summary>The author's display name when the token carries one.</summary>
    public string? AuthorName { get; set; }

    public DateTime CreatedAt { get; set; }
}

public static class ClaimNoteCategories
{
    /// <summary>Why the claim was paid, denied or pended.</summary>
    public const string Decision = "Decision";
    /// <summary>A call, fax or portal message with the provider's office.</summary>
    public const string ProviderContact = "ProviderContact";
    /// <summary>A conversation with the member.</summary>
    public const string MemberContact = "MemberContact";
    /// <summary>What the next person to pick up the claim needs to know.</summary>
    public const string HandOff = "HandOff";
    public const string Internal = "Internal";

    public static readonly IReadOnlyList<string> All =
        [Decision, ProviderContact, MemberContact, HandOff, Internal];

    /// <summary>The canonical spelling of a category, or null when it isn't one.</summary>
    public static string? Normalize(string? category) =>
        string.IsNullOrWhiteSpace(category)
            ? Internal
            : All.FirstOrDefault(c => string.Equals(c, category.Trim(), StringComparison.OrdinalIgnoreCase));
}

public sealed class AddClaimNoteRequest
{
    public string? Category { get; set; }
    public string? Text { get; set; }
}
