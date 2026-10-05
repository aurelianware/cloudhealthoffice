namespace SmartAuthService.Services;

/// <summary>
/// Browser security headers for smart-auth-service.
///
/// <para>
/// Every response: <c>X-Frame-Options: DENY</c> and
/// <c>Content-Security-Policy: frame-ancestors 'none'</c> (no site can frame
/// the login, consent or link pages and trick a click on Allow),
/// <c>X-Content-Type-Options: nosniff</c> and <c>Referrer-Policy: no-referrer</c>
/// (an authorization code or launch token in a URL never leaves in a Referer).
/// </para>
///
/// <para>
/// The HTML pages this service renders itself (<see cref="Page"/>) get a
/// fuller policy, <see cref="PagePolicy"/>: nothing loads from anywhere but
/// this origin, no script runs, no base or plugin. It has no
/// <c>form-action</c>: the consent form posts to <c>/connect/authorize</c>,
/// which redirects to the client's registered redirect URI (any origin), and
/// browsers apply form-action to that redirect, so form-action 'self' would
/// break the code flow. OpenIddict's own <c>response_mode=form_post</c> page
/// (an inline auto-submit script posting to the client) keeps only the
/// frame-ancestors policy for the same reason.
/// </para>
/// </summary>
public static class SmartSecurityHeaders
{
    public const string FrameOnlyPolicy = "frame-ancestors 'none'";

    /// <summary>The CSP of the pages this service renders. Inline style attributes only; no script.</summary>
    public const string PagePolicy =
        "default-src 'self'; script-src 'none'; style-src 'unsafe-inline'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";

    public static IApplicationBuilder UseSmartSecurityHeaders(this IApplicationBuilder app)
        => app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["X-Frame-Options"] = "DENY";
                headers["X-Content-Type-Options"] = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                // A page's own fuller policy (which also forbids framing) is kept.
                if (!headers.ContainsKey("Content-Security-Policy"))
                    headers["Content-Security-Policy"] = FrameOnlyPolicy;
                return Task.CompletedTask;
            });
            return next(context);
        });

    /// <summary>An HTML page rendered by this service, with <see cref="PagePolicy"/>.</summary>
    public static Microsoft.AspNetCore.Mvc.ContentResult Page(HttpResponse response, string html)
    {
        response.Headers["Content-Security-Policy"] = PagePolicy;
        return new Microsoft.AspNetCore.Mvc.ContentResult
        {
            Content = html,
            ContentType = "text/html; charset=utf-8",
            StatusCode = StatusCodes.Status200OK,
        };
    }
}
