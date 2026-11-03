using System.Net;
using System.Text;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace CloudHealthOffice.Portal.Services.Dtr;

// The portal has its own Coverage view model one namespace up.
using Coverage = Hl7.Fhir.Model.Coverage;

/// <summary>
/// The provider portal's DTR questionnaire page, against fhir-service:
/// find the member's Coverage, fetch the DTR <c>$questionnaire-package</c>,
/// save the completed QuestionnaireResponse and submit the PAS request that
/// carries it. All calls go with the signed-in user's CHO token
/// (<see cref="ChoBearerTokenHandler"/>), so fhir-service enforces the
/// user's permissions (members:read, coverage:read, authorizations:read and
/// authorizations:write).
/// </summary>
public interface IDtrPortalService
{
    /// <summary>The member's Patient and Coverages, or null when there is no such member.</summary>
    Task<DtrMember?> FindMemberAsync(string memberId, CancellationToken ct = default);

    /// <summary>Active questionnaires the tenant can use, for choosing a form by hand.</summary>
    Task<IReadOnlyList<Questionnaire>> ListQuestionnairesAsync(CancellationToken ct = default);

    /// <summary>The forms for these canonicals (or for the service code), packaged for the member's Coverage.</summary>
    Task<DtrPackage> GetPackageAsync(
        Coverage coverage, IReadOnlyList<string> questionnaires, Coding? serviceCode, CancellationToken ct = default);

    /// <summary>An adaptive form's next question: the response with the answers so far, as the plan returns it.</summary>
    Task<QuestionnaireResponse> NextQuestionAsync(QuestionnaireResponse response, CancellationToken ct = default);

    Task<QuestionnaireResponse> SaveResponseAsync(QuestionnaireResponse response, CancellationToken ct = default);

    Task<PasSubmitOutcome> SubmitPriorAuthAsync(Bundle request, CancellationToken ct = default);

    /// <summary>A stored response and the Questionnaire it answers (null when the form is not in the library).</summary>
    Task<(QuestionnaireResponse Response, Questionnaire? Questionnaire)?> GetResponseAsync(string id, CancellationToken ct = default);
}

public sealed record DtrMember(Patient Patient, IReadOnlyList<Coverage> Coverages)
{
    public string DisplayName
    {
        get
        {
            var name = Patient.Name?.FirstOrDefault(n => n.Use == HumanName.NameUse.Official) ?? Patient.Name?.FirstOrDefault();
            if (name is null) return Patient.Id;
            var text = string.Join(' ', (name.Given ?? []).Append(name.Family).Where(p => !string.IsNullOrWhiteSpace(p)));
            return string.IsNullOrWhiteSpace(text) ? name.Text ?? Patient.Id : text;
        }
    }
}

/// <summary>
/// One packaged form: the Questionnaire, its expanded ValueSets, the draft
/// response's extensions to carry over, and the draft itself (which an
/// adaptive form is answered from).
/// </summary>
public sealed record DtrPackagedForm(
    Questionnaire Questionnaire, IReadOnlyList<ValueSet> ValueSets, IReadOnlyList<Extension> ResponseExtensions,
    QuestionnaireResponse? Draft = null)
{
    public bool IsAdaptive => AdaptiveForm.IsAdaptiveDraft(Draft);
}

public sealed record DtrPackage(IReadOnlyList<DtrPackagedForm> Forms, IReadOnlyList<string> Messages);

/// <summary>What <c>Claim/$submit</c> answered.</summary>
public sealed record PasSubmitOutcome(
    bool Accepted, string? AuthorizationNumber, string? Decision, string? Disposition, IReadOnlyList<string> Problems)
{
    public static PasSubmitOutcome Refused(IReadOnlyList<string> problems) => new(false, null, null, null, problems);
}

/// <summary>A call fhir-service refused (4xx), with its OperationOutcome text for the page.</summary>
public sealed class DtrRequestException(HttpStatusCode status, IReadOnlyList<string> problems)
    : Exception(string.Join(" ", problems))
{
    public HttpStatusCode Status { get; } = status;

    public IReadOnlyList<string> Problems { get; } = problems;
}

public sealed class DtrPortalService : IDtrPortalService
{
    public const string ServiceName = "FHIR Service";

    private const string ReviewActionCodeUrl =
        "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-reviewActionCode";

    private static readonly FhirJsonParser Parser = new(new ParserSettings { PermissiveParsing = true });
    private static readonly FhirJsonSerializer Serializer = new();

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DtrPortalService> _logger;

    public DtrPortalService(HttpClient http, IConfiguration configuration, ILogger<DtrPortalService> logger)
    {
        _http = http;
        _configuration = configuration;
        _logger = logger;
    }

    private string Base =>
        (_configuration["Services:FhirService"] ?? "http://fhir-service.cloudhealthoffice").TrimEnd('/') + "/fhir/r4";

