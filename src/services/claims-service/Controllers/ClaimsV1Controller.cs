using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json.Nodes;
using ClaimsService.Adapters;
using ClaimsService.Fhir;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Snip = ClaimsService.EDI.Validation;

namespace ClaimsService.Controllers;

/// <summary>
/// v1 claims API — the canonical surface for claim submission and
/// member-scoped search.
///
/// <para>
/// <b>POST</b> — capability 5.3 ships the canonical
/// <c>POST /api/v1/claims</c> submission endpoint. Accepts an
/// <see cref="AdapterClaim"/> (vendor-neutral DTO from 5.2),
/// orchestrates validation + adapter call + version-event emission
/// through <see cref="IClaimSubmissionService"/>, and returns the
/// created claim version. Legacy <c>POST /api/claims</c> is marked
/// <c>[Obsolete]</c> and routes through the same service so the
/// audit chain is continuous regardless of which surface a caller
/// picks.
/// </para>
///
/// <para>
/// <b>GET</b> — member-scoped search powering the portal Member
/// Details Claims tab. Reads through the tenant-routed
/// <see cref="IClaimAdapter"/> (5.2) and projects each
/// <see cref="AdapterClaim"/> onto a FHIR R4 ExplanationOfBenefit
/// resource via <see cref="IExplanationOfBenefitProjector"/>. The
/// response shape (<see cref="EobSearchResponse"/>) is unchanged
/// from the pre-5.3 repo-routed implementation — portal contract
/// preserved.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/claims")]
[Produces("application/json")]
public class ClaimsV1Controller : ControllerBase
{
    private readonly ClaimAdapterFactory _adapterFactory;
    private readonly IClaimSubmissionService _submissionService;
    private readonly IExplanationOfBenefitProjector _eobProjector;
    private readonly IClaimImportTransactionRepository _importTransactions;
    private readonly ICurrentActor _actor;
    private readonly ILogger<ClaimsV1Controller> _logger;
    private readonly int _raw837MaxConcurrency;
    private readonly Snip.ISnip837Validator _snipValidator;
    private readonly Snip.Snip837ValidationOptions _snipOptions;

    public ClaimsV1Controller(
        ClaimAdapterFactory adapterFactory,
        IClaimSubmissionService submissionService,
        IExplanationOfBenefitProjector eobProjector,
        IClaimImportTransactionRepository importTransactions,
        IConfiguration configuration,
        ICurrentActor actor,
        ILogger<ClaimsV1Controller> logger,
        Snip.ISnip837Validator? snipValidator = null,
        Microsoft.Extensions.Options.IOptions<Snip.Snip837ValidationOptions>? snipOptions = null)
    {
        _snipOptions = snipOptions?.Value ?? new Snip.Snip837ValidationOptions();
        _snipValidator = snipValidator ?? new Snip.X12837SnipValidator(_snipOptions);
        _adapterFactory = adapterFactory;
        _submissionService = submissionService;
        _eobProjector = eobProjector;
        _importTransactions = importTransactions;
        _actor = actor;
        _logger = logger;
        _raw837MaxConcurrency = Math.Clamp(
            configuration.GetValue("ClaimsImport:Raw837MaxConcurrency", 32),
            1,
            64);
    }

