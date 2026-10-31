using System.Net.Http.Headers;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;

namespace ClaimsService.Import.Cpcds.Drop;

/// <summary>
/// The other two CPCDS imports a drop run makes: members and coverage
/// (enrollment-import-service) and the providers a claim file names
/// (provider-service). Both are called with this service's own identity for
/// the run's tenant, and each answer is reduced to a <see cref="CpcdsDropStep"/>.
/// </summary>
public interface ICpcdsUpstreamImports
{
    Task<CpcdsDropStep> ImportMembersAsync(
        string tenantId, CpcdsDropFile members, string membersContent,
        CpcdsDropFile? coverages, string? coveragesContent, CancellationToken ct);

    Task<CpcdsDropStep> ImportClaimProvidersAsync(
        string tenantId, CpcdsDropFile claims, string claimsContent,
        IReadOnlyDictionary<string, string>? columnMap, CancellationToken ct);

    /// <summary>
    /// An authorization file (authorization-service): an X12 278 interchange
    /// when the content starts with ISA, else the authorization CSV.
    /// </summary>
    Task<CpcdsDropStep> ImportAuthorizationsAsync(
        string tenantId, CpcdsDropFile file, string content, string? sourceSystem, string? lineOfBusiness, CancellationToken ct);
}

public sealed class HttpCpcdsUpstreamImports : ICpcdsUpstreamImports
{
    public const string EnrollmentClientName = "EnrollmentImportService";
    public const string ProviderClientName = "ProviderServiceCpcdsImports";
    public const string AuthorizationClientName = "AuthorizationServiceImports";

    private readonly IHttpClientFactory _http;

    public HttpCpcdsUpstreamImports(IHttpClientFactory http) => _http = http;

    public async Task<CpcdsDropStep> ImportMembersAsync(
        string tenantId, CpcdsDropFile members, string membersContent,
        CpcdsDropFile? coverages, string? coveragesContent, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(FileContent(membersContent), "members", members.Name);
        if (coverages is not null && coveragesContent is not null)
            form.Add(FileContent(coveragesContent), "coverages", coverages.Name);

        var step = new CpcdsDropStep
        {
            Name = "members-and-coverage",
            Files = coverages is null ? [members.Name] : [members.Name, coverages.Name],
        };
        await SendAsync(EnrollmentClientName, "api/v1/enrollment/import/cpcds", tenantId, form, step, json =>
        {
            Count(json, step, "memberRows", "coverageRows", "membersInFile");
            if (json.TryGetProperty("import", out var import) && import.ValueKind == JsonValueKind.Object)
                Count(import, step, "membersCreated", "membersUpdated", "dependentsCreated", "dependentsUpdated",
                    "coverageRecordsCreated", "coverageRecordsUpdated", "coverageMappingsUnresolved", "failedCount");
            if (json.TryGetProperty("planCodes", out var plans) && plans.ValueKind == JsonValueKind.Object
                && plans.TryGetProperty("unmapped", out var unmapped) && unmapped.ValueKind == JsonValueKind.Array)
                step.Counts["unmappedPlanCodes"] = unmapped.GetArrayLength();
            Errors(json, step, "fileErrors", "rowErrors");
            if (json.TryGetProperty("import", out var imp) && imp.ValueKind == JsonValueKind.Object)
                Errors(imp, step, "errors");
        }, ct);
        return step;
    }

    public async Task<CpcdsDropStep> ImportClaimProvidersAsync(
        string tenantId, CpcdsDropFile claims, string claimsContent,
        IReadOnlyDictionary<string, string>? columnMap, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(FileContent(claimsContent), "file", claims.Name);
        if (columnMap is { Count: > 0 })
            form.Add(new StringContent(JsonSerializer.Serialize(columnMap)), "columnMap");

        var step = new CpcdsDropStep { Name = "claim-providers", Files = [claims.Name] };
        await SendAsync(ProviderClientName, "api/v1/providers/import/cpcds-claims", tenantId, form, step, json =>
        {
            Count(json, step, "npisInFile", "alreadyKnown", "created", "notInNppes", "lookupFailed", "invalid");
            Errors(json, step, "fileErrors");
        }, ct);
        return step;
    }

    public async Task<CpcdsDropStep> ImportAuthorizationsAsync(
        string tenantId, CpcdsDropFile file, string content, string? sourceSystem, string? lineOfBusiness, CancellationToken ct)
    {
        var x12 = content.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("ISA", StringComparison.Ordinal);
        using var form = new MultipartFormDataContent();
        form.Add(FileContent(content), "file", file.Name);
        if (!string.IsNullOrWhiteSpace(sourceSystem))
            form.Add(new StringContent(sourceSystem), "sourceSystem");
        if (!string.IsNullOrWhiteSpace(lineOfBusiness))
            form.Add(new StringContent(lineOfBusiness), "lineOfBusiness");

        var step = new CpcdsDropStep { Name = x12 ? "authorizations-278" : "authorizations", Files = [file.Name] };
        await SendAsync(AuthorizationClientName, x12 ? "api/v1/authorizations/import/x12-278" : "api/v1/authorizations/import/csv",
            tenantId, form, step, json =>
            {
                Count(json, step, "rowsRead", "transactionSets", "authorizationsInFile", "created", "updated", "unchanged", "rejected");
                Errors(json, step, "fileErrors");
                if (json.TryGetProperty("authorizations", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in list.EnumerateArray())
                    {
                        if (step.Errors.Count >= MaxErrorsKept) break;
                        if (!item.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array
                            || errors.GetArrayLength() == 0) continue;
                        var number = item.TryGetProperty("authorizationNumber", out var n) ? n.GetString() : null;
                        step.Errors.Add($"{number}: {string.Join(" ", errors.EnumerateArray().Select(e => e.GetString()))}");
                    }
                }
            }, ct);
        return step;
    }

    private async Task SendAsync(
        string client, string path, string tenantId, HttpContent body, CpcdsDropStep step,
        Action<JsonElement> read, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body }.UseChoServiceIdentity();
            request.Headers.Add("X-Tenant-ID", tenantId);
            using var response = await _http.CreateClient(client).SendAsync(request, ct);
            step.HttpStatus = (int)response.StatusCode;
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(text);
                read(doc.RootElement);
            }
            step.Status = !response.IsSuccessStatusCode ? CpcdsDropStepStatus.Failed
                : step.Errors.Count > 0 ? CpcdsDropStepStatus.CompletedWithErrors
                : CpcdsDropStepStatus.Succeeded;
            if (!response.IsSuccessStatusCode && step.Errors.Count == 0)
                step.Errors.Add($"{path} answered {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            step.Status = CpcdsDropStepStatus.Failed;
            step.Errors.Add($"{path} could not be reached: {ex.GetType().Name}.");
        }
    }

    private static ByteArrayContent FileContent(string content)
    {
        var bytes = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(content));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        return bytes;
    }

    private static void Count(JsonElement json, CpcdsDropStep step, params string[] names)
    {
        foreach (var name in names)
        {
            if (json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                step.Counts[name] = value.GetDecimal();
        }
    }

    internal const int MaxErrorsKept = 20;

    private static void Errors(JsonElement json, CpcdsDropStep step, params string[] names)
    {
        foreach (var name in names)
        {
            if (!json.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in list.EnumerateArray())
            {
                if (step.Errors.Count >= MaxErrorsKept) return;
                if (item.ValueKind == JsonValueKind.String) step.Errors.Add(item.GetString()!);
            }
        }
    }
}
