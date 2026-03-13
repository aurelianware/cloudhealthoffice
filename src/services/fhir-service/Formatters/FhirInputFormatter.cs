using System.Text;
using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Mvc.Formatters;

namespace FhirService.Formatters;

/// <summary>
/// ASP.NET Core input formatter that deserializes FHIR resources using the Firely SDK.
/// Supports application/fhir+json and application/json content types.
/// </summary>
public class FhirInputFormatter : TextInputFormatter
{
    private static readonly JsonSerializerOptions Options =
        new JsonSerializerOptions().ForFhir(typeof(Patient).Assembly);

    public FhirInputFormatter()
    {
        SupportedMediaTypes.Add("application/fhir+json");
        SupportedMediaTypes.Add("application/json");
        SupportedEncodings.Add(Encoding.UTF8);
    }

    protected override bool CanReadType(Type type)
        => typeof(Base).IsAssignableFrom(type);

    public override async Task<InputFormatterResult> ReadRequestBodyAsync(
        InputFormatterContext context, Encoding encoding)
    {
        using var reader = new StreamReader(context.HttpContext.Request.Body, encoding);
        var json = await reader.ReadToEndAsync();

        try
        {
            var resource = (Base?)JsonSerializer.Deserialize(json, context.ModelType, Options);
            return await InputFormatterResult.SuccessAsync(resource);
        }
        catch (Exception ex)
        {
            context.ModelState.AddModelError("fhir", $"Invalid FHIR JSON: {ex.Message}");
            return await InputFormatterResult.FailureAsync();
        }
    }
}
