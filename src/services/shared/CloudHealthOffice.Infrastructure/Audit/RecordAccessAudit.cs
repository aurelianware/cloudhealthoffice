using System.Collections;
using System.Diagnostics;
using System.Reflection;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Audit;

/// <summary>
/// Marks an endpoint that reads a record. Every call is recorded in the
/// record access log (<see cref="RecordAccessEvent"/>): allowed, not found,
/// error, or denied by authorization.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AuditRecordAccessAttribute : Attribute
{
    public AuditRecordAccessAttribute(string recordType, string action)
    {
        RecordType = recordType;
        Action = action;
    }

    /// <summary>Member, Provider, Authorization, Payment.</summary>
    public string RecordType { get; }

    public string Action { get; }

    /// <summary>The route value holding the record id, for a single-record read.</summary>
    public string? RecordIdRouteValue { get; init; }

    /// <summary>The route value holding the member id (for a member read, the same as the record id).</summary>
    public string? MemberIdRouteValue { get; init; }

    /// <summary>Query parameters that name the member searched for.</summary>
    public string[] MemberQueryParameters { get; init; } = ["memberId"];

    /// <summary>In a list or search result, the property holding each record's id.</summary>
    public string ResultIdProperty { get; init; } = "Id";

    /// <summary>In a result, the property holding the member id.</summary>
    public string ResultMemberProperty { get; init; } = "MemberId";

    /// <summary>
    /// The response is one record found by something other than its id (a
    /// check number, an NPI, an auth number): its id is the record id, so the
    /// read is logged as a single-record read of that record.
    /// </summary>
    public bool ResultIsRecord { get; init; }
}

/// <summary>A record (and its member) found in a response.</summary>
public sealed record RecordSubject(string? RecordId, string? MemberId);

/// <summary>Writes access events; never fails the read it records.</summary>
public sealed class RecordAccessWriter
{
    private readonly IRecordAccessStore _store;
    private readonly ILogger<RecordAccessWriter> _logger;

    /// <summary>The longest a read waits for its access event to be stored.</summary>
    public static TimeSpan AppendTimeout { get; set; } = TimeSpan.FromSeconds(3);

    public RecordAccessWriter(IRecordAccessStore store, ILogger<RecordAccessWriter> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task RecordAsync(
        HttpContext http, AuditRecordAccessAttribute audit, ICurrentActor actor, int statusCode,
        string? outcome, IReadOnlyList<RecordSubject> subjects)
    {
        var recordId = RouteValue(http, audit.RecordIdRouteValue)
                       ?? (audit.ResultIsRecord && subjects.Count == 1 ? subjects[0].RecordId : null);
        var memberRoute = RouteValue(http, audit.MemberIdRouteValue);
        var memberQuery = audit.MemberQueryParameters
            .Select(p => http.Request.Query.TryGetValue(p, out var v) ? v.ToString() : null)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var members = subjects.Select(s => s.MemberId).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToList();
        var single = recordId is not null;

        await AppendAsync(http, actor, new RecordAccessEvent
        {
            RecordType = audit.RecordType,
            RecordId = recordId,
            MemberId = memberRoute ?? memberQuery ?? (members.Count == 1 ? members[0] : null),
            RecordIds = single
                ? []
                : subjects.Select(s => s.RecordId).Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct().Take(RecordAccessEvent.MaxRecordIds).ToList()!,
            ResultCount = single ? null : subjects.Count,
            Action = audit.Action,
            Outcome = outcome ?? RecordAccessOutcomes.For(statusCode),
            StatusCode = statusCode,
        });
    }

    /// <summary>Records an event built by the caller (e.g. a read of the access log itself).</summary>
    public async Task AppendAsync(HttpContext http, ICurrentActor actor, RecordAccessEvent accessEvent)
    {
        accessEvent.TenantId = http.Items["TenantId"]?.ToString() is { Length: > 0 } t ? t : Safe(() => actor.TenantId, string.Empty);
        accessEvent.HttpMethod = http.Request.Method;
        accessEvent.Route ??= (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        accessEvent.ActorId = Safe(() => actor.UserId, "(unauthenticated)");
        accessEvent.ActorName = Safe<string?>(() => actor.DisplayName, null);
        accessEvent.ActorRoles = Safe(() => actor.Roles.ToList(), new List<string>());
        accessEvent.ActorIsService = Safe(() => actor.IsService, false);
        accessEvent.CorrelationId = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier;
        accessEvent.ClientIp = http.Connection.RemoteIpAddress?.ToString();
        accessEvent.OccurredAt = DateTime.UtcNow;
        try
        {
            // Bounded: an unreachable store must not hold the read for the
            // driver's server-selection timeout.
            using var timeout = new CancellationTokenSource(AppendTimeout);
            await _store.AppendAsync(accessEvent, timeout.Token);
        }
        catch (Exception ex)
        {
            // The read is not failed for an audit outage; the gap is logged
            // (with who and what) so it can be reconciled.
            _logger.LogError(ex,
                "Record access audit write failed: {RecordType} {Action} {Outcome} by {ActorId} on {RecordId} ({Route})",
                ForLog(accessEvent.RecordType), ForLog(accessEvent.Action), ForLog(accessEvent.Outcome), ForLog(accessEvent.ActorId),
                ForLog(accessEvent.RecordId ?? "(search)"), ForLog(accessEvent.Route));
        }
    }

    // Record ids come from the URL: strip line breaks so they can't forge log entries.
    private static string ForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);

    private static string? RouteValue(HttpContext http, string? name) =>
        name is not null && http.GetRouteValue(name)?.ToString() is { Length: > 0 } value ? value : null;

    // An unauthenticated or denied caller may have no resolvable actor.
    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); } catch { return fallback; }
    }
}

