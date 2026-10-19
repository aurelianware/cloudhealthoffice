using System.Collections;
using System.Diagnostics;
using System.Reflection;
using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace ClaimsService.Audit;

/// <summary>
/// Marks an endpoint that reads claim data. Every call to it is recorded in
/// the claim access log (<see cref="ClaimAccessEvent"/>): allowed, denied or
/// not found.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AuditClaimAccessAttribute : Attribute
{
    public AuditClaimAccessAttribute(string action) => Action = action;

    /// <summary>What is read: View, Search, Notes, Edits, AdjudicationDetail, ...</summary>
    public string Action { get; }

    /// <summary>The route value holding the claim id, when the endpoint reads one claim.</summary>
    public string? ClaimIdRouteValue { get; init; }

    /// <summary>The route value holding the claim number, when the endpoint reads by number.</summary>
    public string? ClaimNumberRouteValue { get; init; }

    /// <summary>Query parameters that name the member searched for (memberId, patient, ...).</summary>
    public string[] MemberQueryParameters { get; init; } = ["memberId", "patient"];
}

/// <summary>Writes access events; never fails the read it records.</summary>
public sealed class ClaimAccessAuditWriter
{
    private readonly IClaimAccessAuditRepository _repository;
    private readonly ILogger<ClaimAccessAuditWriter> _logger;

    public ClaimAccessAuditWriter(IClaimAccessAuditRepository repository, ILogger<ClaimAccessAuditWriter> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task RecordAsync(
        HttpContext http, AuditClaimAccessAttribute audit, ICurrentActor actor, int statusCode,
        string? outcome, IReadOnlyList<ClaimSubject> subjects)
    {
        var routeClaimId = RouteValue(http, audit.ClaimIdRouteValue);
        var routeClaimNumber = RouteValue(http, audit.ClaimNumberRouteValue);
        var single = routeClaimId is not null || routeClaimNumber is not null;

        var tenant = http.Items["TenantId"]?.ToString() is { Length: > 0 } t ? t : SafeTenant(actor);
        var first = subjects.FirstOrDefault();
        var memberFromQuery = audit.MemberQueryParameters
            .Select(p => http.Request.Query.TryGetValue(p, out var v) ? v.ToString() : null)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var members = subjects.Select(s => s.MemberId).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToList();

        var accessEvent = new ClaimAccessEvent
        {
            TenantId = tenant,
            ClaimId = single ? routeClaimId ?? first?.ClaimId : null,
            ClaimNumber = single ? routeClaimNumber ?? first?.ClaimNumber : null,
            // A member is recorded when the read concerns one member: the
            // member searched for, or the only member in the results.
            MemberId = memberFromQuery ?? (members.Count == 1 ? members[0] : null),
            ClaimIds = single
                ? []
                : subjects.Select(s => s.ClaimId).OfType<string>().Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct().Take(ClaimAccessEvent.MaxClaimIds).ToList(),
            ResultCount = single ? null : subjects.Count,
            Action = audit.Action,
            Outcome = outcome ?? OutcomeFor(statusCode),
            StatusCode = statusCode,
            HttpMethod = http.Request.Method,
            Route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText,
            ActorId = SafeUser(actor),
            ActorName = SafeName(actor),
            ActorRoles = SafeRoles(actor),
            ActorIsService = SafeIsService(actor),
            CorrelationId = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier,
            ClientIp = http.Connection.RemoteIpAddress?.ToString(),
            OccurredAt = DateTime.UtcNow,
        };

        try
        {
            await _repository.AppendAsync(accessEvent, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The read is not failed for an audit outage; the gap is logged
            // (with who and what) so it can be reconciled.
            _logger.LogError(ex,
                "Claim access audit write failed: {Action} {Outcome} by {ActorId} on claim {ClaimId} ({Route})",
                accessEvent.Action, accessEvent.Outcome, accessEvent.ActorId, accessEvent.ClaimId ?? "(search)", accessEvent.Route);
        }
    }

    public static string OutcomeFor(int statusCode) => statusCode switch
    {
        401 or 403 => ClaimAccessOutcomes.Denied,
        404 => ClaimAccessOutcomes.NotFound,
        >= 500 => ClaimAccessOutcomes.Error,
        _ => ClaimAccessOutcomes.Allowed,
    };

    private static string? RouteValue(HttpContext http, string? name) =>
        name is not null && http.GetRouteValue(name)?.ToString() is { Length: > 0 } value ? value : null;

    // An unauthenticated or denied caller may have no resolvable actor.
    private static string SafeTenant(ICurrentActor a) { try { return a.TenantId; } catch { return string.Empty; } }
    private static string SafeUser(ICurrentActor a) { try { return a.UserId; } catch { return "(unauthenticated)"; } }
    private static string? SafeName(ICurrentActor a) { try { return a.DisplayName; } catch { return null; } }
    private static List<string> SafeRoles(ICurrentActor a) { try { return a.Roles.ToList(); } catch { return []; } }
    private static bool SafeIsService(ICurrentActor a) { try { return a.IsService; } catch { return false; } }
}

/// <summary>A claim (and its member) found in a response.</summary>
public sealed record ClaimSubject(string? ClaimId, string? ClaimNumber, string? MemberId);

/// <summary>
/// Records every call to an <see cref="AuditClaimAccessAttribute"/> endpoint
/// that reaches the action (allowed or not found). Calls refused by
/// authorization never reach it; <see cref="ClaimAccessDenialRecorder"/>
/// records those.
/// </summary>
public sealed class ClaimAccessAuditFilter : IAsyncActionFilter
{
    private readonly ClaimAccessAuditWriter _writer;
    private readonly ICurrentActor _actor;

    public ClaimAccessAuditFilter(ClaimAccessAuditWriter writer, ICurrentActor actor)
    {
        _writer = writer;
        _actor = actor;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var audit = (context.ActionDescriptor as ControllerActionDescriptor)?.MethodInfo
            .GetCustomAttribute<AuditClaimAccessAttribute>();
        if (audit is null)
        {
            await next();
            return;
        }

        var executed = await next();
        var statusCode = executed.Exception is not null && !executed.ExceptionHandled
            ? 500
            : executed.Result is IStatusCodeActionResult { StatusCode: { } code } ? code
            : executed.Result is ObjectResult { StatusCode: null } or JsonResult or ContentResult or FileResult or EmptyResult ? 200
            : 200;
        var value = (executed.Result as ObjectResult)?.Value ?? (executed.Result as JsonResult)?.Value;
        var subjects = statusCode is >= 200 and < 300 ? ClaimSubjects.From(value) : [];

        await _writer.RecordAsync(context.HttpContext, audit, _actor, statusCode, null, subjects);
    }
}

/// <summary>
/// Records a denied call to an audited endpoint (the authorization middleware
/// refuses it before any filter runs), then lets the default handler answer.
/// </summary>
public sealed class ClaimAccessDenialRecorder : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _inner = new();

    public async Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden
            && context.GetEndpoint()?.Metadata.GetMetadata<AuditClaimAccessAttribute>() is { } audit
            && context.RequestServices.GetService<ClaimAccessAuditWriter>() is { } writer
            && context.RequestServices.GetService<ICurrentActor>() is { } actor)
        {
            await writer.RecordAsync(context, audit, actor, StatusCodes.Status403Forbidden, ClaimAccessOutcomes.Denied, []);
        }

