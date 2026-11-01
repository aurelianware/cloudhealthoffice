using Microsoft.Extensions.Options;

namespace EncounterSubmissionService.StateRoutes;

/// <summary>
/// The output a state route produces.
/// </summary>
public enum EncounterRouteFormat
{
    /// <summary>Legacy Florida staging: claim JSON for the FMMIS hand-off (unchanged).</summary>
    FmmisJson,

    /// <summary>ASC X12 005010 837P / 837I encounter interchange.</summary>
    X12_837
}

/// <summary>
/// One state's encounter route: who receives it, how the deadline counts, and
/// what format it is produced in. State-specific layout rules live in the route
/// definition so the batch pipeline, writer and reconciliation stay shared.
/// </summary>
public sealed record StateEncounterRoute
{
    public required string StateCode { get; init; }

    /// <summary>Short receiver label for logs, files and the console (TMHP, DHCS, FMMIS).</summary>
    public required string Receiver { get; init; }

    /// <summary>Line of business the route admits. All current routes are Medicaid only.</summary>
    public string LineOfBusiness { get; init; } = "Medicaid";

    public required EncounterRouteFormat Format { get; init; }

    public required EncounterDeadlineRule DefaultDeadlineRule { get; init; }
    public required int DefaultDeadlineDays { get; init; }

    /// <summary>Where the deadline default comes from, so an operator can check it.</summary>
    public required string DeadlineSource { get; init; }

    /// <summary>Response files the receiver returns, reconciled in a later step.</summary>
    public IReadOnlyList<string> ResponseFiles { get; init; } = Array.Empty<string>();

    /// <summary>File name prefix for staged interchanges.</summary>
    public required string FilePrefix { get; init; }

    public DateTime ComputeDeadline(DateTime adjudicatedAtUtc, StateEncounterRouteOptions? options)
    {
        var rule = options?.DeadlineRule ?? DefaultDeadlineRule;
        var days = options?.DeadlineDays is > 0 ? options.DeadlineDays.Value : DefaultDeadlineDays;
        var adjudicated = DateTime.SpecifyKind(adjudicatedAtUtc, DateTimeKind.Utc);
        return rule switch
        {
            EncounterDeadlineRule.DaysAfterEndOfAdjudicationMonth =>
                new DateTime(adjudicated.Year, adjudicated.Month,
                        DateTime.DaysInMonth(adjudicated.Year, adjudicated.Month), 23, 59, 59, DateTimeKind.Utc)
                    .AddDays(days),
            _ => adjudicated.AddDays(days)
        };
    }

    // ── Route definitions ───────────────────────────────────────────────

    /// <summary>
    /// Florida AHCA / FMMIS. Behavior unchanged: claim JSON staging, deadline from the
    /// tenant's compliance config (default 60 days after adjudication).
    /// </summary>
    public static readonly StateEncounterRoute Florida = new()
    {
        StateCode = "FL",
        Receiver = "FMMIS",
        Format = EncounterRouteFormat.FmmisJson,
        DefaultDeadlineRule = EncounterDeadlineRule.DaysAfterAdjudication,
        DefaultDeadlineDays = 60,
        DeadlineSource = "Tenant compliance config EncounterSubmissionDays (AHCA 60-day window)",
        ResponseFiles = new[] { "999" },
        FilePrefix = "FMMIS"
    };

    /// <summary>
    /// Texas HHSC, received through TMHP. 837P / 837I 5010 per the TMHP encounters
    /// companion guide. The deadline default (30 days after the end of the month the
    /// claim was adjudicated in) follows the HHSC managed care contract's encounter
    /// timeliness standard and must be confirmed against the plan's signed contract
    /// (TX-00). Responses: 999, 277CA, then the HHSC encounter response reports.
    /// </summary>
    public static readonly StateEncounterRoute Texas = new()
    {
        StateCode = "TX",
        Receiver = "TMHP",
        Format = EncounterRouteFormat.X12_837,
        DefaultDeadlineRule = EncounterDeadlineRule.DaysAfterEndOfAdjudicationMonth,
        DefaultDeadlineDays = 30,
        DeadlineSource = "HHSC MCO contract encounter timeliness (verify against the plan's contract, TX-00)",
        ResponseFiles = new[] { "999", "277CA" },
        FilePrefix = "TMHP"
    };

    /// <summary>
    /// California DHCS (Medi-Cal managed care), received by the DHCS encounter system
    /// (PACES). 837P / 837I 5010 per the DHCS companion guides. APL 14-019 requires at
    /// least monthly submission and corrections within 15 days of the response file;
    /// the default here is 30 days after the end of the adjudication month and must be
    /// confirmed against the current encounter APL and plan contract.
    /// Responses: 999, 277, and the Encounter Validation Response (EVR) XML file.
    /// </summary>
    public static readonly StateEncounterRoute California = new()
    {
        StateCode = "CA",
        Receiver = "DHCS",
        Format = EncounterRouteFormat.X12_837,
        DefaultDeadlineRule = EncounterDeadlineRule.DaysAfterEndOfAdjudicationMonth,
        DefaultDeadlineDays = 30,
        DeadlineSource = "DHCS APL 14-019 monthly submission (verify against the current encounter APL)",
        ResponseFiles = new[] { "999", "277", "EVR" },
        FilePrefix = "DHCS"
    };

