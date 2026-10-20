using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FhirService.Services.ChoNative;

/// <summary>
/// Answers the two adapter failures as FHIR (ADR 018): an unsupported search as
/// 400 and an upstream outage as 502, each with an OperationOutcome. Registered
/// globally so every controller over <see cref="IFhirDataAdapter"/> gets it.
/// </summary>
public sealed class FhirAdapterExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        switch (context.Exception)
        {
            case FhirSearchNotSupportedException notSupported:
                context.Result = Outcome(400, OperationOutcome.IssueType.NotSupported, notSupported.Message);
                context.ExceptionHandled = true;
                break;
            case FhirUpstreamException upstream:
                context.Result = Outcome(502, OperationOutcome.IssueType.Transient, upstream.Message);
                context.ExceptionHandled = true;
                break;
        }
    }

    private static ObjectResult Outcome(int status, OperationOutcome.IssueType code, string diagnostics) => new(new OperationOutcome
    {
        Issue =
        [
            new OperationOutcome.IssueComponent
            {
                Severity = OperationOutcome.IssueSeverity.Error,
                Code = code,
                Diagnostics = diagnostics,
            },
        ],
    })
    {
        StatusCode = status,
    };
}
