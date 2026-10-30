namespace CloudHealthOffice.Infrastructure.ReferenceData;

/// <summary>
/// Display names for the <c>urn:ietf:bcp:47</c> language tags CHO stores. A
/// FHIR validator checks a coding's display against the IANA registry, so a
/// display that just repeats the tag (<c>en</c>) is reported as wrong; a tag
/// not listed here gets no display at all rather than a wrong one.
/// </summary>
public static class Bcp47Language
{
    public const string System = "urn:ietf:bcp:47";

    /// <summary>The registry name for <paramref name="tag"/>, or null when CHO doesn't know it.</summary>
    public static string? DisplayName(string? tag) => tag?.Trim().ToLowerInvariant() switch
    {
        "en" => "English",
        "es" => "Spanish",
        "zh" => "Chinese",
        "vi" => "Vietnamese",
        "tl" => "Tagalog",
        "ko" => "Korean",
        "ru" => "Russian",
        "ar" => "Arabic",
        "fr" => "French",
        "de" => "German",
        "hi" => "Hindi",
        "ja" => "Japanese",
        "pt" => "Portuguese",
        "it" => "Italian",
        "pl" => "Polish",
        _ => null
    };
}
