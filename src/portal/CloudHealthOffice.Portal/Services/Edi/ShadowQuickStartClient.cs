using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// The portal's quick start for shadow adjudication: a plan uploads a sample
/// of what its core already decided (a month of CPCDS claims, and optionally
/// the members and coverage and the authorizations behind them), and the page
/// loads them through the same importers the file drop uses and starts a
/// shadow run on the claims. Each call carries the signed-in user's token, so
/// each service enforces its own import permission and the tenant.
/// </summary>
public interface IShadowQuickStartClient
{
    /// <summary>CPCDS members (and coverage) through enrollment-import-service.</summary>
    Task<ShadowQuickStartStep> ImportMembersAsync(
        ShadowQuickStartFile members, ShadowQuickStartFile? coverages, CancellationToken ct = default);

    /// <summary>A UM platform's authorizations (CSV, or X12 278) through authorization-service.</summary>
    Task<ShadowQuickStartStep> ImportAuthorizationsAsync(
        ShadowQuickStartFile file, string lineOfBusiness, string? sourceSystem, CancellationToken ct = default);

    /// <summary>A CPCDS claim file through claims-service; the step carries the import batch id to shadow-run.</summary>
    Task<ShadowQuickStartStep> ImportClaimsAsync(
        ShadowQuickStartFile file, string lineOfBusiness, string? sourceSystem, CancellationToken ct = default);
}

/// <summary>An uploaded file, as a stream the caller owns.</summary>
public sealed record ShadowQuickStartFile(string Name, Stream Content);

/// <summary>What one load did, in a line a plan reads.</summary>
public sealed class ShadowQuickStartStep
{
    public string Label { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public string Summary { get; init; } = string.Empty;

    /// <summary>The claims import batch, for the shadow run. Null for other steps.</summary>
    public string? ImportBatchId { get; init; }

    /// <summary>Up to five reasons rows or the file were refused.</summary>
    public List<string> Problems { get; init; } = [];
}

public class ShadowQuickStartClient : IShadowQuickStartClient
{
    private const int MaxProblems = 5;
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ShadowQuickStartClient> _logger;