/// <summary>Records every call to an audited endpoint that reaches the action.</summary>
public sealed class RecordAccessAuditFilter : IAsyncActionFilter
{
    private readonly RecordAccessWriter _writer;
    private readonly ICurrentActor _actor;

    public RecordAccessAuditFilter(RecordAccessWriter writer, ICurrentActor actor)
    {
        _writer = writer;
        _actor = actor;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var audit = (context.ActionDescriptor as ControllerActionDescriptor)?.MethodInfo.GetCustomAttribute<AuditRecordAccessAttribute>();
        if (audit is null)
        {
            await next();
            return;
        }

        var executed = await next();
        var statusCode = executed.Exception is not null && !executed.ExceptionHandled
            ? 500
            : executed.Result is IStatusCodeActionResult { StatusCode: { } code } ? code : 200;
        var value = (executed.Result as ObjectResult)?.Value ?? (executed.Result as JsonResult)?.Value;
        var subjects = statusCode is >= 200 and < 300 ? RecordSubjects.From(value, audit.ResultIdProperty, audit.ResultMemberProperty) : [];
        await _writer.RecordAsync(context.HttpContext, audit, _actor, statusCode, null, subjects);
    }
}

/// <summary>
/// Records a denied call to an audited endpoint (authorization refuses it
/// before any filter runs), then lets the default handler answer.
/// </summary>
public sealed class RecordAccessDenialRecorder : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _inner = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden
            && context.GetEndpoint()?.Metadata.GetMetadata<AuditRecordAccessAttribute>() is { } audit
            && context.RequestServices.GetService<RecordAccessWriter>() is { } writer
            && context.RequestServices.GetService<ICurrentActor>() is { } actor)
        {
            await writer.RecordAsync(context, audit, actor, StatusCodes.Status403Forbidden, RecordAccessOutcomes.Denied, []);
        }

        await _inner.HandleAsync(next, context, policy, authorizeResult);
    }
}

/// <summary>Finds records in a response: one record, a list, or a page object holding a list.</summary>
public static class RecordSubjects
{
    private static readonly string[] ListProperties = ["Items", "Results", "Data", "Entries", "Members", "Providers", "Authorizations", "Payments"];

    public static IReadOnlyList<RecordSubject> From(object? value, string idProperty = "Id", string memberProperty = "MemberId")
    {
        if (value is null or string) return [];
        if (value is IEnumerable items)
        {
            var found = new List<RecordSubject>();
            foreach (var item in items)
            {
                if (Single(item, idProperty, memberProperty) is { } s) found.Add(s);
                if (found.Count >= 1000) break;
            }
            return found;
        }
        foreach (var name in ListProperties)
        {
            var list = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(value);
            if (list is IEnumerable and not string) return From(list, idProperty, memberProperty);
        }
        return Single(value, idProperty, memberProperty) is { } one ? [one] : [];
    }

    private static RecordSubject? Single(object? item, string idProperty, string memberProperty)
    {
        if (item is null) return null;
        var type = item.GetType();
        var id = Text(type, item, idProperty);
        var member = Text(type, item, memberProperty);
        return id is null && member is null ? null : new RecordSubject(id, member);
    }

