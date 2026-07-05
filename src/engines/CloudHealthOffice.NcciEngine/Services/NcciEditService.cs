using CloudHealthOffice.NcciEngine.Domain;
using CloudHealthOffice.NcciEngine.Models;
using CloudHealthOffice.NcciEngine.Persistence;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.NcciEngine.Services;

/// <summary>
/// CHO-native NCCI/MUE editing service.
///
/// ── How it works ─────────────────────────────────────────────────
///
/// NCCI Column 1/Column 2 edit (Rule NE001)
///   1. For every unique ordered pair of procedure codes that appear
///      on the same claim on the same date of service, we look up
///      whether a bundling edit exists (Column1 = higher-ranked code,
///      Column2 = component code).
///   2. If an edit exists and ModifierIndicator = 0, the Column 2
///      line is denied — modifier cannot override.
///   3. If ModifierIndicator = 1, we check whether a -59, XE, XS,
///      XP, or XU modifier appears on the Column 2 line.  If present,
///      the edit is informational only (pair is allowed to be billed
///      separately); otherwise the Column 2 line is denied.
///
/// MUE Maximum Units (Rule NE002)
///   For MAI 1: each line is checked independently.
///   For MAI 2/3: units for the same code on the same date are summed
///   across all lines before comparing to MaxUnits.
///
/// ── Suggested CARC/RARC codes ────────────────────────────────────
///   NCCI bundling (MI=0):  CARC 97 + RARC N519
///   NCCI bundling (MI=1, no override modifier): CARC B20 + RARC N519
///   MUE exceeded:          CARC 151 + RARC N115
/// </summary>
internal class NcciEditService : INcciEditService
{
    // Modifiers that can override a ModifierIndicator=1 bundling edit
    private static readonly HashSet<string> DistinctProcedureModifiers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "59",  // Distinct Procedural Service (legacy)
            "XE",  // Separate encounter
            "XS",  // Separate structure
            "XP",  // Separate practitioner
            "XU",  // Unusual non-overlapping service
        };

    private readonly INcciRepository _repository;
    private readonly NcciLookupCache _lookupCache;
    private readonly ILogger<NcciEditService> _logger;

    public NcciEditService(
        INcciRepository repository,
        ILogger<NcciEditService> logger,
        NcciLookupCache? lookupCache = null)
    {
        _repository = repository;
        _lookupCache = lookupCache ?? new NcciLookupCache();
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════
    // INcciEditService
    // ═══════════════════════════════════════════════════════════════

    public async Task<NcciScrubResult> ScrubAsync(NcciScrubRequest request, CancellationToken ct = default)
    {
        var effectiveDate = request.EffectiveDate
            ?? (request.ServiceLines.Count > 0
                ? request.ServiceLines.Min(l => l.ServiceDate)
                : DateOnly.FromDateTime(DateTime.UtcNow));

        var result = new NcciScrubResult { ClaimId = request.ClaimId };

        await ApplyNcciPairEdits(request, effectiveDate, result, ct);
        await ApplyMueEdits(request, effectiveDate, result, ct);

        _logger.LogDebug(
            "NCCI scrub for claim {ClaimId}: {PairChecks} pair checks, {MueChecks} MUE checks, {Failures} failures",
            SanitizeForLog(request.ClaimId), result.NcciPairsChecked, result.MueChecked, result.EditFailures.Count);

        return result;
    }

    public Task<NcciTableVersion?> GetTableVersionAsync(string tenantId, CancellationToken ct = default)
        => _repository.GetCurrentVersionAsync(tenantId, ct);

    public async Task<(int NcciPairsWritten, int MueEntriesWritten)> ImportQuarterlyUpdateAsync(
        string tenantId,
        string quarter,
        IReadOnlyList<NcciEditPair> pairs,
        IReadOnlyList<MueEntry> entries,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Importing NCCI quarterly update for tenant {TenantId}, quarter {Quarter}: " +
            "{PairCount} pairs, {MueCount} MUE entries",
            SanitizeForLog(tenantId), SanitizeForLog(quarter), pairs.Count, entries.Count);

        var (pairsWritten, mueWritten) = await _repository.UpsertQuarterAsync(
            tenantId, quarter, pairs, entries, ct);

        _lookupCache.InvalidateTenant(tenantId);

        _logger.LogInformation(
            "NCCI import complete for {Quarter}: {PairsWritten} pairs, {MueWritten} MUE entries written",
            SanitizeForLog(quarter), pairsWritten, mueWritten);

        return (pairsWritten, mueWritten);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }

    private static string NormalizeCode(string code) => code.Trim().ToUpperInvariant();

    // ═══════════════════════════════════════════════════════════════
    // PRIVATE — NCCI PAIR EDITS (NE001)
    // ═══════════════════════════════════════════════════════════════

    private async Task ApplyNcciPairEdits(
        NcciScrubRequest request,
        DateOnly effectiveDate,
        NcciScrubResult result,
        CancellationToken ct)
    {
        // Group lines by service date for same-date-of-service comparisons
        var byDate = request.ServiceLines
            .GroupBy(l => l.ServiceDate)
            .ToList();

        foreach (var group in byDate)
        {
            var lines = group.ToList();

            // Check every unordered pair of lines on the same DOS
            for (int i = 0; i < lines.Count; i++)
            {
                for (int j = i + 1; j < lines.Count; j++)
                {
                    var lineA = lines[i];
                    var lineB = lines[j];

                    var codeA = NormalizeCode(lineA.ProcedureCode);
                    var codeB = NormalizeCode(lineB.ProcedureCode);

                    // Look up both orderings: A/B and B/A
                    var editAB = await _lookupCache.GetEditPairAsync(
                        request.TenantId,
                        codeA,
                        codeB,
                        effectiveDate,
                        lookupCt => _repository.GetEditPairAsync(
                            request.TenantId,
                            codeA,
                            codeB,
                            effectiveDate,
                            lookupCt),
                        ct);

                    var editBA = await _lookupCache.GetEditPairAsync(
                        request.TenantId,
                        codeB,
                        codeA,
                        effectiveDate,
                        lookupCt => _repository.GetEditPairAsync(
                            request.TenantId,
                            codeB,
                            codeA,
                            effectiveDate,
                            lookupCt),
                        ct);

                    result.NcciPairsChecked++;

                    // Process whichever ordering matched
                    if (editAB is not null)
                        EvaluatePairEdit(editAB, col1Line: lineA, col2Line: lineB, result);
                    else if (editBA is not null)
                        EvaluatePairEdit(editBA, col1Line: lineB, col2Line: lineA, result);
                }
            }
        }
    }

    private static void EvaluatePairEdit(
        NcciEditPair edit,
        ClaimServiceLine col1Line,
        ClaimServiceLine col2Line,
        NcciScrubResult result)
    {
        if (edit.ModifierIndicator == NcciModifierIndicator.NotApplicable)
            return; // retired/informational pair — no action

        bool overridePresent = edit.ModifierIndicator == NcciModifierIndicator.Allowed
            && col2Line.Modifiers.Any(m => DistinctProcedureModifiers.Contains(m));

        if (overridePresent)
        {
            // -59/X modifier on Column 2 line — edit is overridden; bill as separate services
            return;
        }

        // Determine denial context
        bool absoluteBundling = edit.ModifierIndicator == NcciModifierIndicator.NotAllowed;

        var (carc, rarc, message) = absoluteBundling
            ? ("97", "N519",
               $"Procedure {col2Line.ProcedureCode} is a component of {col1Line.ProcedureCode} " +
               $"and is not separately payable (NCCI Modifier Indicator 0).")
            : ("B20", "N519",
               $"Procedure {col2Line.ProcedureCode} is bundled into {col1Line.ProcedureCode}. " +
               $"A -59/X modifier on line {col2Line.LineNumber} is required to bill separately.");

        result.EditFailures.Add(new NcciEditFailure
        {
            EditType = NcciEditType.NcciPair,
            RuleId = "NE001",
            Message = message,
            Column1Code = col1Line.ProcedureCode,
            Column2Code = col2Line.ProcedureCode,
            AffectedLineNumbers = [col2Line.LineNumber],
            ModifierOverridePresent = false,
            SuggestedCarc = carc,
            SuggestedRarc = rarc,
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // PRIVATE — MUE EDITS (NE002)
    // ═══════════════════════════════════════════════════════════════

    private async Task ApplyMueEdits(
        NcciScrubRequest request,
        DateOnly effectiveDate,
        NcciScrubResult result,
        CancellationToken ct)
    {
        // Determine the POS type for the whole claim (professional vs facility)
        bool isProfessional = request.ClaimType == "837P";

        // Group by (ProcedureCode, ServiceDate) — unit aggregation is per code per DOS
        var groups = request.ServiceLines
            .GroupBy(l => (l.ProcedureCode, l.ServiceDate))
            .ToList();

        foreach (var group in groups)
        {
            var (code, dos) = group.Key;
            var lines = group.ToList();

            var normalizedCode = NormalizeCode(code);
            var mue = await _lookupCache.GetMueEntryAsync(
                request.TenantId,
                normalizedCode,
                effectiveDate,
                lookupCt => _repository.GetMueEntryAsync(request.TenantId, normalizedCode, effectiveDate, lookupCt),
                ct);

            result.MueChecked++;

            if (mue is null) continue;

            // Skip if MUE does not apply to this setting
            if (isProfessional && !mue.AppliesToProfessional) continue;
            if (!isProfessional && !mue.AppliesToOutpatientFacility) continue;

            switch (mue.AdjudicationIndicator)
            {
                case MueAdjudicationIndicator.ClaimLine:
                    // MAI 1: each line checked independently
                    foreach (var line in lines)
                    {
                        if (line.Units > mue.MaxUnits)
                        {
                            result.EditFailures.Add(BuildMueFailure(
                                line.Units, mue, [line.LineNumber], code, dos));
                        }
                    }
                    break;

                case MueAdjudicationIndicator.DateOfService:
                case MueAdjudicationIndicator.DateOfServiceAbsolute:
                    // MAI 2/3: sum units across all lines for this code + DOS
                    var totalUnits = lines.Sum(l => l.Units);
                    if (totalUnits > mue.MaxUnits)
                    {
                        result.EditFailures.Add(BuildMueFailure(
                            totalUnits, mue, lines.Select(l => l.LineNumber).ToList(), code, dos));
                    }
                    break;
            }
        }
    }

    private static NcciEditFailure BuildMueFailure(
        decimal unitsBilled, MueEntry mue, List<int> lineNumbers, string code, DateOnly dos)
    {
        return new NcciEditFailure
        {
            EditType = NcciEditType.Mue,
            RuleId = "NE002",
            Message = $"Procedure {code} on {dos:yyyy-MM-dd} billed {unitsBilled} units " +
                      $"but the MUE limit is {mue.MaxUnits} unit(s) per day " +
                      $"(MAI {(int)mue.AdjudicationIndicator}).",
            Column2Code = code,
            AffectedLineNumbers = lineNumbers,
            UnitsBilled = unitsBilled,
            MueMaxUnits = mue.MaxUnits,
            SuggestedCarc = "151",
            SuggestedRarc = "N115",
        };
    }
}