    public static IReadOnlyList<StateEncounterRoute> All { get; } = new[] { Florida, Texas, California };
}

/// <summary>
/// Decides which state route, if any, an adjudicated claim belongs to.
/// </summary>
public interface IStateEncounterRouteRegistry
{
    /// <summary>
    /// Returns the route when the state has one, it is enabled, and the tenant is
    /// configured for it; otherwise null with a reason.
    /// </summary>
    StateEncounterRoute? Resolve(string? stateCode, string? lineOfBusiness, string tenantId, out string? reason);

    StateEncounterRoute? Get(string stateCode);

    StateEncounterRouteOptions? GetOptions(string stateCode);

    StateEncounterTenantOptions? GetTenantOptions(string stateCode, string tenantId);

    /// <summary>The X12 routes (TX, CA) this tenant is fully configured for.</summary>
    IReadOnlyList<StateEncounterRoute> EnrolledX12Routes(string tenantId);
}

public sealed class StateEncounterRouteRegistry : IStateEncounterRouteRegistry
{
    private readonly IReadOnlyDictionary<string, StateEncounterRoute> _routes;
    private readonly IReadOnlyDictionary<string, StateEncounterRouteOptions> _options;

    public StateEncounterRouteRegistry(IOptions<Dictionary<string, StateEncounterRouteOptions>> options)
        : this(options.Value)
    {
    }

    public StateEncounterRouteRegistry(IDictionary<string, StateEncounterRouteOptions>? options)
    {
        _routes = StateEncounterRoute.All.ToDictionary(r => r.StateCode, StringComparer.OrdinalIgnoreCase);
        _options = new Dictionary<string, StateEncounterRouteOptions>(
            options ?? new Dictionary<string, StateEncounterRouteOptions>(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A registry with no state configuration: Florida only, as before.</summary>
    public static StateEncounterRouteRegistry FloridaOnly() => new((IDictionary<string, StateEncounterRouteOptions>?)null);

    public StateEncounterRoute? Get(string stateCode) =>
        _routes.TryGetValue(stateCode, out var route) ? route : null;

    public StateEncounterRouteOptions? GetOptions(string stateCode) =>
        _options.TryGetValue(stateCode, out var o) ? o : null;

    public StateEncounterTenantOptions? GetTenantOptions(string stateCode, string tenantId)
    {
        var o = GetOptions(stateCode);
        if (o is null || string.IsNullOrWhiteSpace(tenantId)) return null;
        // Tenants is bound from configuration; rebuild case-insensitively in case the
        // binder replaced the dictionary instance.
        foreach (var (key, value) in o.Tenants)
            if (string.Equals(key, tenantId, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    public IReadOnlyList<StateEncounterRoute> EnrolledX12Routes(string tenantId) =>
        StateEncounterRoute.All
            .Where(r => r.Format == EncounterRouteFormat.X12_837
                        && Resolve(r.StateCode, r.LineOfBusiness, tenantId, out _) is not null)
            .ToList();

    public StateEncounterRoute? Resolve(string? stateCode, string? lineOfBusiness, string tenantId, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(stateCode) || !_routes.TryGetValue(stateCode, out var route))
        {
            reason = $"no encounter route exists for state '{stateCode}'";
            return null;
        }

        if (!string.Equals(lineOfBusiness, route.LineOfBusiness, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"line of business '{lineOfBusiness}' is not {route.LineOfBusiness}";
            return null;
        }

        // Florida keeps its long-standing behavior: always admitted, identities and
        // deadline from the existing FMMIS path.
        if (route.Format == EncounterRouteFormat.FmmisJson)
            return route;

        var options = GetOptions(route.StateCode);
        if (options is null || !options.Enabled)
        {
            reason = $"the {route.StateCode} route is not enabled";
            return null;
        }

        var stateMissing = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ReceiverId)) stateMissing.Add(nameof(options.ReceiverId));
        if (string.IsNullOrWhiteSpace(options.ReceiverName)) stateMissing.Add(nameof(options.ReceiverName));
        if (string.IsNullOrWhiteSpace(options.DestinationPayerId)) stateMissing.Add(nameof(options.DestinationPayerId));
        if (string.IsNullOrWhiteSpace(options.DestinationPayerName)) stateMissing.Add(nameof(options.DestinationPayerName));
        if (stateMissing.Count > 0)
        {
            reason = $"the {route.StateCode} route is missing {string.Join(", ", stateMissing)}";
            return null;
        }

        var tenant = GetTenantOptions(route.StateCode, tenantId);
        if (tenant is null)
        {
            reason = $"tenant is not enrolled on the {route.StateCode} route";
            return null;
        }

        var missing = tenant.MissingFields();
        if (missing.Count > 0)
        {
            reason = $"tenant's {route.StateCode} identities are missing {string.Join(", ", missing)}";
            return null;
        }

        return route;
    }
}
