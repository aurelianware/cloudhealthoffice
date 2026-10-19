namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Wire shape returned by claims-service <c>/api/v1/claims</c> — the
/// resources array is FHIR ExplanationOfBenefit JSON and stays opaque to the
/// portal (which only renders count + drill-out link).
/// </summary>
public class EobSearchResponse
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    /// <summary>FHIR ExplanationOfBenefit resources, untyped on this side.</summary>
    public List<System.Text.Json.JsonElement> Resources { get; set; } = new();
}