    private static string? Text(Type type, object item, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(item) is { } v
        && v.ToString() is { Length: > 0 } s ? s : null;
}

public static class RecordAccessAuditExtensions
{
    public const string AuditReadPermission = "audit:read";

    /// <summary>
    /// Records every read of an <see cref="AuditRecordAccessAttribute"/>
    /// endpoint. The store is MongoDB when the service has a database; a
    /// host without one (Development, tests) keeps the log in memory.
    /// </summary>
    public static IServiceCollection AddRecordAccessAudit(this IServiceCollection services)
    {
        services.TryAddSingleton<IRecordAccessStore>(sp =>
        {
            if (sp.GetService<IMongoDatabase>() is { } database) return new RecordAccessStoreMongo(database);

            // In-memory is for development hosts and tests. Anywhere else the
            // access log would vanish on restart, so say so loudly.
            var env = sp.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>();
            if (env is not null && !Microsoft.Extensions.Hosting.HostEnvironmentEnvExtensions.IsDevelopment(env))
            {
                sp.GetService<ILoggerFactory>()?.CreateLogger("CloudHealthOffice.Audit.RecordAccess")
                    .LogCritical("No MongoDB database is registered: record access events are kept in memory only and will be lost on restart. Configure MongoDb:ConnectionString.");
            }
            return new RecordAccessStoreInMemory();
        });
        services.TryAddScoped<RecordAccessWriter>();
        services.Configure<MvcOptions>(o => o.Filters.Add<RecordAccessAuditFilter>());
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, RecordAccessDenialRecorder>();
        return services;
    }

    /// <summary>
    /// The access log of one record type: <c>{prefix}/{id}/access-log</c> (who
    /// read this record) and <c>{prefix}/access-log</c> (by member, user and/or
    /// period). Both need <c>audit:read</c>; reading the log is itself recorded.
    /// </summary>
    public static IEndpointRouteBuilder MapRecordAccessLog(this IEndpointRouteBuilder app, string routePrefix, string recordType)
    {
        var prefix = routePrefix.TrimEnd('/');
        var policy = RequirePermissionAttribute.PolicyPrefix + AuditReadPermission;

        app.MapGet($"{prefix}/{{recordId}}/access-log", async (string recordId, int? limit, HttpContext http,
                IRecordAccessStore store, RecordAccessWriter writer, ICurrentActor actor) =>
            {
                var tenant = TenantOf(http, actor);
                var events = await store.QueryAsync(new RecordAccessQuery(tenant, recordType, RecordId: recordId, Limit: limit ?? 200));
                await writer.AppendAsync(http, actor, new RecordAccessEvent
                {
                    RecordType = recordType, RecordId = recordId, Action = RecordAccessActions.AccessLog,
                    MemberId = string.Equals(recordType, "Member", StringComparison.OrdinalIgnoreCase) ? recordId : null,
                    StatusCode = 200,
                });
                return Results.Ok(events);
            })
            .RequireAuthorization(policy);

        app.MapGet($"{prefix}/access-log", async (string? memberId, string? actorId, DateTime? from, DateTime? to, int? limit,
                HttpContext http, IRecordAccessStore store, RecordAccessWriter writer, ICurrentActor actor) =>
            {
                if (string.IsNullOrWhiteSpace(memberId) && string.IsNullOrWhiteSpace(actorId) && from is null)
                    return Results.BadRequest("Give a memberId, an actorId, or a from date.");
                if (from is not null && to is not null && from > to)
                    return Results.BadRequest("from must be before to.");
                var tenant = TenantOf(http, actor);
                var events = await store.QueryAsync(new RecordAccessQuery(tenant, recordType, MemberId: memberId, ActorId: actorId, From: from, To: to, Limit: limit ?? 200));
                await writer.AppendAsync(http, actor, new RecordAccessEvent
                {
                    RecordType = recordType, MemberId = memberId, Action = RecordAccessActions.AccessLog, StatusCode = 200,
                    ResultCount = events.Count,
                });
                return Results.Ok(events);
            })
            .RequireAuthorization(policy);

        return app;
    }

    private static string TenantOf(HttpContext http, ICurrentActor actor) =>
        http.Items["TenantId"]?.ToString() is { Length: > 0 } t ? t : actor.TenantId;
}
