using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using TenantService.CpcdsSftp;
using TenantService.Security;

namespace TenantService.Controllers;

/// <summary>
/// The tenant's SFTP login for the hosted CPCDS drop: where its core sends
/// the daily extracts (docs/integration/CPCDS-SFTP-DROP-GCP.md). The plan
/// registers SSH public keys; there is no password to issue, store or send.
/// Acts only on the caller's own tenant (<see cref="RouteTenantFilter"/>).
/// </summary>
[ApiController]
[Route("api/v1/tenants/{tenantId}/cpcds-sftp")]
[Produces("application/json")]
public class CpcdsSftpController : ControllerBase
{
    private readonly ICpcdsSftpAccounts _accounts;
    private readonly TenantAuditLog _audit;

    public CpcdsSftpController(ICpcdsSftpAccounts accounts, TenantAuditLog audit)
    {
        _accounts = accounts;
        _audit = audit;
    }

    /// <summary>The login: host, port, username, folders, the registered keys' fingerprints.</summary>
    [HttpGet]
    [RequirePermission(TenantPermissions.SettingsManage)]
    [ProducesResponseType(typeof(CpcdsSftpAccount), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Get(string tenantId, CancellationToken ct)
        => Call(() => _accounts.GetAsync(tenantId, ct));

    /// <summary>
    /// Creates the login, or replaces its keys (and turns it back on). Every
    /// key is checked first; one bad key refuses the whole request, so a plan
    /// never ends up with fewer keys than it sent.
    /// </summary>
    [HttpPut]
    [RequirePermission(TenantPermissions.SettingsManage)]
    [ProducesResponseType(typeof(CpcdsSftpAccount), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Put(string tenantId, [FromBody] CpcdsSftpKeysRequest request, CancellationToken ct)
    {
        var lines = request.PublicKeys ?? [];
        if (lines.Count == 0)
            return BadRequest(new { error = "invalid_keys", errors = new[] { "publicKeys: at least one SSH public key." } });
        if (lines.Count > CpcdsSftpKeysRequest.MaxKeys)
            return BadRequest(new { error = "invalid_keys", errors = new[] { $"publicKeys: at most {CpcdsSftpKeysRequest.MaxKeys}." } });

        var keys = new List<SshPublicKey>();
        var errors = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var (key, error) = SshPublicKey.Parse(lines[i]);
            if (key is null) errors.Add($"publicKeys[{i}]: {error}");
            else keys.Add(key);
        }
        if (errors.Count > 0)
            return BadRequest(new { error = "invalid_keys", errors });

        var result = await Call(() => _accounts.UpsertAsync(tenantId, keys, ct));
        _audit.Record("set CPCDS SFTP keys", tenantId, result is OkObjectResult ? "allowed" : "failed");
        return result;
    }

    /// <summary>Turns the login off. Files already uploaded stay and still load.</summary>
    [HttpDelete]
    [RequirePermission(TenantPermissions.SettingsManage)]
    [ProducesResponseType(typeof(CpcdsSftpAccount), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Delete(string tenantId, CancellationToken ct)
    {
        var result = await Call(() => _accounts.DisableAsync(tenantId, ct));
        _audit.Record("disable CPCDS SFTP login", tenantId, result is OkObjectResult ? "allowed" : "failed");
        return result;
    }

    private async Task<IActionResult> Call(Func<Task<CpcdsSftpAccount>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is CpcdsSftpUnavailableException or HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "sftp_unavailable", detail = ex is CpcdsSftpUnavailableException ? ex.Message : "The SFTP server could not be reached." });
        }
    }
}

public sealed class CpcdsSftpKeysRequest
{
    public const int MaxKeys = 5;

    /// <summary>OpenSSH public key lines, e.g. <c>ssh-ed25519 AAAA... core-prod</c>.</summary>
    public List<string>? PublicKeys { get; set; }
}
