using System.Globalization;
using System.Text.Json;
using CloudHealthOffice.PlanEdits.Contracts;

namespace PlanEditServiceSample;

/// <summary>Settings for the example rules, bound from <c>PlanEdits</c>.</summary>
public sealed class SamplePlanEditOptions
{
    public const string SectionName = "PlanEdits";

    /// <summary>The secret shared with Cloud Health Office, from a secret store.</summary>
    public string SigningSecret { get; set; } = string.Empty;

    /// <summary>A line charging at least this much needs a reviewer (rule SAMPLE-HIDOLLAR).</summary>
    public decimal HighDollarLineThreshold { get; set; } = 10_000m;

    /// <summary>Procedure codes this example plan does not cover (rule SAMPLE-NONCOVERED).</summary>
    public List<string> NonCoveredProcedureCodes { get; set; } = new();
}

/// <summary>
/// The example plan's rules. A real plan writes its own; what matters is the
/// shape: a pure function from the claim to the edits that fired, with stable
/// rule ids, so the same claim always gets the same edits.
/// </summary>
public static class SamplePlanEditRules
{
    public const string HighDollarRule = "SAMPLE-HIDOLLAR";
    public const string NonCoveredRule = "SAMPLE-NONCOVERED";

    public static IReadOnlyList<PlanEdit> Evaluate(PlanEditClaim claim, SamplePlanEditOptions options)
    {
        var edits = new List<PlanEdit>();
        var nonCovered = options.NonCoveredProcedureCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var notCovered = claim.Lines.Where(l => nonCovered.Contains(l.ProcedureCode)).Select(l => l.LineNumber).ToList();
        if (notCovered.Count > 0)
        {
            // A denial edit: upheld, CHO denies these lines with CARC 96 / RARC N130.
            edits.Add(new PlanEdit
            {
                RuleId = NonCoveredRule,
                Message = "Procedure is not a covered service under this plan's benefits.",
                LineNumbers = notCovered,
                SuggestedCarc = "96",
                SuggestedRarc = "N130",
            });
        }

        foreach (var line in claim.Lines.Where(l => l.Charge >= options.HighDollarLineThreshold))
        {
            // A review edit: upheld, it records the review and changes no payment.
            edits.Add(new PlanEdit
            {
                RuleId = HighDollarRule,
                Message = string.Create(CultureInfo.InvariantCulture,
                    $"Line charge of {line.Charge:0.00} is at or over the plan's review threshold of {options.HighDollarLineThreshold:0.00}."),
                LineNumbers = [line.LineNumber],
            });
        }

        return edits;
    }
}

/// <summary>The HTTP endpoint: verify the signature, then evaluate.</summary>
public static class SamplePlanEditEndpoint
{
    public const string Route = "/v1/claims";

    /// <summary>Largest request body the sample reads before it checks the signature.</summary>
    public const int MaxRequestBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapSamplePlanEdits(this IEndpointRouteBuilder app)
    {
        app.MapPost(Route, HandleAsync);
        return app;
    }

    private static async Task<IResult> HandleAsync(HttpContext http, Microsoft.Extensions.Options.IOptions<SamplePlanEditOptions> options, TimeProvider time)
    {
        // Read the exact bytes, up to a limit: the signature is over them, not
        // over a re-serialization, and nothing unauthenticated is read without bound.
        if (http.Request.ContentLength is > MaxRequestBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, http.RequestAborted)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxRequestBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        var body = buffer.ToArray();

        if (!PlanEditSignature.Verify(
                options.Value.SigningSecret,
                http.Request.Headers[PlanEditContract.TimestampHeader],
                http.Request.Headers[PlanEditContract.SignatureHeader],
                body,
                time.GetUtcNow()))
        {
            return Results.Unauthorized();
        }

        PlanEditRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<PlanEditRequest>(body, PlanEditJson.Options);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }
        if (request is null || request.ContractVersion != PlanEditContract.Version)
            return Results.BadRequest();

        var response = new PlanEditResponse
        {
            ContractVersion = PlanEditContract.Version,
            Edits = SamplePlanEditRules.Evaluate(request.Claim, options.Value),
        };
        return Results.Json(response, PlanEditJson.Options, PlanEditContract.MediaType);
    }
}
