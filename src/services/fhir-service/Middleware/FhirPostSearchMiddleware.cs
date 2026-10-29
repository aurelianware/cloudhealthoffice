using Hl7.Fhir.Model;
using Task = System.Threading.Tasks.Task;
using Microsoft.AspNetCore.Http.Extensions;

namespace FhirService.Middleware;

/// <summary>
/// Serves FHIR's POST search (<c>POST [base]/[type]/_search</c> with an
/// <c>application/x-www-form-urlencoded</c> body) as the GET search it is
/// equivalent to.
///
/// <para>
/// FHIR R4 (http.html#search) defines both forms, and US Core 3.1.1 requires a
/// server to support both: its first search test for each resource repeats the
/// search as a POST. fhir-service only routed GET, so the POST matched no search
/// endpoint and answered 403, and Inferno's US Core kit failed Patient
/// <c>_id</c> search on it (run 40956429968).
/// </para>
///
/// <para>
/// It runs before routing (Program.cs calls UseRouting right after it), because
/// the endpoint, and with it the [FhirAccess] policy, is chosen from the method
/// and path. The rewrite also happens before authentication, so everything downstream (SMART
/// scope enforcement, patient binding, [FhirAccess], the disclosure ledger)
/// sees the read it actually is and enforces it the same way. Parameters in the
/// URL and in the body both apply, as the spec says. A POST to
/// <c>_search</c> with any other body is left alone and refused as before:
/// the spec allows only the form encoding.
/// </para>
/// </summary>
public sealed class FhirPostSearchMiddleware
{
    private const string SearchSuffix = "/_search";

    private readonly RequestDelegate _next;

    public FhirPostSearchMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var path = request.Path.Value;

        if (HttpMethods.IsPost(request.Method)
            && path is not null
            && request.Path.StartsWithSegments("/fhir/r4")
            && path.EndsWith(SearchSuffix, StringComparison.Ordinal)
            && path.Length > "/fhir/r4".Length + SearchSuffix.Length
            && request.HasFormContentType)
        {
            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync(context.RequestAborted);
            }
            catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
            {
                await FhirErrorResponse.WriteAsync(context, 400,
                    OperationOutcome.IssueSeverity.Error,
                    OperationOutcome.IssueType.Invalid,
                    "The _search body could not be read as application/x-www-form-urlencoded.");
                return;
            }

            var query = new QueryBuilder();
            foreach (var (key, values) in request.Query)
            {
                foreach (var value in values)
                {
                    query.Add(key, value ?? "");
                }
            }

            foreach (var (key, values) in form)
            {
                foreach (var value in values)
                {
                    query.Add(key, value ?? "");
                }
            }

            request.Method = HttpMethods.Get;
            request.Path = new PathString(path[..^SearchSuffix.Length]);
            request.QueryString = query.ToQueryString();
            request.Body = Stream.Null;
            request.ContentLength = 0;
            request.ContentType = null;
        }

        await _next(context);
    }
}
