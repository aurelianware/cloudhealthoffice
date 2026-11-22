using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace FhirService.Formatters;

/// <summary>
/// The answer to a FHIR request whose body could not be bound: 400 with an
/// OperationOutcome naming each problem, instead of ASP.NET's ProblemDetails.
/// </summary>
public static class FhirInvalidRequest
{
    public static IActionResult Response(ModelStateDictionary modelState)
    {
        var outcome = new OperationOutcome();
        foreach (var error in modelState.Values.SelectMany(v => v.Errors))
        {
            var message = string.IsNullOrWhiteSpace(error.ErrorMessage) ? error.Exception?.Message : error.ErrorMessage;
            if (string.IsNullOrWhiteSpace(message)) continue;
            outcome.Issue.Add(new OperationOutcome.IssueComponent
            {
                Severity = OperationOutcome.IssueSeverity.Error,
                Code = OperationOutcome.IssueType.Invalid,
                // The parser's own words, which name the element; never echoes a value.
                Diagnostics = message.Length > 500 ? message[..500] : message,
            });
        }

        if (outcome.Issue.Count == 0)
        {
            outcome.Issue.Add(new OperationOutcome.IssueComponent
            {
                Severity = OperationOutcome.IssueSeverity.Error,
                Code = OperationOutcome.IssueType.Invalid,
                Diagnostics = "The request body is not a valid FHIR resource.",
            });
        }

        return new ObjectResult(outcome) { StatusCode = 400 };
    }
}