    /// <summary>
    /// Submit a new claim through the canonical V1 surface. Accepts
    /// <see cref="AdapterClaim"/> directly so the wire shape stays
    /// stable as the internal <see cref="Claim"/> domain model
    /// evolves. On success, emits a <c>ClaimVersionSubmitted</c>
    /// audit event to the Mongo append-only stream.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(AdapterClaim), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<IActionResult> SubmitClaim(
        [FromBody] AdapterClaim claim,
        CancellationToken ct = default)
    {
        if (claim is null)
        {
            return BadRequest(new { error = "Request body is required" });
        }

        var tenantId = GetTenantId();

        _logger.LogInformation(
            "v1 claim submission: member={Member}, provider={Provider}, lines={LineCount}",
            SanitizeForLog(claim.MemberId), SanitizeForLog(claim.BillingProviderNPI),
            claim.ClaimLines?.Count ?? 0);

        var actorId = ResolveActorId();
        var correlationId = ResolveCorrelationId();

        // Lifecycle, adjudication and audit fields on the body are server-owned.
        ClaimSubmissionInput.ResetServerOwnedFields(claim, actorId);

        var result = await _submissionService.SubmitAsync(
            claim, tenantId, actorId, correlationId, ct);

        if (!result.Success)
        {
            return MapFailure(result);
        }

        var created = result.Claim!;
        return CreatedAtAction(
            nameof(SearchMemberClaims),
            new { memberId = created.MemberId },
            created);
    }

    /// <summary>
    /// Accepts a raw X12 837 EDI file (professional or institutional,
    /// single claim or a multi-claim batch), parses it, maps each parsed
    /// claim onto <see cref="AdapterClaim"/>, and submits each through the
    /// same <see cref="IClaimSubmissionService"/> every other claim on
    /// this surface goes through — the on-ramp for evaluators dropping in
    /// their own 837 file rather than calling <c>POST /api/v1/claims</c>
    /// with an already-structured payload one claim at a time.
    /// </summary>
    [HttpPost("import/raw837")]
    [RequestSizeLimit(20_000_000)]
    [ProducesResponseType(typeof(Raw837ImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Raw837ImportResult>> ImportRaw837(
        [FromForm] IFormFile file,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "A non-empty 837 file is required." });
        }

        string ediContent;
        using (var reader = new StreamReader(file.OpenReadStream()))
        {
            ediContent = await reader.ReadToEndAsync(ct);
        }

        // WEDI SNIP 1–5 runs before anything is parsed or mapped. Claims in a
        // transaction set the validation rejects are reported (and logged as
        // rejected imports) but never submitted.
        Snip.SnipValidationResult? snip = null;
        string? acknowledgment = null;
        string? acknowledgmentControl = null;
        List<ParsedClaim> parsedClaims;
        try
        {
            if (_snipOptions.Enabled)
            {
                snip = _snipValidator.Validate(ediContent);
                var control = Random.Shared.NextInt64(1, 1_000_000_000);
                acknowledgment = Snip.X12999AcknowledgmentBuilder.Build(snip, new Snip.X12999AcknowledgmentBuilder.Options
                {
                    ControlNumber = control,
                });
                acknowledgmentControl = acknowledgment is null ? null : control.ToString("D9", System.Globalization.CultureInfo.InvariantCulture);

                if (snip.Document is null || !snip.FunctionalGroups.Any())
                {
                    _logger.LogWarning("Uploaded 837 file {FileName} is not readable X12", SanitizeForLog(file.FileName));
                    return BadRequest(SnipFailure(file.FileName, "Could not parse 837 file: the file is not a readable X12 interchange.", snip, acknowledgment));
                }

                parsedClaims = ClaimsBySnipOutcome(snip);
                LogSnipLevel2Warnings(snip, GetTenantId());
            }
            else
            {
                parsedClaims = ClaimsService.EDI.Inbound.X12837Parser.Parse(ediContent)
                    .Select(c => new ParsedClaim(c, null, null, null))
                    .ToList();
            }
        }
        catch (ClaimsService.EDI.Inbound.X12FormatException ex)
        {
            _logger.LogWarning(ex, "Failed to parse uploaded 837 file {FileName}", SanitizeForLog(file.FileName));
            return BadRequest(new { error = $"Could not parse 837 file: {ex.Message}" });
        }

        if (parsedClaims.Count == 0)
        {
            const string noClaims = "No CLM (claim) segments found in the uploaded file.";
            return snip is null
                ? BadRequest(new { error = noClaims })
                : BadRequest(SnipFailure(file.FileName, noClaims, snip, acknowledgment));
        }

        var tenantId = GetTenantId();
        var actorId = ResolveActorId();
        var correlationId = ResolveCorrelationId();

        _logger.LogInformation(
            "Parsed uploaded 837 file {FileName} for tenant {TenantId}: {Count} claim(s), {Rejected} rejected by SNIP validation ({Ack}), submitting with max concurrency {MaxConcurrency}",
            SanitizeForLog(file.FileName), SanitizeForLog(tenantId), parsedClaims.Count,
            parsedClaims.Count(c => c.SnipErrors is not null), snip?.AcknowledgmentCode ?? "n/a", _raw837MaxConcurrency);

        var results = new Raw837ClaimResult[parsedClaims.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, parsedClaims.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _raw837MaxConcurrency,
                CancellationToken = ct
            },
            async (index, cancellationToken) =>
        {
            var (parsed, snipErrors, snipSet, snipGroup) = parsedClaims[index];
            var adapterClaim = ClaimsService.EDI.Inbound.X12837ClaimMapper.Map(parsed, tenantId);
            ClaimSubmissionResult? result = null;
            if (snipErrors is null)
            {
                result = await _submissionService.SubmitAsync(
                    adapterClaim,
                    tenantId,
                    actorId,
                    correlationId,
                    cancellationToken);
            }

            var errors = snipErrors
                ?? (result!.Success
                    ? []
                    : result.Errors.Select(e => $"{e.Field}: {e.Message}").ToList());
            var success = result?.Success == true;

            results[index] = new Raw837ClaimResult
            {
                ClaimNumber = parsed.ClaimId,
                Success = success,
                ClaimId = result?.Claim?.Id,
                Errors = errors
            };

            // Persisted regardless of outcome — a rejected claim is exactly
            // what an evaluator troubleshooting a dropped file needs to see.
            try
            {
                await _importTransactions.CreateAsync(new ClaimImportTransaction
                {
                    TenantId = tenantId,
                    ClaimNumber = parsed.ClaimId,
                    ClaimId = result?.Claim?.Id,
                    MemberId = adapterClaim.MemberId,
                    FileName = file.FileName,
                    Status = success ? "Accepted" : "Rejected",
                    Errors = errors,
                    TransactionSetControlNumber = snipSet?.ControlNumber,
                    AcknowledgmentCode = snipSet?.AcknowledgmentCode,
                    Acknowledgment999ControlNumber = acknowledgmentControl,
                    SubmitterQualifier = snipGroup?.Interchange.SenderQualifier,
                    SubmitterId = snipGroup?.Interchange.SenderId,
                    ApplicationSenderCode = snipGroup?.ApplicationSenderCode?.Trim(),
                    SubmitterIdNormalized = NormalizeSubmitter(snipGroup?.Interchange.SenderId),
                    ApplicationSenderCodeNormalized = NormalizeSubmitter(snipGroup?.ApplicationSenderCode),
                    InterchangeControlNumber = snipGroup?.Interchange.ControlNumber,
                    GroupControlNumber = snipGroup?.ControlNumber,
                    SnipPartnerOverride = snipSet?.PartnerOverrideKey,
                    SnipWarnings = snipSet is null || snip?.Document is null
                        ? []
                        : SnipWarningsFor(snip.Document, snipSet, parsed.ClaimId),
                });
            }
            catch (Exception ex)
            {
                // Transaction-log failures must not break the import — log
                // and move on, same posture as enrollment-import-service's
                // RecordTransactionAsync.
                _logger.LogWarning(ex,
                    "Failed to persist ClaimImportTransaction for claim {ClaimNumber}",
                    SanitizeForLog(parsed.ClaimId));
            }
        });

        return Ok(new Raw837ImportResult
        {
            FileName = file.FileName,
            TotalClaims = parsedClaims.Count,
            SucceededCount = results.Count(r => r.Success),
            Results = results.ToList(),
            AcknowledgmentCode = snip?.AcknowledgmentCode,
            Acknowledgment999 = acknowledgment,
            SnipIssues = snip?.AllIssues.ToList() ?? [],
        });
    }

    /// <summary>
    /// Runs WEDI SNIP 1–5 validation on a raw 837 without submitting
    /// anything, and returns the findings and the 999 acknowledgment.
    /// Uses the same per-level Reject/Warn configuration as the import.
    /// </summary>
    [HttpPost("import/raw837/validate")]
    [RequestSizeLimit(20_000_000)]
    [ProducesResponseType(typeof(Raw837ImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Raw837ImportResult>> ValidateRaw837(
        [FromForm] IFormFile file,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "A non-empty 837 file is required." });
        }

        string ediContent;
        using (var reader = new StreamReader(file.OpenReadStream()))
        {
            ediContent = await reader.ReadToEndAsync(ct);
        }

        var snip = _snipValidator.Validate(ediContent);
        var acknowledgment = Snip.X12999AcknowledgmentBuilder.Build(snip, new Snip.X12999AcknowledgmentBuilder.Options
        {
            ControlNumber = Random.Shared.NextInt64(1, 1_000_000_000),
        });

        return Ok(new Raw837ImportResult
        {
            FileName = file.FileName,
            AcknowledgmentCode = snip.AcknowledgmentCode,
            Acknowledgment999 = acknowledgment,
            SnipIssues = snip.AllIssues.ToList(),
        });
    }

    /// <summary>
    /// Parses each transaction set on its own so every claim is tied to the
    /// SNIP outcome of the set it came from. Claims in a rejected set carry
    /// that set's error messages (the claim's own first, then set-level ones).
    /// </summary>
    private static List<ParsedClaim> ClaimsBySnipOutcome(Snip.SnipValidationResult snip)
    {
        var doc = snip.Document!;
        var accepted = snip.AcceptedTransactionSets.ToHashSet();
        var envelopeErrors = snip.EnvelopeIssues
            .Where(i => i.Severity == Snip.SnipSeverity.Error)
            .Select(i => $"SNIP {(int)i.Level} {i.RuleId}: {i.Message}")
            .ToList();
        var claims = new List<ParsedClaim>();

        foreach (var (ts, group) in snip.FunctionalGroups.SelectMany(g => g.TransactionSets.Select(t => (t, g))))
        {
            var segments = new List<ClaimsService.EDI.Inbound.X12Segment>();
            if (doc.Segments[ts.InterchangeSegmentIndex].Id == "ISA")
                segments.Add(doc.Segments[ts.InterchangeSegmentIndex]);
            var last = Math.Min(ts.EndSegmentIndex, doc.Segments.Count - 1);
            for (var i = ts.StartSegmentIndex; i <= last; i++)
            {
                if (i > ts.StartSegmentIndex && doc.Segments[i].Id is "ST" or "GE" or "IEA" or "GS" or "ISA") break;
                segments.Add(doc.Segments[i]);
            }

            var parsed = ClaimsService.EDI.Inbound.X12837Parser.Parse(new ClaimsService.EDI.Inbound.X12Document
            {
                Segments = segments,
                ElementSeparator = doc.ElementSeparator,
                ComponentSeparator = doc.ComponentSeparator,
            });

            foreach (var claim in parsed)
            {
                if (accepted.Contains(ts))
                {
                    claims.Add(new ParsedClaim(claim, null, ts, group));
                    continue;
                }

                var errors = ts.Issues
                    .Where(i => i.Severity == Snip.SnipSeverity.Error)
                    .OrderByDescending(i => i.ClaimId == claim.ClaimId)
                    .Where(i => i.ClaimId is null || i.ClaimId == claim.ClaimId)
                    .Select(i => $"SNIP {(int)i.Level} {i.RuleId}: {i.Message}")
                    .Concat(envelopeErrors)
                    .Take(20)
                    .ToList();
                if (errors.Count == 0)
                    errors.Add("SNIP: the transaction set containing this claim was rejected.");
                claims.Add(new ParsedClaim(claim, errors, ts, group));
            }
        }

        return claims;
    }

    /// <summary>A parsed claim, the SNIP errors that keep it from being submitted (null = submit), its transaction set and functional group.</summary>
    private sealed record ParsedClaim(
        CloudHealthOffice.ClaimsScrubEngine.Models.X12837Claim Claim,
        List<string>? SnipErrors,
        Snip.SnipTransactionSetOutcome? Set,
        Snip.SnipFunctionalGroupOutcome? Group);

    internal static readonly EventId SnipLevel2WarningEvent = new(8372, "SnipLevel2Warning");

    /// <summary>
    /// One structured log entry per SNIP Level 2 finding reported at Warn:
    /// submitter (ISA05/ISA06, GS02), control numbers (ISA13, GS06, ST02,
    /// CLM01), location (loop, segment and qualifier, element) and rule id.
    /// No message text and no member data; the durable counterpart is
    /// <see cref="ClaimImportTransaction.SnipWarnings"/>.
    /// </summary>
    private void LogSnipLevel2Warnings(Snip.SnipValidationResult snip, string tenantId)
    {
        var doc = snip.Document;
        if (doc is null) return;
        foreach (var group in snip.FunctionalGroups)
        {
            foreach (var ts in group.TransactionSets)
            {
                foreach (var issue in ts.Issues)
                {
                    if (issue.Level != Snip.SnipLevel.ImplementationGuide || issue.Severity != Snip.SnipSeverity.Warning)
                        continue;
                    _logger.LogWarning(SnipLevel2WarningEvent,
                        "SNIP Level 2 warning {RuleId} at {Location}; transaction set accepted: {SetAccepted}; " +
                        "submitter ISA05/ISA06 {SubmitterQualifier}/{SubmitterId}, GS02 {ApplicationSenderCode}; " +
                        "ISA13 {InterchangeControlNumber}, GS06 {GroupControlNumber}, ST02 {TransactionSetControlNumber}, CLM01 {ClaimNumber}; " +
                        "tenant {TenantId}; SNIP policy {SnipPolicy}",
                        issue.RuleId, SnipLocation(doc, ts, issue), ts.Accepted,
                        SanitizeForLog(group.Interchange.SenderQualifier), SanitizeForLog(group.Interchange.SenderId),
                        SanitizeForLog(group.ApplicationSenderCode),
                        SanitizeForLog(group.Interchange.ControlNumber), SanitizeForLog(group.ControlNumber),
                        SanitizeForLog(ts.ControlNumber), SanitizeForLog(issue.ClaimId),
                        SanitizeForLog(tenantId),
                        ts.PartnerOverrideKey is null ? "default" : "partner:" + SanitizeForLog(ts.PartnerOverrideKey));
                }
            }
        }
    }

    /// <summary>Warn-level findings of a set that concern the given claim (its own and set-level ones).</summary>
    private static List<SnipFindingRecord> SnipWarningsFor(
        ClaimsService.EDI.Inbound.X12Document doc, Snip.SnipTransactionSetOutcome ts, string? claimId) =>
        ts.Issues
            .Where(i => i.Severity == Snip.SnipSeverity.Warning && i.RuleId != "L1-TOO-MANY-FINDINGS")
            .Where(i => i.ClaimId is null || i.ClaimId == claimId)
            .Select(i => new SnipFindingRecord
            {
                Level = (int)i.Level,
                RuleId = i.RuleId,
                Severity = i.Severity.ToString(),
                Message = i.Level == Snip.SnipLevel.ImplementationGuide ? i.Message : null,
                TransactionSetControlNumber = ts.ControlNumber,
                ClaimLevel = i.ClaimId is not null,
                Loop = i.Loop,
                SegmentId = i.SegmentId,
                SegmentPosition = i.SegmentPosition,
                ElementPosition = i.ElementPosition,
                ComponentPosition = i.ComponentPosition,
                DataElementReference = i.DataElementReference,
                Location = SnipLocation(doc, ts, i),
            })
            .ToList();

    // Segments whose first element is a qualifier code worth echoing in a location (DTP*472, REF*F8, NM1*85 ...).
    private static readonly HashSet<string> QualifiedSegments = ["DTP", "REF", "NM1", "AMT", "QTY", "PRV", "PWK"];

    /// <summary>
    /// Finding location: loop, segment (with its qualifier code for
    /// qualified segments) and element, e.g. "2300 DTP*472", "2300 CLM05-2",
    /// "2400 DTP*472 DTP03". Only a 1-3 character alphanumeric qualifier
    /// code is read from the file; never names, ids or dates.
    /// </summary>
    internal static string SnipLocation(ClaimsService.EDI.Inbound.X12Document doc, Snip.SnipTransactionSetOutcome ts, Snip.SnipIssue issue)
    {
        var segmentId = issue.SegmentId ?? "???";
        var location = segmentId;
        if (issue.SegmentPosition is { } pos && QualifiedSegments.Contains(segmentId))
        {
            var index = ts.StartSegmentIndex + pos - 1;
            if (index >= 0 && index < doc.Segments.Count && doc.Segments[index].Id == segmentId
                && doc.Segments[index].Element(0) is { Length: >= 1 and <= 3 } qualifier
                && qualifier.All(char.IsAsciiLetterOrDigit))
                location = $"{segmentId}*{qualifier}";
        }

        if (issue.ElementPosition is { } element)
        {
            var reference = issue.ComponentPosition is { } component
                ? $"{segmentId}{element:00}-{component}"
                : $"{segmentId}{element:00}";
            location = location == segmentId ? reference : $"{location} {reference}";
        }

        return string.IsNullOrEmpty(issue.Loop) ? location : $"{issue.Loop} {location}";
    }

    private static Raw837ImportResult SnipFailure(
        string fileName, string error, Snip.SnipValidationResult snip, string? acknowledgment) => new()
    {
        FileName = fileName,
        Error = error,
        AcknowledgmentCode = snip.AcknowledgmentCode,
        Acknowledgment999 = acknowledgment,
        SnipIssues = snip.AllIssues.ToList(),
    };

    /// <summary>
    /// Most recent 837 import transactions for the tenant, newest first —
    /// the admin-console read path (mirrors enrollment-import-service's
    /// <c>GET /api/v1/enrollment/transactions/recent</c>). Includes
    /// rejected imports, not just successful ones.
    /// </summary>
    [HttpGet("import-transactions")]
    [ProducesResponseType(typeof(List<ClaimImportTransaction>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListImportTransactions([FromQuery] int limit = 100)
    {
        var tenantId = GetTenantId();
        if (limit < 1 || limit > 500) limit = 100;

        var transactions = await _importTransactions.ListRecentAsync(tenantId, limit);
        return Ok(transactions);
    }

    /// <summary>
    /// 837 import transactions that carried a SNIP warning (accepted with
    /// errors), newest first, optionally narrowed to a rule id (e.g.
    /// <c>L2-2300-DTP472</c>), a SNIP level and/or a submitter (ISA06 or
    /// GS02). The ops view for deciding which partner can be moved from Warn
    /// to Reject (<c>ClaimsImport:Snip:PartnerOverrides</c>).
    /// </summary>
    [HttpGet("import-transactions/snip-warnings")]
    [ProducesResponseType(typeof(List<ClaimImportTransaction>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSnipWarningTransactions(
        [FromQuery] string? ruleId = null,
        [FromQuery] int? level = null,
        [FromQuery] string? submitterId = null,
        [FromQuery] int limit = 100)
    {
        var tenantId = GetTenantId();
        if (limit < 1 || limit > 500) limit = 100;

        var transactions = await _importTransactions.ListWithSnipWarningsAsync(
            tenantId,
            string.IsNullOrWhiteSpace(ruleId) ? null : ruleId.Trim(),
            level,
            NormalizeSubmitter(submitterId),
            limit);
        return Ok(transactions);
    }

    /// <summary>
    /// Search claims for a member. Returns a small wrapper
    /// <c>{ total, page, pageSize, resources[] }</c> where <c>resources</c>
    /// is a FHIR <c>ExplanationOfBenefit</c> array (one per matching
    /// claim).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(EobSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<EobSearchResponse>> SearchMemberClaims(
        [FromQuery, Required] string memberId,
        [FromQuery] DateTime? serviceDateFrom = null,
        [FromQuery] DateTime? serviceDateTo = null,
        [FromQuery] ClaimStatus? status = null,
        [FromQuery] string? providerNPI = null,
        [FromQuery] ClaimType? claimType = null,
        [FromQuery] decimal? amountMin = null,
        [FromQuery] decimal? amountMax = null,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(memberId))
            return BadRequest(new { error = "memberId is required" });

        var tenantId = GetTenantId();

        _logger.LogInformation(
            "v1 claims member search: member={Member}, status={Status}, type={Type}, amount=[{Min},{Max}]",
            SanitizeForLog(memberId), status, claimType, amountMin, amountMax);

        var adapter = await _adapterFactory.GetAdapterAsync(tenantId, ct);
        var adapterResponse = await adapter.SearchClaimsForMemberAsync(
            new ClaimMemberSearchAdapterRequest
            {
                TenantId = tenantId,
                MemberId = memberId,
                ServiceDateFrom = serviceDateFrom,
                ServiceDateTo = serviceDateTo,
                Status = status,
                ProviderNPI = providerNPI,
                ClaimType = claimType,
                AmountMin = amountMin,
                AmountMax = amountMax,
                Page = page,
                PageSize = pageSize,
            },
            ct);

        var resources = new JsonArray();
        foreach (var adapterClaim in adapterResponse.Claims)
        {
            // Round-trip AdapterClaim → Claim for the existing projector
            // contract. The 5.2 mapper is loss-less per
            // SubmitClaimAsync_round_trips_AdapterClaim_losslessly.
            // Capability 5.11 may evolve the projector to consume
            // AdapterClaim directly; that's 5.11's scope.
            resources.Add(_eobProjector.Project(adapterClaim.ToClaim()));
        }

        // Adapters that don't surface a TotalCount fall back to the page
        // size — defensive, only reachable for vendor adapters that
        // currently throw NotImplementedException on this method anyway.
        var total = adapterResponse.TotalCount ?? adapterResponse.Claims.Count;

        return Ok(new EobSearchResponse
        {
            Total = total,
            Page = page,
            PageSize = pageSize,
            Resources = resources
        });
    }

    private IActionResult MapFailure(ClaimSubmissionResult result)
    {
        var errors = result.Errors.Select(e => new
        {
            field = e.Field,
            code = e.Code,
            message = e.Message
        });

        return result.FailureKind switch
        {
            ClaimSubmissionFailureKind.NotImplemented => StatusCode(
                StatusCodes.Status501NotImplemented,
                new
                {
                    error = "Claim submission is not implemented for this tenant's configured platform",
                    errors
                }),
            _ => BadRequest(new
            {
                error = "Claim submission validation failed",
                errors
            }),
        };
    }

    private string GetTenantId()
    {
        var tenantId = HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new InvalidOperationException(
                "TenantId not found in HttpContext. Ensure tenant middleware is configured.");
        }
        return tenantId;
    }

    /// <summary>The acting user or service, from the validated token only.</summary>
    private string ResolveActorId() => _actor.UserId;

    private string? ResolveCorrelationId()
    {
        if (HttpContext.Request.Headers.TryGetValue("X-Correlation-Id", out var header) &&
            !string.IsNullOrEmpty(header.ToString()))
        {
            return header.ToString();
        }
        return Activity.Current?.Id;
    }

    /// <summary>Submitter ids (ISA06, GS02) as stored for lookup: trimmed, upper-case, null when blank.</summary>
    internal static string? NormalizeSubmitter(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

/// <summary>
/// Wrapper for FHIR ExplanationOfBenefit search results. Kept intentionally
/// small — pagination metadata plus the FHIR resource array as a JsonNode so
/// we can project without taking on the Hl7.Fhir.R4 transitive dep graph.
/// </summary>
public class EobSearchResponse
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public JsonArray Resources { get; set; } = new();
}

/// <summary>Per-file result of a raw 837 upload — one entry per CLM segment found, in order.</summary>
public class Raw837ImportResult
{
    public string FileName { get; set; } = string.Empty;
    public int TotalClaims { get; set; }
    public int SucceededCount { get; set; }
    public List<Raw837ClaimResult> Results { get; set; } = [];

    /// <summary>Set when the file as a whole could not be imported.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// SNIP outcome for the file, as in 999 AK9: A accepted, E accepted with
    /// errors, P partially accepted (some transaction sets rejected), R rejected.
    /// Null when SNIP validation is disabled.
    /// </summary>
    public string? AcknowledgmentCode { get; set; }

    /// <summary>The X12 999 acknowledgment (005010X231A1) for the file.</summary>
    public string? Acknowledgment999 { get; set; }

    /// <summary>Every SNIP finding, with level, loop/segment/element position and message.</summary>
    public List<ClaimsService.EDI.Validation.SnipIssue> SnipIssues { get; set; } = [];
}

public class Raw837ClaimResult
{
    public string ClaimNumber { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? ClaimId { get; set; }
    public List<string> Errors { get; set; } = [];
}
