using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace CHO.TmppmIngestionService.Parsers;

/// <summary>
/// Extracts text sections from TMPPM PDF chapters using PdfPig.
/// Sections are identified by their hierarchical numbering (e.g., "9.2.46.14").
/// </summary>
public partial class TmppmPdfParser(ILogger<TmppmPdfParser> logger)
{
    /// <summary>
    /// Extract all text from a PDF file, page by page.
    /// </summary>
    public List<PageText> ExtractAllText(string pdfPath)
    {
        var pages = new List<PageText>();
        using var document = PdfDocument.Open(pdfPath);

        foreach (var page in document.GetPages())
        {
            var text = page.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                pages.Add(new PageText { PageNumber = page.Number, Text = text });
            }
        }

        logger.LogInformation("Extracted {PageCount} pages from {Path}", pages.Count, pdfPath);
        return pages;
    }

    /// <summary>
    /// Extract a specific section by its TMPPM reference number (e.g., "9.2.46.14").
    /// Returns the text from the section header through the next section of the same or higher level.
    /// </summary>
    public string? ExtractSection(List<PageText> pages, string sectionRef)
    {
        var fullText = string.Join("\n", pages.Select(p => p.Text));

        // Build regex for the section header — match patterns like "9.2.46.14 Hypoglossal Nerve Stimulators"
        var escapedRef = Regex.Escape(sectionRef);
        var headerPattern = $@"(?:^|\n)\s*{escapedRef}[\s\n]+([^\n]+)";
        var headerMatch = Regex.Match(fullText, headerPattern);

        if (!headerMatch.Success)
        {
            logger.LogWarning("Section {Ref} not found in PDF text", sectionRef);
            return null;
        }

        var startIndex = headerMatch.Index;

        // Find the next section at the same or higher level
        var parts = sectionRef.Split('.');
        var nextSectionPattern = BuildNextSectionPattern(parts);
        var nextMatch = Regex.Match(fullText[(startIndex + headerMatch.Length)..], nextSectionPattern);

        var endIndex = nextMatch.Success
            ? startIndex + headerMatch.Length + nextMatch.Index
            : Math.Min(startIndex + 10000, fullText.Length); // Cap at ~10K chars if no next section

        var sectionText = fullText[startIndex..endIndex].Trim();
        logger.LogInformation("Extracted section {Ref}: {Len} chars", sectionRef, sectionText.Length);
        return sectionText;
    }

    /// <summary>
    /// Scan a section's text for CPT/HCPCS procedure codes.
    /// Matches 5-digit numeric CPT codes and alphanumeric HCPCS codes (e.g., C8007, L2221, J9601).
    /// </summary>
    public List<ExtractedCode> ExtractProcedureCodes(string sectionText)
    {
        var codes = new HashSet<ExtractedCode>();

        // CPT: 5-digit numeric codes (10000-99999 range typical)
        foreach (Match m in CptCodeRegex().Matches(sectionText))
        {
            var code = m.Value;
            if (int.TryParse(code, out var num) && num >= 10000)
            {
                codes.Add(new ExtractedCode { Code = code, System = "CPT" });
            }
        }

        // HCPCS Level II: Letter + 4 digits (A-V prefix)
        foreach (Match m in HcpcsCodeRegex().Matches(sectionText))
        {
            codes.Add(new ExtractedCode { Code = m.Value, System = "HCPCS" });
        }

        logger.LogInformation("Found {Count} procedure codes in section text", codes.Count);
        return [.. codes];
    }

    /// <summary>
    /// Extract age restrictions from section text.
    /// Looks for patterns like "clients under 21", "ages 12 through 20", "birth through 20 years".
    /// </summary>
    public Models.AgeRule? ExtractAgeRule(string sectionText)
    {
        // Pattern: "clients [age] X through Y years"
        var rangeMatch = Regex.Match(sectionText,
            @"(?:age[sd]?|clients?)\s+(\d+)\s+(?:through|to)\s+(\d+)\s+years?",
            RegexOptions.IgnoreCase);
        if (rangeMatch.Success)
        {
            return new Models.AgeRule
            {
                MinAge = int.Parse(rangeMatch.Groups[1].Value),
                MaxAge = int.Parse(rangeMatch.Groups[2].Value),
                Unit = "years"
            };
        }

        // Pattern: "under X years" or "younger than X"
        var underMatch = Regex.Match(sectionText,
            @"(?:under|younger than|less than)\s+(\d+)\s+years?",
            RegexOptions.IgnoreCase);
        if (underMatch.Success)
        {
            return new Models.AgeRule { MaxAge = int.Parse(underMatch.Groups[1].Value) - 1, Unit = "years" };
        }

        // Pattern: "X years of age or older" or "at least X years"
        var overMatch = Regex.Match(sectionText,
            @"(\d+)\s+years?\s+of\s+age\s+or\s+older|at\s+least\s+(\d+)\s+years?",
            RegexOptions.IgnoreCase);
        if (overMatch.Success)
        {
            var val = overMatch.Groups[1].Success ? overMatch.Groups[1].Value : overMatch.Groups[2].Value;
            return new Models.AgeRule { MinAge = int.Parse(val), Unit = "years" };
        }

        return null;
    }

    /// <summary>
    /// Extract ICD-10 diagnosis codes from section text.
    /// </summary>
    public List<string> ExtractDiagnosisCodes(string sectionText)
    {
        var codes = new HashSet<string>();
        foreach (Match m in Icd10Regex().Matches(sectionText))
        {
            codes.Add(m.Value);
        }
        return [.. codes];
    }

    /// <summary>
    /// Detect whether a section indicates PA is required.
    /// </summary>
    public bool DetectPaRequired(string sectionText)
    {
        var positivePatterns = new[]
        {
            @"prior\s+authorization\s+(?:is\s+)?required",
            @"requires?\s+prior\s+authorization",
            @"must\s+(?:be\s+)?(?:prior\s+)?authorized",
            @"prior\s+authorization\s+must\s+be\s+obtained",
            @"a\s+prior\s+authorization\s+number\s+\(PAN\)",
        };

        return positivePatterns.Any(p =>
            Regex.IsMatch(sectionText, p, RegexOptions.IgnoreCase));
    }

    private static string BuildNextSectionPattern(string[] currentParts)
    {
        // Match any section header at the same depth or shallower
        // E.g., for "9.2.46.14", match "9.2.46.15", "9.2.47", "9.3", "10", etc.
        var depth = currentParts.Length;
        var patterns = new List<string>();

        for (int i = depth; i >= 1; i--)
        {
            var prefix = string.Join(@"\.", currentParts.Take(i - 1));
            var nextNum = int.Parse(currentParts[i - 1]) + 1;
            if (prefix.Length > 0)
                patterns.Add($@"(?:^|\n)\s*{prefix}\.{nextNum}\s+\S");
            else
                patterns.Add($@"(?:^|\n)\s*{nextNum}\s+\S");
        }

        return string.Join("|", patterns);
    }

    [GeneratedRegex(@"\b\d{5}\b")]
    private static partial Regex CptCodeRegex();

    [GeneratedRegex(@"\b[A-V]\d{4}\b")]
    private static partial Regex HcpcsCodeRegex();

    [GeneratedRegex(@"\b[A-Z]\d{2}\.?\d{0,4}\b")]
    private static partial Regex Icd10Regex();
}

public class PageText
{
    public int PageNumber { get; set; }
    public string Text { get; set; } = string.Empty;
}

public class ExtractedCode
{
    public string Code { get; set; } = string.Empty;
    public string System { get; set; } = string.Empty;

    public override bool Equals(object? obj) =>
        obj is ExtractedCode other && Code == other.Code && System == other.System;

    public override int GetHashCode() => HashCode.Combine(Code, System);
}
