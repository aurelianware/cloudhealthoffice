using System.Text.RegularExpressions;

namespace FhirService.Services.PublicDirectory;

/// <summary>
/// The provider directories this deployment publishes without authentication
/// (CMS-9115-F; 42 CFR 422.120, 431.70, 438.242(b)(6), 457.760; D-29).
///
/// <code>
///   PublicProviderDirectory:Directories:&lt;directory id&gt;   tenant id
///   PublicProviderDirectory:PermitPerMinute               per client address, default 300
/// </code>
///
/// A plan publishes its directory by being listed here, under the id that
/// appears in its public URL (<c>/fhir/r4/directory/&lt;id&gt;/Practitioner</c>).
/// Nothing is published by default: a tenant that is not listed has no public
/// directory, and an unknown id is answered as not found. The id is a public
/// name chosen for the URL, so tenant ids never have to be.
/// </summary>
public sealed class PublicProviderDirectoryOptions
{
    public const string SectionName = "PublicProviderDirectory";

    private static readonly Regex DirectoryIdShape = new("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant);

    public Dictionary<string, string> Directories { get; set; } = new(StringComparer.Ordinal);

    public int PermitPerMinute { get; set; } = 300;

    /// <summary>The tenant whose directory <paramref name="directoryId"/> publishes, or null.</summary>
    public string? ResolveTenant(string? directoryId)
    {
        if (string.IsNullOrEmpty(directoryId) || !DirectoryIdShape.IsMatch(directoryId))
            return null;
        return Directories.TryGetValue(directoryId, out var tenant) && !string.IsNullOrWhiteSpace(tenant)
            ? tenant
            : null;
    }
}
