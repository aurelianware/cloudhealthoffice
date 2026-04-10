using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using CHO.TmppmIngestionService.Models;

namespace CHO.TmppmIngestionService.Loaders;

/// <summary>
/// Downloads TMPPM PDF chapters from TMHP and detects changes via SHA256 hashing.
/// Supports both full edition downloads and incremental change detection.
/// </summary>
public class TmhpChapterDownloader(HttpClient httpClient, ILogger<TmhpChapterDownloader> logger)
{
    private const string BaseUrl = "https://www.tmhp.com/sites/default/files/file-library/resources/provider-manuals/tmppm/pdf-chapters";

    /// <summary>
    /// Known TMPPM chapters with their PDF filenames and CHO service mappings.
    /// </summary>
    public static readonly List<ChapterDefinition> KnownChapters =
    [
        new("1_05_prior_authorization", "Fee-for-Service Prior Authorizations", "Vol. 1 Section 5"),
        new("2_01_ambulance_services", "Ambulance Services Handbook", "Vol. 2"),
        new("2_02_behavioral_health", "Behavioral Health and Case Management Services Handbook", "Vol. 2"),
        new("2_06_dme_and_supplies", "DME, Medical Supplies, and Nutritional Products Handbook", "Vol. 2"),
        new("2_11_inpatient_outpatient_hosp_srvs", "Inpatient and Outpatient Hospital Services Handbook", "Vol. 2"),
        new("2_13_med_specs_and_phys_srvs", "Medical and Nursing Specialists, Physicians, and PAs Handbook", "Vol. 2"),
        new("2_16_pt_ot_st_srvs", "PT/OT/Speech Therapy Services Handbook", "Vol. 2"),
        new("2_17_radiology_and_lab_srvs", "Radiology and Laboratory Services Handbook", "Vol. 2"),
    ];

    /// <summary>
    /// Download all known chapters for a given edition (year/month).
    /// Returns the edition with SHA256 hashes for each chapter.
    /// </summary>
    public async Task<TmppmEdition> DownloadEditionAsync(int year, int month, string outputDir)
    {
        var monthName = new DateTime(year, month, 1).ToString("MMMM").ToLower();
        var yearMonth = $"{year}-{month:D2}-{monthName}";

        var edition = new TmppmEdition
        {
            EditionId = $"{year}-{month:D2}",
            SourceUrl = $"{BaseUrl}/{year}/{yearMonth}/",
            IngestedAt = DateTime.UtcNow
        };

        Directory.CreateDirectory(outputDir);

        foreach (var chapterDef in KnownChapters)
        {
            var pdfUrl = $"{BaseUrl}/{year}/{yearMonth}/{chapterDef.FileName}.pdf";
            var localPath = Path.Combine(outputDir, $"{chapterDef.FileName}.pdf");

            try
            {
                var response = await httpClient.GetAsync(pdfUrl);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Failed to download {Url}: {Status}", pdfUrl, response.StatusCode);
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(localPath, bytes);

                var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

                edition.Chapters.Add(new TmppmChapter
                {
                    ChapterId = chapterDef.FileName,
                    Title = chapterDef.Title,
                    PdfFileName = $"{chapterDef.FileName}.pdf",
                    PdfUrl = pdfUrl,
                    Sha256 = sha256
                });

                logger.LogInformation("Downloaded {Chapter} ({Bytes} bytes, SHA256: {Hash})",
                    chapterDef.FileName, bytes.Length, sha256[..12]);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error downloading {Url}", pdfUrl);
            }
        }

        return edition;
    }

    /// <summary>
    /// Compare two editions by SHA256 hash to identify changed chapters.
    /// Only changed chapters need re-parsing.
    /// </summary>
    public List<string> DetectChangedChapters(TmppmEdition previous, TmppmEdition current)
    {
        var changed = new List<string>();
        var prevHashes = previous.Chapters.ToDictionary(c => c.ChapterId, c => c.Sha256);

        foreach (var chapter in current.Chapters)
        {
            if (!prevHashes.TryGetValue(chapter.ChapterId, out var prevHash) || prevHash != chapter.Sha256)
            {
                changed.Add(chapter.ChapterId);
                logger.LogInformation("Chapter changed: {Id} (prev: {Prev}, curr: {Curr})",
                    chapter.ChapterId,
                    prevHash?[..12] ?? "new",
                    chapter.Sha256?[..12]);
            }
        }

        logger.LogInformation("{Count}/{Total} chapters changed between {From} and {To}",
            changed.Count, current.Chapters.Count, previous.EditionId, current.EditionId);

        return changed;
    }
}

public record ChapterDefinition(string FileName, string Title, string Volume);
