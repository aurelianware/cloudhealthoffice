using System.Text;
using System.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartAuthService.Services;

namespace SmartAuthService.Controllers;

/// <summary>
/// The signed-in person's connected apps: every app they approved on the
/// consent page, with the permissions approved, and a button to withdraw
/// each approval. Withdrawing revokes the approval and every token issued
/// under it (the app's refresh tokens stop working at once); the app must ask
/// again, and the person sees the consent page again.
///
/// Each revoke form carries a short-lived Data Protection proof bound to the
/// signed-in identity and that approval (<see cref="SmartConsent.IssueRevocation"/>),
/// the same pattern as the consent form: another site cannot post a
/// revocation, and one person cannot revoke another's approval.
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class AccountAppsController : ControllerBase
{
    public const string ApprovalField = "approval";
    public const string TokenField = "revoke_token";

    private readonly SmartAppApprovals _approvals;
    private readonly SmartConsent _consent;
    private readonly SmartAuthAudit _audit;

    public AccountAppsController(SmartAppApprovals approvals, SmartConsent consent, SmartAuthAudit audit)
    {
        _approvals = approvals;
        _consent = consent;
        _audit = audit;
    }

    /// <summary>GET /account/apps — the apps the signed-in person has approved.</summary>
    [HttpGet("~/account/apps")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (await SignedInSubjectAsync() is not { } subject)
            return Redirect($"/account/login?returnUrl={Uri.EscapeDataString("/account/apps")}");

        static string E(string? s) => HttpUtility.HtmlEncode(s ?? string.Empty);

        var approvals = await _approvals.ForSubjectAsync(subject, ct);
        var body = new StringBuilder();
        if (Request.Query["revoked"] == "1")
            body.Append("<p id=\"revoked\">Access withdrawn. The app must ask you again.</p>");
        if (approvals.Count == 0)
        {
            body.Append("<p id=\"none\">You have not approved any apps.</p>");
        }
        else
        {
            body.Append("<ul id=\"apps\" style=\"padding-left:0;list-style:none\">");
            foreach (var approval in approvals)
            {
                body.Append($"""
                    <li style="margin-bottom:16px;border-bottom:1px solid #ddd;padding-bottom:8px">
                      <strong>{E(approval.DisplayName)}</strong> <code style="color:#666">{E(approval.ClientId)}</code><br>
                      <span style="font-size:0.9em">{E(string.Join(" ", approval.Scopes))}</span><br>
                      <span style="font-size:0.8em;color:#666">Approved {E(approval.CreatedAt?.ToString("yyyy-MM-dd"))}</span>
                      <form method="post" action="/account/apps/revoke">
                        <input type="hidden" name="{ApprovalField}" value="{E(approval.Id)}">
                        <input type="hidden" name="{TokenField}" value="{E(_consent.IssueRevocation(subject, approval.Id))}">
                        <button type="submit" style="padding:4px 12px">Withdraw access</button>
                      </form>
                    </li>
                    """);
            }
            body.Append("</ul>");
        }

        return SmartSecurityHeaders.Page(Response, $"""
            <!DOCTYPE html>
            <html lang="en">
            <head><meta charset="utf-8"><title>Your connected apps</title></head>
            <body style="font-family:sans-serif;max-width:560px;margin:80px auto">
              <h2>Your connected apps</h2>
              {body}
            </body>
            </html>
            """);
    }

    /// <summary>POST /account/apps/revoke — withdraws one of the signed-in person's approvals.</summary>
    [HttpPost("~/account/apps/revoke")]
    [IgnoreAntiforgeryToken] // The Data Protection proof below is the anti-forgery check.
    public async Task<IActionResult> Revoke(CancellationToken ct)
    {
        if (await SignedInSubjectAsync() is not { } subject)
            return Unauthorized(new { error = "sign_in_required" });

        var id = Request.HasFormContentType ? Request.Form[ApprovalField].ToString() : string.Empty;
        var proof = Request.HasFormContentType ? Request.Form[TokenField].ToString() : string.Empty;
        if (string.IsNullOrEmpty(id) || !_consent.VerifyRevocation(proof, subject, id))
            return BadRequest(new { error = "invalid_revocation" });

        var approval = await _approvals.FindAsync(id, ct);
        if (approval is null || !string.Equals(approval.Subject, subject, StringComparison.Ordinal))
            return NotFound();

        if (await _approvals.RevokeAsync(id, ct))
            _audit.ConsentRevoked(subject, approval.ClientId, "member", subject);

        return new RedirectResult("/account/apps?revoked=1") { PreserveMethod = false };
    }

    private async Task<string?> SignedInSubjectAsync()
    {
        var auth = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return auth.Succeeded ? SmartSession.IdentityOf(auth.Principal)?.ToString() : null;
    }
}
