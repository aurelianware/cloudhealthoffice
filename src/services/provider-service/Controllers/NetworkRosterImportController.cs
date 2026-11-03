using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Programs;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using ProviderService.Security;
using ProviderService.Services.Roster;

namespace ProviderService.Controllers;

/// <summary>
/// A plan's network roster from its core (NPI, network, plan, dates), so CHO
/// finds the same providers in network that the core does. See
/// <c>docs/integration/NETWORK-ROSTER-IMPORTER.md</c>.
/// </summary>
[ApiController]
[Route("api/v1/providers/import/network-roster")]
[Produces("application/json")]
public class NetworkRosterImportController : ControllerBase
{
    private const long MaxUploadBytes = 100L * 1024 * 1024;

    private readonly NetworkRosterImporter _importer;
    private readonly ILogger<NetworkRosterImportController> _logger;

    public NetworkRosterImportController(NetworkRosterImporter importer, ILogger<NetworkRosterImportController> logger)
    {
        _importer = importer;
        _logger = logger;
    }

    /// <summary>
    /// Puts each row's provider in its network. Providers CHO does not have are
    /// created from NPPES; networks named in the file that do not exist are
    /// created when the caller holds <c>networks:write</c>. With a
    /// <c>Credentialed</c> column, a "yes" is recorded as the core's
    /// (delegated) credentialing approval when the caller holds
    /// <c>providers:credential</c>. Reloading adds nothing twice.
    /// </summary>
    /// <param name="file">CSV: NPI, Network, Effective Date (required); Plan, Line of Business, Tier, Term Date, Credentialed, Credentialed Date, Network Type.</param>
    /// <param name="lineOfBusiness">For rows that name none. Default Commercial.</param>
    /// <param name="dryRun">Check and report only.</param>
    [HttpPost]
    [RequirePermission("providers:write")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [ProducesResponseType(typeof(NetworkRosterImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(NetworkRosterImportResult), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NetworkRosterImportResult>> Import(
        [FromForm] IFormFile file,
        [FromForm] string? lineOfBusiness = null,
        [FromForm] bool dryRun = false,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "A non-empty roster file is required." });

        var lob = LineOfBusiness.Commercial;
        if (!string.IsNullOrWhiteSpace(lineOfBusiness)
            && (!Enum.TryParse(lineOfBusiness, ignoreCase: true, out lob) || !Enum.IsDefined(lob)))
        {
            return BadRequest(new { error = $"lineOfBusiness '{lineOfBusiness}' is not one of: {string.Join(", ", Enum.GetNames<LineOfBusiness>())}." });
        }

        string content;
        using (var reader = new StreamReader(file.OpenReadStream()))
            content = await reader.ReadToEndAsync(ct);

        var may = new NetworkRosterImporter.Permissions(
            CreateNetworks: ChoPrincipal.HasPermission(User, "networks:write"),
            RecordCredentialing: ChoPrincipal.HasPermission(User, "providers:credential"));
        var tenantId = this.TokenTenantId();
        var result = await _importer.ImportAsync(
            tenantId, content, lob, may, dryRun, this.TokenActorId(), HttpContext.TraceIdentifier, ct);
        _logger.LogInformation(
            "Network roster {FileName} for tenant {TenantId}: {Rows} rows, {Added} participations added, {Created} providers created, {Rejected} refused (dry run {DryRun})",
            LogSanitizer.Sanitize(file.FileName), LogSanitizer.Sanitize(tenantId),
            result.RowsRead, result.ParticipationsAdded, result.ProvidersCreated, result.Rejected, dryRun);

        return result.FileErrors.Count > 0 ? UnprocessableEntity(result) : Ok(result);
    }
}
