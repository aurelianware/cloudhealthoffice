using Microsoft.AspNetCore.Mvc;

namespace FhirService.Models;

public class QuestionnaireSearchParams
{
    [FromQuery(Name = "_id")] public string? Id { get; set; }
    [FromQuery(Name = "name")] public string? Name { get; set; }
    [FromQuery(Name = "title")] public string? Title { get; set; }
    [FromQuery(Name = "status")] public string? Status { get; set; }
    [FromQuery(Name = "context-type-value")] public string? ContextTypeValue { get; set; }
    [FromQuery(Name = "_count")] public int Count { get; set; } = 20;
    [FromQuery(Name = "page")] public int Page { get; set; } = 1;
}

public class QuestionnaireResponseSearchParams
{
    [FromQuery(Name = "_id")] public string? Id { get; set; }
    [FromQuery(Name = "questionnaire")] public string? QuestionnaireRef { get; set; }
    [FromQuery(Name = "patient")] public string? Patient { get; set; }
    [FromQuery(Name = "status")] public string? Status { get; set; }
    [FromQuery(Name = "authored")] public string? Authored { get; set; }
    [FromQuery(Name = "_count")] public int Count { get; set; } = 20;
    [FromQuery(Name = "page")] public int Page { get; set; } = 1;
}
