using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Task = System.Threading.Tasks.Task;

namespace FhirService.Middleware;

/// <summary>
/// Writes a FHIR <c>OperationOutcome</c> error response.
///
/// One writer, shared by every middleware that refuses a request under
/// <c>/fhir/r4</c>. A FHIR client is entitled to parse an error body as an
/// OperationOutcome, so a middleware that answers with an ad-hoc JSON shape is
/// not merely inconsistent — it is unparseable to a strict client, and which
/// shape it gets depends on which layer happened to refuse it.
/// </summary>
internal static class FhirErrorResponse
{
    private static readonly JsonSerializerOptions FhirOptions =
        new JsonSerializerOptions().ForFhir(ModelInfo.ModelInspector);

    public static async Task WriteAsync(
        HttpContext context,
        int statusCode,
        OperationOutcome.IssueSeverity severity,
        OperationOutcome.IssueType code,
        string diagnostics)
    {
        var outcome = new OperationOutcome
        {
            Issue =
            [
                new OperationOutcome.IssueComponent
                {
                    Severity = severity,
                    Code = code,
                    Diagnostics = diagnostics,
                }
            ],
        };

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/fhir+json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(outcome, FhirOptions));
    }
}