    public ShadowQuickStartClient(HttpClient httpClient, IConfiguration configuration, ILogger<ShadowQuickStartClient> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ShadowQuickStartStep> ImportMembersAsync(
        ShadowQuickStartFile members, ShadowQuickStartFile? coverages, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        Add(form, "members", members);
        if (coverages is not null) Add(form, "coverages", coverages);
        form.Add(new StringContent("false"), "dryRun");

        var url = $"{Base("Services:EnrollmentImportService")}/v1/enrollment/import/cpcds";
        return await PostAsync("Members and coverage", "Enrollment Import Service", url, form, root =>
        {
            var summary = $"{Int(root, "membersInFile")} members, {Int(root, "coverageRows")} coverage rows";
            if (root.TryGetProperty("import", out var import) && import.ValueKind == JsonValueKind.Object)
                summary += $"; {Int(import, "membersCreated")} created, {Int(import, "membersUpdated")} updated, {Int(import, "failedCount")} failed";
            return (summary, null, Errors(root, "errors"));
        }, ct);
    }

    public async Task<ShadowQuickStartStep> ImportAuthorizationsAsync(
        ShadowQuickStartFile file, string lineOfBusiness, string? sourceSystem, CancellationToken ct = default)
    {
        var endpoint = await LooksLikeX12Async(file) ? "x12-278" : "csv";
        using var form = new MultipartFormDataContent();
        Add(form, "file", file);
        form.Add(new StringContent("false"), "dryRun");
        form.Add(new StringContent(lineOfBusiness), "lineOfBusiness");
        if (!string.IsNullOrWhiteSpace(sourceSystem)) form.Add(new StringContent(sourceSystem.Trim()), "sourceSystem");

        var url = $"{Base("Services:AuthorizationService")}/v1/authorizations/import/{endpoint}";
        return await PostAsync("Authorizations", "Authorization Service", url, form, root =>
            ($"{Int(root, "authorizationsInFile")} authorizations: {Int(root, "created")} created, {Int(root, "updated")} updated, {Int(root, "unchanged")} unchanged, {Int(root, "rejected")} refused",
             null,
             RowErrors(root, "authorizations", "authorizationNumber").Concat(Errors(root, "fileErrors")).Take(MaxProblems).ToList()), ct);
    }

    public async Task<ShadowQuickStartStep> ImportClaimsAsync(
        ShadowQuickStartFile file, string lineOfBusiness, string? sourceSystem, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        Add(form, "file", file);
        form.Add(new StringContent("false"), "dryRun");
        form.Add(new StringContent(lineOfBusiness), "lineOfBusiness");
        if (!string.IsNullOrWhiteSpace(sourceSystem)) form.Add(new StringContent(sourceSystem.Trim()), "sourceSystem");

        var url = $"{Base("Services:ClaimsService")}/v1/claims/import/cpcds";
        return await PostAsync("Claims", "Claims Service", url, form, root =>
        {
            var loaded = Int(root, "created") + Int(root, "updated") + Int(root, "unchanged");
            var paid = root.TryGetProperty("paidAmountLoaded", out var p) && p.TryGetDecimal(out var amount) ? amount : 0m;
            var summary = $"{Int(root, "claimsInFile")} claims: {loaded} loaded ({paid.ToString("C0", Us)} core paid), {Int(root, "rejected")} refused";
            var batch = loaded > 0 && root.TryGetProperty("batchId", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            return (summary, batch,
                Errors(root, "fileErrors").Concat(RowErrors(root, "claims", "claimId")).Take(MaxProblems).ToList());
        }, ct);
    }

    private async Task<ShadowQuickStartStep> PostAsync(
        string label, string serviceName, string url, MultipartFormDataContent form,
        Func<JsonElement, (string Summary, string? BatchId, List<string> Problems)> read, CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.PostAsync(url, form, ct);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                return Failed(label, $"You don't have permission to load {label.ToLowerInvariant()}.");

            var body = await response.Content.ReadAsStringAsync(ct);
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return Failed(label, $"{serviceName} answered {(int)response.StatusCode}.");
            }

            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.UnprocessableEntity)
            {
                var (summary, batch, problems) = read(root);
                return new ShadowQuickStartStep
                {
                    Label = label,
                    Succeeded = response.StatusCode == HttpStatusCode.OK,
                    Summary = summary,
                    ImportBatchId = batch,
                    Problems = problems,
                };
            }

            var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString()!
                : $"{serviceName} answered {(int)response.StatusCode}.";
            return Failed(label, error);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Service unavailable: {ServiceName}", serviceName);
            return Failed(label, $"{serviceName} could not be reached.");
        }
    }

    private string Base(string key) => (_configuration[key] ?? string.Empty).TrimEnd('/');

    private static void Add(MultipartFormDataContent form, string field, ShadowQuickStartFile file)
    {
        var content = new StreamContent(file.Content);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(content, field, file.Name);
    }

    /// <summary>An X12 interchange starts with ISA (after any byte order mark or white space).</summary>
    internal static async Task<bool> LooksLikeX12Async(ShadowQuickStartFile file)
    {
        if (!file.Content.CanSeek) return file.Name.Contains("278", StringComparison.Ordinal);
        var buffer = new byte[512];
        var read = await file.Content.ReadAsync(buffer);
        file.Content.Position = 0;
        var head = System.Text.Encoding.UTF8.GetString(buffer, 0, read).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return head.StartsWith("ISA", StringComparison.Ordinal);
    }

    private static ShadowQuickStartStep Failed(string label, string error) =>
        new() { Label = label, Succeeded = false, Summary = error };

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static List<string> Errors(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(MaxProblems).ToList()
            : [];

    private static IEnumerable<string> RowErrors(JsonElement root, string listName, string idName)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(listName, out var rows) || rows.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0)
                continue;
            var id = row.TryGetProperty(idName, out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : "?";
            yield return $"{id}: {string.Join("; ", errors.EnumerateArray().Select(x => x.ToString()))}";
        }
    }
}
