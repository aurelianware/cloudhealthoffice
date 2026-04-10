using Microsoft.Extensions.Logging;
using CHO.TmppmIngestionService.Loaders;
using CHO.TmppmIngestionService.Models;
using CHO.TmppmIngestionService.Parsers;

namespace CHO.TmppmIngestionService.Services;

/// <summary>
/// Orchestrates the TMPPM ingestion pipeline:
/// 1. Download TMPPM edition PDFs from TMHP
/// 2. Detect changed chapters via SHA256
/// 3. Parse changed chapters for PA rules
/// 4. Generate diff report against previous edition
/// 5. Persist rules + publish as ConceptMapEntry overrides
/// </summary>
public class IngestionPipeline(
    TmhpChapterDownloader downloader,
    TmppmPdfParser parser,
    TmppmRuleStore store,
    ILogger<IngestionPipeline> logger)
{
    /// <summary>
    /// Run full ingestion for a specific edition.
    /// </summary>
    public async Task<IngestionResult> RunAsync(int year, int month, string? tenantId = null)
    {
        var result = new IngestionResult { EditionId = $"{year}-{month:D2}" };
        var outputDir = Path.Combine("tmppm-data", result.EditionId);

        logger.LogInformation("═══ TMPPM Ingestion Pipeline: {Edition} ═══", result.EditionId);

        // Step 1: Download
        logger.LogInformation("Step 1: Downloading TMPPM chapters...");
        var edition = await downloader.DownloadEditionAsync(year, month, outputDir);
        result.ChaptersDownloaded = edition.Chapters.Count;

        // Step 2: Detect changes
        logger.LogInformation("Step 2: Detecting changes...");
        var previousEdition = await store.GetLatestEditionAsync();
        var changedChapters = previousEdition != null
            ? downloader.DetectChangedChapters(previousEdition, edition)
            : edition.Chapters.Select(c => c.ChapterId).ToList();
        result.ChaptersChanged = changedChapters.Count;

        // Step 3: Parse changed chapters
        logger.LogInformation("Step 3: Parsing {Count} changed chapters...", changedChapters.Count);
        var allRules = new List<TmppmPaRule>();

        foreach (var chapterId in changedChapters)
        {
            var chapter = edition.Chapters.First(c => c.ChapterId == chapterId);
            var pdfPath = Path.Combine(outputDir, chapter.PdfFileName);

            if (!File.Exists(pdfPath))
            {
                logger.LogWarning("PDF not found: {Path}", pdfPath);
                continue;
            }

            var pages = parser.ExtractAllText(pdfPath);
            var chapterRules = ExtractRulesFromChapter(pages, chapter, edition);
            allRules.AddRange(chapterRules);
            chapter.ExtractedRuleCount = chapterRules.Count;
        }

        result.RulesExtracted = allRules.Count;

        // Step 4: Generate diff
        if (previousEdition != null)
        {
            logger.LogInformation("Step 4: Generating diff report...");
            // TODO: Load previous rules and diff against new rules
            // For now, mark all as "Added" on first run
        }

        // Step 5: Persist
        logger.LogInformation("Step 5: Persisting {Count} rules...", allRules.Count);
        await store.UpsertRulesAsync(allRules);
        await store.SaveEditionAsync(edition);

        // Step 6: Publish to ConceptMap
        var mapVersionId = $"tmppm-{result.EditionId}";
        result.ConceptMapOverridesPublished = await store.PublishAsConceptMapOverridesAsync(
            allRules, mapVersionId, tenantId);

        logger.LogInformation("═══ Pipeline Complete: {Rules} rules, {Overrides} ConceptMap overrides ═══",
            result.RulesExtracted, result.ConceptMapOverridesPublished);

        return result;
    }

    /// <summary>
    /// Extract PA rules from a parsed chapter by scanning for "Prior Authorization" sections.
    /// </summary>
    private List<TmppmPaRule> ExtractRulesFromChapter(
        List<PageText> pages, TmppmChapter chapter, TmppmEdition edition)
    {
        var rules = new List<TmppmPaRule>();
        var fullText = string.Join("\n", pages.Select(p => p.Text));

        // Find all "Prior Authorization" section headers
        var paHeaderPattern = @"(\d+(?:\.\d+)+)\s+(?:Prior\s+Authorization|Authorization\s+Requirements)\s+(?:for\s+)?([^\n]+)";
        var matches = System.Text.RegularExpressions.Regex.Matches(
            fullText, paHeaderPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var sectionRef = match.Groups[1].Value;
            var categoryName = match.Groups[2].Value.Trim().TrimEnd('.');

            var sectionText = parser.ExtractSection(pages, sectionRef);
            if (sectionText == null) continue;

            var codes = parser.ExtractProcedureCodes(sectionText);
            var ageRule = parser.ExtractAgeRule(sectionText);
            var dxCodes = parser.ExtractDiagnosisCodes(sectionText);
            var paRequired = parser.DetectPaRequired(sectionText);

            var rule = new TmppmPaRule
            {
                RuleId = $"TX-{chapter.ChapterId}-{sectionRef}".Replace(".", "-"),
                State = "TX",
                Category = categoryName,
                TmppmRef = $"§{sectionRef}",
                RuleType = "AuthRequired",
                ProcedureCodes = codes.Select(c => c.Code).ToList(),
                CodeSystem = codes.FirstOrDefault()?.System ?? "CPT",
                AuthRequired = paRequired,
                AgeLimit = ageRule,
                AllowedDiagnoses = dxCodes.Count > 0 ? dxCodes : null,
                EffectiveDate = edition.PolicyThroughDate,
                SourceEdition = edition.EditionId,
                ClinicalCriteriaSummary = sectionText.Length > 500
                    ? sectionText[..500] + "..."
                    : sectionText
            };

            rules.Add(rule);
            logger.LogDebug("Extracted rule {Id}: {Category} ({CodeCount} codes)",
                rule.RuleId, rule.Category, rule.ProcedureCodes.Count);
        }

        logger.LogInformation("Chapter {Id}: extracted {Count} PA rules", chapter.ChapterId, rules.Count);
        return rules;
    }
}

public class IngestionResult
{
    public string EditionId { get; set; } = string.Empty;
    public int ChaptersDownloaded { get; set; }
    public int ChaptersChanged { get; set; }
    public int RulesExtracted { get; set; }
    public int ConceptMapOverridesPublished { get; set; }
}