        await _inner.HandleAsync(next, context, policy, authorizeResult);
    }
}

/// <summary>
/// Finds the claims (and members) in a response body: a claim, a list of
/// claims, or a page object holding a list. A property named ClaimId wins;
/// a type whose name mentions Claim or ExplanationOfBenefit uses its Id.
/// </summary>
public static class ClaimSubjects
{
    private static readonly string[] ListProperties = ["Items", "Claims", "Results", "Entries", "Entry", "Data", "ExplanationOfBenefits"];

    public static IReadOnlyList<ClaimSubject> From(object? value)
    {
        if (value is null or string) return [];

        if (value is IEnumerable items)
        {
            var found = new List<ClaimSubject>();
            foreach (var item in items)
            {
                if (Single(item) is { } s) found.Add(s);
                if (found.Count >= 1000) break;
            }
            return found;
        }

        if (Single(value) is { } one) return [one];

        foreach (var name in ListProperties)
        {
            var list = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(value);
            if (list is IEnumerable and not string) return From(list);
        }
        return [];
    }

    private static ClaimSubject? Single(object? item)
    {
        if (item is null) return null;
        var type = item.GetType();
        var claimId = Text(type, item, "ClaimId")
            ?? (type.Name.Contains("Claim", StringComparison.OrdinalIgnoreCase)
                || type.Name.Contains("ExplanationOfBenefit", StringComparison.OrdinalIgnoreCase)
                ? Text(type, item, "Id") : null);
        if (claimId is null) return null;
        return new ClaimSubject(claimId, Text(type, item, "ClaimNumber"), Text(type, item, "MemberId"));
    }

    private static string? Text(Type type, object item, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(item) is { } v
        && v.ToString() is { Length: > 0 } s ? s : null;
}
