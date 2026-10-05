using System.Text.RegularExpressions;
using BenefitPlanService.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BenefitPlanService.Services;

/// <summary>
/// Configuration for which hosts a plan document <c>location</c> may point
/// at. Bound from the <c>BenefitPlan</c> configuration section, e.g.
/// <c>BenefitPlan:AllowedDocumentHosts:0 = docs.example-payer.com</c>.
///
/// Defaults to empty: with no hosts configured only the internal
/// <c>documentreference/{id}</c> form is accepted. An entry of the form
/// <c>*.example.com</c> allows any subdomain of <c>example.com</c> (not the
/// apex itself).
/// </summary>
public sealed class PlanDocumentLocationOptions
{
    public const string SectionName = "BenefitPlan";

    public List<string> AllowedDocumentHosts { get; set; } = new();
}

/// <summary>
/// The rule for plan document locations. A location is either the internal
/// reference <c>documentreference/{id}</c> (resolved by the portal /
/// member-document-service), or an <c>https</c> URL whose host is on
/// <see cref="PlanDocumentLocationOptions.AllowedDocumentHosts"/>.
///
/// Everything else is refused: other schemes (<c>javascript:</c>,
/// <c>data:</c>, <c>file:</c>, <c>http:</c>, ...), URLs carrying userinfo,
/// IP-literal hosts, non-default ports, and internal / cluster host names
/// (<c>localhost</c>, <c>*.svc</c>, <c>*.cluster.local</c>,
/// <c>*.cloudhealthoffice</c>, <c>*.internal</c>, <c>*.local</c>,
/// single-label names) even when an operator lists them.
///
/// Applied on write (400 via <see cref="ValidateDocuments"/>) and on read
/// (<see cref="SanitizeForRead(BenefitPlan)"/> nulls a stored location that
/// fails the rule, so data written before the rule existed is not served as
/// a link).
/// </summary>
public sealed class PlanDocumentLocationPolicy
{
    private static readonly Regex InternalReferenceId =
        new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);

    private static readonly string[] InternalHostSuffixes =
    {
        ".localhost",
        ".svc",
        ".cluster.local",
        ".local",
        ".localdomain",
        ".internal",
        ".intranet",
        ".lan",
        ".home.arpa",
        ".cloudhealthoffice",
    };

    private readonly string[] _exactHosts;
    private readonly string[] _wildcardSuffixes;
    private readonly ILogger _logger;

    /// <summary>A policy with no allowed hosts (only internal references pass).</summary>
    public static PlanDocumentLocationPolicy Empty { get; } = new(Array.Empty<string>());

    public PlanDocumentLocationPolicy(
        IOptions<PlanDocumentLocationOptions> options,
        ILogger<PlanDocumentLocationPolicy> logger)
        : this(options.Value.AllowedDocumentHosts, logger)
    {
    }

    public PlanDocumentLocationPolicy(IEnumerable<string>? allowedHosts, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;

        var entries = (allowedHosts ?? Array.Empty<string>())
            .SelectMany(h => (h ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(h => h.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(h => h.Length > 0)
            .ToList();

        _exactHosts = entries.Where(h => !h.StartsWith("*.", StringComparison.Ordinal)).Distinct().ToArray();
        _wildcardSuffixes = entries
            .Where(h => h.StartsWith("*.", StringComparison.Ordinal) && h.Length > 2)
            .Select(h => h[1..]) // ".example.com"
            .Distinct()
            .ToArray();
    }

    /// <summary>True when <paramref name="location"/> satisfies the rule.</summary>
    public bool IsAllowed(string? location) => Check(location) is null;

    /// <summary>
    /// Returns null when the location is allowed, otherwise a short reason
    /// it was refused (safe to log; never echoes the location itself).
    /// </summary>
    public string? Check(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return "location is empty";

        foreach (var c in location)
        {
            if (c <= ' ' || c == '\u007f' || c == '\\')
                return "location contains whitespace, control characters or backslashes";
        }

        var prefix = PlanDocumentValidation.InternalReferencePrefix;
        if (location.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = location[prefix.Length..];
            return InternalReferenceId.IsMatch(id)
                ? null
                : $"internal reference must be '{prefix}{{id}}' with an id of letters, digits, '.', '_' or '-'";
        }

        if (!location.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(location, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return $"location must be an HTTPS URL on an allowed host or '{prefix}{{id}}'";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo)) return "location must not carry user information";
        if (uri.HostNameType != UriHostNameType.Dns) return "location host must be a DNS name, not an IP address";
        if (!uri.IsDefaultPort) return "location must use the default https port";

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (IsInternalHost(host)) return "location host is an internal or cluster host";

        if (_exactHosts.Contains(host, StringComparer.Ordinal)) return null;
        foreach (var suffix in _wildcardSuffixes)
        {
            if (host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.Ordinal)) return null;
        }

        return "location host is not on BenefitPlan:AllowedDocumentHosts";
    }

    private static bool IsInternalHost(string host)
    {
        if (host.Length == 0) return true;
        if (!host.Contains('.')) return true; // single-label: localhost, service names
        if (host == "localhost" || host == "cloudhealthoffice") return true;

        // A last label that is all digits is an IPv4 literal in disguise
        // (e.g. a trailing-dot or partially numeric form).
        var lastLabel = host[(host.LastIndexOf('.') + 1)..];
        if (lastLabel.Length > 0 && lastLabel.All(char.IsAsciiDigit)) return true;

        foreach (var suffix in InternalHostSuffixes)
        {
            if (host.EndsWith(suffix, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> (ParamName = <paramref name="fieldName"/>)
    /// when <paramref name="location"/> fails the rule.
    /// </summary>
    public void ValidateLocation(string? location, string fieldName)
    {
        var reason = Check(location);
        if (reason is not null)
        {
            throw new ArgumentException($"{fieldName}: {reason}.", fieldName);
        }
    }

    /// <summary>
    /// Producer-boundary validation of every document on a plan: location
    /// rule plus hash shape. Controllers map the exception to 400.
    /// </summary>
    public void ValidateDocuments(IEnumerable<PlanDocumentReference>? documents)
    {
        if (documents == null) return;

        var index = 0;
        foreach (var doc in documents)
        {
            ValidateLocation(doc.Location, $"documents[{index}].location");
            PlanDocumentValidation.ValidateHash(doc.ContentHashSha256, $"documents[{index}].contentHashSha256");
            index++;
        }
    }

    // ── read side ────────────────────────────────────────────────────────

    /// <summary>
    /// Replace any stored document whose location fails the rule with a
    /// copy whose <c>location</c> is null and <c>locationBlocked</c> is true.
    /// Documents are copied, never mutated in place.
    /// </summary>
    public BenefitPlan SanitizeForRead(BenefitPlan plan)
    {
        if (plan?.Documents is null || plan.Documents.Count == 0) return plan!;
        if (plan.Documents.All(d => d is null || IsAllowed(d.Location))) return plan;

        plan.Documents = plan.Documents.Select(d =>
        {
            if (d is null) return d!;
            var reason = Check(d.Location);
            if (reason is null) return d;
            LogBlocked(plan.TenantId, plan.PlanId, d.Id, reason);
            return new PlanDocumentReference
            {
                Id = d.Id,
                DocType = d.DocType,
                Location = null,
                LocationBlocked = true,
                ContentType = d.ContentType,
                Size = d.Size,
                ContentHashSha256 = d.ContentHashSha256,
                Version = d.Version,
                EffectiveDate = d.EffectiveDate,
                DisplayName = d.DisplayName,
            };
        }).ToList();
        return plan;
    }

    public MemberBenefitView SanitizeForRead(MemberBenefitView view)
    {
        if (view?.Documents is null || view.Documents.Count == 0) return view!;
        if (view.Documents.All(d => d is null || IsAllowed(d.Location))) return view;

        view.Documents = view.Documents.Select(d =>
        {
            if (d is null) return d!;
            var reason = Check(d.Location);
            if (reason is null) return d;
            LogBlocked(tenantId: null, view.PlanId, d.DocType, reason);
            return new PlanDocumentLink
            {
                DocType = d.DocType,
                DisplayName = d.DisplayName,
                Location = null,
                LocationBlocked = true,
                ContentType = d.ContentType,
                Size = d.Size,
                ContentHashSha256 = d.ContentHashSha256,
                Version = d.Version,
                EffectiveDate = d.EffectiveDate,
            };
        }).ToList();
        return view;
    }

    private void LogBlocked(string? tenantId, string? planId, string? documentId, string reason)
    {
        _logger.LogWarning(
            "Plan document location withheld on read: tenant {TenantId} plan {PlanId} document {DocumentId}: {Reason}",
            Sanitize(tenantId), Sanitize(planId), Sanitize(documentId), reason);
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
