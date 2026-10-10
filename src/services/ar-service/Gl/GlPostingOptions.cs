using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ArService.Gl;

/// <summary>
/// Configuration section <c>GlPosting</c>. Off by default: with <see cref="Enabled"/>
/// false the event endpoint answers 409 and stores nothing (the producer keeps the
/// event and retries), and no GL write happens.
/// <code>
/// "GlPosting": {
///   "Enabled": true,
///   "Tenants": {
///     "tenant-1": { "Accounts": { "ClaimsExpense": "5100", "ClaimsPayable": "2100",
///                                 "AchInTransit": "1015", "ProviderReceivable": "1250" } }
///   }
/// }
/// </code>
/// Account values are <c>GlAccount.AccountNumber</c>s of the tenant's chart in
/// ar-service. A role a tenant does not map, or maps to an account that is missing,
/// inactive or not effective on the entry date, parks the event (never drops it).
/// </summary>
public sealed class GlPostingOptions
{
    public const string SectionName = "GlPosting";

    public bool Enabled { get; set; }

    public Dictionary<string, GlTenantPostingOptions> Tenants { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The account number mapped to <paramref name="role"/> for <paramref name="tenantId"/>, or null.</summary>
    public string? AccountFor(string tenantId, GlPostingRole role)
        => Tenants.TryGetValue(tenantId, out var tenant) && tenant.Accounts.TryGetValue(role.ToString(), out var account)
           && !string.IsNullOrWhiteSpace(account)
            ? account.Trim()
            : null;
}

public sealed class GlTenantPostingOptions
{
    /// <summary>Role name (<see cref="GlPostingRole"/>) to account number.</summary>
    public Dictionary<string, string> Accounts { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Startup validation (ValidateOnStart): unknown roles, malformed account numbers, and
/// role pairs that would make an entry debit and credit the same account fail the start.
/// </summary>
public sealed class GlPostingOptionsValidator : IValidateOptions<GlPostingOptions>
{
    private static readonly Regex AccountNumber = new("^[A-Za-z0-9][A-Za-z0-9.\\-]{0,19}$", RegexOptions.Compiled);

    /// <summary>Roles that appear on opposite sides of one entry: they may not share an account.</summary>
    private static readonly (GlPostingRole, GlPostingRole)[] OppositeSides =
    [
        (GlPostingRole.ClaimsExpense, GlPostingRole.ClaimsPayable),
        (GlPostingRole.ClaimsExpense, GlPostingRole.ProviderReceivable),
        (GlPostingRole.ClaimsPayable, GlPostingRole.AchInTransit),
        (GlPostingRole.ClaimsPayable, GlPostingRole.ProviderReceivable),
    ];

    public ValidateOptionsResult Validate(string? name, GlPostingOptions options)
    {
        var failures = new List<string>();
        foreach (var (tenant, settings) in options.Tenants)
        {
            if (string.IsNullOrWhiteSpace(tenant))
                failures.Add("GlPosting:Tenants has an empty tenant id.");
            var mapped = new Dictionary<GlPostingRole, string>();
            foreach (var (roleName, account) in settings.Accounts)
            {
                if (!Enum.TryParse<GlPostingRole>(roleName, ignoreCase: false, out var role) || !Enum.IsDefined(role))
                {
                    failures.Add($"GlPosting:Tenants:{tenant}:Accounts:{roleName} is not a posting role ({string.Join(", ", Enum.GetNames<GlPostingRole>())}).");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(account) || !AccountNumber.IsMatch(account.Trim()))
                {
                    failures.Add($"GlPosting:Tenants:{tenant}:Accounts:{roleName} must be a GL account number (letters, digits, '.', '-', at most 20).");
                    continue;
                }
                mapped[role] = account.Trim();
            }
            foreach (var (a, b) in OppositeSides)
            {
                if (mapped.TryGetValue(a, out var x) && mapped.TryGetValue(b, out var y) && string.Equals(x, y, StringComparison.OrdinalIgnoreCase))
                    failures.Add($"GlPosting:Tenants:{tenant}: {a} and {b} are both {x}; they are posted on opposite sides of one entry and need different accounts.");
            }
        }
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
