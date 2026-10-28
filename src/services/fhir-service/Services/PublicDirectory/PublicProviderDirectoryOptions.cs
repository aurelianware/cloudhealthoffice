using System.Text.RegularExpressions;

namespace FhirService.Services.PublicDirectory;

/// <summary>
/// The provider directories this deployment publishes without authentication
/// (CMS-9115-F; 42 CFR 422.120, 431.70, 438.242(b)(6), 457.760; D-29), served
/// in the Da Vinci PDex Plan-Net 1.2.0 shape (G-36).
///
/// <code>
///   PublicProviderDirectory:Directories:&lt;directory id&gt;          tenant id
///   PublicProviderDirectory:Payers:&lt;directory id&gt;:Name           the plan's own organization (see PayerOrganization)
///   PublicProviderDirectory:PermitPerMinute                      per client address, default 300
///   PublicProviderDirectory:RefreshMinutes                       snapshot age before a rebuild, default 60
///   PublicProviderDirectory:MaxResources                         snapshot size guard, default 500000
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

    /// <summary>
    /// The plan's own organization, per directory id. Plan-Net requires every
    /// network to name the organization that manages it, and every insurance
    /// product to name its owner and administrator; CHO keeps no such record
    /// for the plan itself, so the directory publishes it from here.
    /// </summary>
    public Dictionary<string, PayerOrganization> Payers { get; set; } = new(StringComparer.Ordinal);

    public int PermitPerMinute { get; set; } = 300;

    /// <summary>
    /// How old a directory snapshot may get before the next request triggers
    /// a rebuild (served stale meanwhile). CMS asks for directory updates
    /// within 30 business days of a change; an hour is far inside that.
    /// </summary>
    public int RefreshMinutes { get; set; } = 60;

    /// <summary>
    /// The most resources one directory snapshot may hold. A build that would
    /// exceed it fails rather than exhaust memory; raise it deliberately.
    /// </summary>
    public int MaxResources { get; set; } = 500_000;

    /// <summary>The tenant whose directory <paramref name="directoryId"/> publishes, or null.</summary>
    public string? ResolveTenant(string? directoryId)
    {
        if (string.IsNullOrEmpty(directoryId) || !DirectoryIdShape.IsMatch(directoryId))
            return null;
        return Directories.TryGetValue(directoryId, out var tenant) && !string.IsNullOrWhiteSpace(tenant)
            ? tenant
            : null;
    }

    /// <summary>The configured payer for <paramref name="directoryId"/>, or a placeholder named for it.</summary>
    public PayerOrganization PayerFor(string directoryId)
        => Payers.TryGetValue(directoryId, out var payer) && !string.IsNullOrWhiteSpace(payer.Name)
            ? payer
            : new PayerOrganization { Name = directoryId };
}

/// <summary>
/// The plan's own Organization in its public directory (plannet-Organization,
/// type <c>payer</c>). Plan-Net requires at least one address, so a directory
/// without one here publishes an Organization that fails validation.
/// </summary>
public sealed class PayerOrganization
{
    /// <summary>The Organization id in the directory. Fixed, so references to it never move.</summary>
    public const string ResourceId = "payer";

    public string Name { get; set; } = string.Empty;

    /// <summary>An identifier for the plan, such as its NAIC company code or CMS contract id.</summary>
    public string? IdentifierSystem { get; set; }

    public string? IdentifierValue { get; set; }

    public string? Phone { get; set; }

    public string? Website { get; set; }

    public string? AddressLine { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    public string? PostalCode { get; set; }
}