    public async Task<DtrMember?> FindMemberAsync(string memberId, CancellationToken ct = default)
    {
        var id = memberId.Trim();
        if (id.Length == 0 || id.Length > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
            return null;

        var patient = await GetAsync<Patient>($"Patient/{Uri.EscapeDataString(id)}", ct, notFoundIsNull: true);
        if (patient is null) return null;

        var bundle = await GetAsync<Bundle>($"Coverage?beneficiary={Uri.EscapeDataString($"Patient/{id}")}&_count=20", ct);
        var coverages = (bundle?.Entry ?? [])
            .Select(e => e.Resource)
            .OfType<Coverage>()
            .Where(c => !string.IsNullOrEmpty(c.Id))
            // Active first; a cancelled or draft Coverage is offered last.
            .OrderBy(c => c.Status == FinancialResourceStatusCodes.Active ? 0 : 1)
            .ToList();
        return new DtrMember(patient, coverages);
    }

    public async Task<IReadOnlyList<Questionnaire>> ListQuestionnairesAsync(CancellationToken ct = default)
    {
        var bundle = await GetAsync<Bundle>("Questionnaire?status=active&_count=100", ct);
        return (bundle?.Entry ?? [])
            .Select(e => e.Resource)
            .OfType<Questionnaire>()
            .OrderBy(q => q.Title ?? q.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<DtrPackage> GetPackageAsync(
        Coverage coverage, IReadOnlyList<string> questionnaires, Coding? serviceCode, CancellationToken ct = default)
    {
        var parameters = new Parameters();
        parameters.Parameter.Add(new Parameters.ParameterComponent { Name = "coverage", Resource = coverage });
        foreach (var canonical in questionnaires.Distinct(StringComparer.Ordinal))
            parameters.Parameter.Add(new Parameters.ParameterComponent { Name = "questionnaire", Value = new Canonical(canonical) });

        // No form named: the service picks the forms whose focus lists the
        // code, as it does for an EHR's order.
        if (questionnaires.Count == 0 && serviceCode is not null)
        {
            parameters.Parameter.Add(new Parameters.ParameterComponent
            {
                Name = "order",
                Resource = new ServiceRequest
                {
                    Id = "portal-order",
                    Status = RequestStatus.Draft,
                    Intent = RequestIntent.Order,
                    Code = new CodeableConcept { Coding = [serviceCode] },
                    Subject = coverage.Beneficiary,
                },
            });
        }

        var result = await PostAsync<Parameters>("Questionnaire/$questionnaire-package", parameters, ct);

        var forms = new List<DtrPackagedForm>();
        var messages = new List<string>();
        foreach (var parameter in result?.Parameter ?? [])
        {
            if (parameter.Name == "packagebundle" && parameter.Resource is Bundle package)
            {
                var resources = package.Entry.Select(e => e.Resource).ToList();
                if (resources.OfType<Questionnaire>().FirstOrDefault() is not { } questionnaire) continue;
                var draft = resources.OfType<QuestionnaireResponse>().FirstOrDefault();
                forms.Add(new DtrPackagedForm(
                    questionnaire,
                    resources.OfType<ValueSet>().ToList(),
                    draft?.Extension.ToList() ?? [],
                    draft));
            }
            else if (parameter.Name == "outcome" && parameter.Resource is OperationOutcome outcome)
            {
                messages.AddRange(Messages(outcome));
            }
        }

        return new DtrPackage(forms, messages);
    }

    public async Task<QuestionnaireResponse> NextQuestionAsync(QuestionnaireResponse response, CancellationToken ct = default)
        => await PostAsync<QuestionnaireResponse>("Questionnaire/$next-question", response, ct)
           ?? throw new DtrRequestException(HttpStatusCode.BadGateway, ["The FHIR service did not return the next question."]);

    public async Task<QuestionnaireResponse> SaveResponseAsync(QuestionnaireResponse response, CancellationToken ct = default)
        => await PostAsync<QuestionnaireResponse>("QuestionnaireResponse", response, ct)
           ?? throw new DtrRequestException(HttpStatusCode.BadGateway, ["The FHIR service did not return the saved response."]);

    public async Task<PasSubmitOutcome> SubmitPriorAuthAsync(Bundle request, CancellationToken ct = default)
    {
        Bundle? answer;
        try
        {
            answer = await PostAsync<Bundle>("Claim/$submit", request, ct);
        }
        catch (DtrRequestException ex) when ((int)ex.Status is >= 400 and < 500)
        {
            return PasSubmitOutcome.Refused(ex.Problems);
        }

        var claimResponse = answer?.Entry.Select(e => e.Resource).OfType<ClaimResponse>().FirstOrDefault();
        if (claimResponse is null)
            return PasSubmitOutcome.Refused(["The FHIR service answered without a ClaimResponse."]);

        var action = ReviewActionCodes(claimResponse).FirstOrDefault();
        var decision = action switch
        {
            "A1" => "Approved",
            "A2" => "Approved with changes",
            "A3" => "Denied",
            "A4" => "Pended for review",
            _ => claimResponse.Outcome?.ToString(),
        };
        return new PasSubmitOutcome(true, claimResponse.PreAuthRef, decision, claimResponse.Disposition, []);
    }

    public async Task<(QuestionnaireResponse Response, Questionnaire? Questionnaire)?> GetResponseAsync(
        string id, CancellationToken ct = default)
    {
        if (id.Length is 0 or > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
            return null;

        var response = await GetAsync<QuestionnaireResponse>($"QuestionnaireResponse/{id}", ct, notFoundIsNull: true);
        if (response is null) return null;

        // An adaptive form's response contains the questions it was asked and
        // names the form they derive from.
        var contained = AdaptiveForm.IsAdaptiveDraft(response)
            ? response.Contained.OfType<Questionnaire>().FirstOrDefault(q => q.Id == response.Questionnaire![1..])
            : null;
        var (url, version) = SplitCanonical(contained is null ? response.Questionnaire : contained.DerivedFrom.FirstOrDefault());
        var questionnaire = (await ListAllQuestionnairesAsync(ct))
            .Where(q => q.Url == url && (version is null || q.Version == version))
            .FirstOrDefault();
        return (response, contained ?? questionnaire);
    }

    private async Task<IReadOnlyList<Questionnaire>> ListAllQuestionnairesAsync(CancellationToken ct)
    {
        var bundle = await GetAsync<Bundle>("Questionnaire?_count=100", ct);
        return (bundle?.Entry ?? []).Select(e => e.Resource).OfType<Questionnaire>().ToList();
    }

    internal static (string? Url, string? Version) SplitCanonical(string? canonical)
    {
        if (string.IsNullOrEmpty(canonical)) return (null, null);
        var bar = canonical.IndexOf('|');
        return bar < 0 ? (canonical, null) : (canonical[..bar], canonical[(bar + 1)..]);
    }

    internal static IEnumerable<string> ReviewActionCodes(ClaimResponse response)
    {
        var extensions = new List<Extension>(response.Extension);
        foreach (var item in response.Item ?? [])
        {
            extensions.AddRange(item.Extension);
            foreach (var adjudication in item.Adjudication ?? [])
                extensions.AddRange(adjudication.Extension);
        }

        return Descend(extensions)
            .Where(e => e.Url == ReviewActionCodeUrl)
            .Select(e => (e.Value as CodeableConcept)?.Coding?.FirstOrDefault()?.Code ?? (e.Value as Coding)?.Code)
            .Where(c => c is not null)
            .Select(c => c!);

        static IEnumerable<Extension> Descend(IEnumerable<Extension> list)
            => list.SelectMany(e => new[] { e }.Concat(Descend(e.Extension)));
    }

    internal static IEnumerable<string> Messages(OperationOutcome outcome)
        => outcome.Issue
            .Select(i => i.Diagnostics ?? i.Details?.Text)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m!);

    // ── HTTP ─────────────────────────────────────────────────────────────────

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct, bool notFoundIsNull = false) where T : Resource
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/{path}");
            request.Headers.Accept.ParseAdd("application/fhir+json");
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", ServiceName);
            throw new ServiceUnavailableException(ServiceName, ex);
        }

        using (response)
        {
            if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)
                return null;
            return await ReadAsync<T>(response, ct);
        }
    }

    private async Task<T?> PostAsync<T>(string path, Resource body, CancellationToken ct) where T : Resource
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/{path}")
            {
                Content = new StringContent(Serializer.SerializeToString(body), Encoding.UTF8, "application/fhir+json"),
            };
            request.Headers.Accept.ParseAdd("application/fhir+json");
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", ServiceName);
            throw new ServiceUnavailableException(ServiceName, ex);
        }

        using (response)
            return await ReadAsync<T>(response, ct);
    }

    private async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct) where T : Resource
    {
        var json = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
            return string.IsNullOrWhiteSpace(json) ? null : Parser.Parse<T>(json);

        if ((int)response.StatusCode >= 500)
        {
            _logger.LogError("{ServiceName} answered {Status}", ServiceName, (int)response.StatusCode);
            throw new ServiceUnavailableException(ServiceName);
        }

        throw new DtrRequestException(response.StatusCode, Problems(response.StatusCode, json));
    }

    private static IReadOnlyList<string> Problems(HttpStatusCode status, string json)
    {
        if (status is HttpStatusCode.Forbidden)
            return ["Your account is not allowed to do this. Ask your administrator for the prior authorization permissions."];
        if (status is HttpStatusCode.Unauthorized)
            return ["Your session has expired. Sign in again."];

        try
        {
            if (Parser.Parse<Resource>(json) is OperationOutcome outcome && Messages(outcome).ToList() is { Count: > 0 } messages)
                return messages;
        }
        catch (Exception)
        {
            // Not FHIR: fall through to the status.
        }
        return [$"The request was refused ({(int)status})."];
    }
}
