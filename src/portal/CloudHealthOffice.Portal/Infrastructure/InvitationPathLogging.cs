namespace CloudHealthOffice.Portal.Infrastructure;

/// <summary>
/// Invitation links (<c>/invite/{code}</c>) carry a bearer secret in the path.
/// These framework categories write request URLs into logs, so they are held
/// at Warning (where they log no URLs), whatever the configured levels:
/// <list type="bullet">
/// <item><c>Microsoft.AspNetCore.Hosting.Diagnostics</c>: "Request starting/finished GET .../invite/{code}" (Information);</item>
/// <item><c>Microsoft.AspNetCore.Components.Server.Circuits.RemoteNavigationManager</c>: navigation targets (Debug);</item>
/// <item><c>Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler</c>: the post-sign-in redirect URI (Trace);</item>
/// <item><c>Microsoft.Identity.Web</c>'s account controller: the sign-in redirect URI.</item>
/// </list>
/// The portal has no request-path telemetry otherwise (no Application Insights
/// or OpenTelemetry; the Plausible component is unused and limited to public
/// marketing pages).
/// </summary>
public static class InvitationPathLogging
{
    public static readonly IReadOnlyList<string> UrlLoggingCategories = new[]
    {
        "Microsoft.AspNetCore.Hosting.Diagnostics",
        "Microsoft.AspNetCore.Components.Server.Circuits.RemoteNavigationManager",
        "Microsoft.AspNetCore.Authentication.OpenIdConnect",
        "Microsoft.Identity.Web.UI.Areas.MicrosoftIdentity.Controllers",
    };

    /// <summary>
    /// Added after configuration is bound, so these rules come later than (and
    /// therefore override) equally specific rules from appsettings.
    /// </summary>
    public static ILoggingBuilder KeepInvitationCodesOutOfLogs(this ILoggingBuilder logging)
    {
        foreach (var category in UrlLoggingCategories)
            logging.AddFilter(category, LogLevel.Warning);
        return logging;
    }
}
