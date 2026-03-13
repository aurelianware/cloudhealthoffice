using System.Text;
using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Task = System.Threading.Tasks.Task;
using Microsoft.AspNetCore.Mvc.Formatters;

namespace FhirService.Formatters;

/// <summary>
/// ASP.NET Core output formatter that serializes FHIR resources using the Firely SDK's
/// System.Text.Json-based serializer. Handles both application/fhir+json and application/json.
/// </summary>
public class FhirOutputFormatter : TextOutputFormatter
{
    private static readonly JsonSerializerOptions Options =
        new JsonSerializerOptions().ForFhir(typeof(Patient).Assembly);

    public FhirOutputFormatter()
    {
        SupportedMediaTypes.Add("application/fhir+json");
        SupportedMediaTypes.Add("application/json");
        SupportedEncodings.Add(Encoding.UTF8);
    }

    protected override bool CanWriteType(Type? type)
        => type != null && typeof(Base).IsAssignableFrom(type);

    public override async Task WriteResponseBodyAsync(
        OutputFormatterWriteContext context, Encoding selectedEncoding)
    {
        if (context.Object is Base fhirResource)
        {
            context.HttpContext.Response.ContentType = "application/fhir+json; charset=utf-8";
            var json = JsonSerializer.Serialize(fhirResource, fhirResource.GetType(), Options);
            await context.HttpContext.Response.WriteAsync(json, selectedEncoding);
        }
    }
}
