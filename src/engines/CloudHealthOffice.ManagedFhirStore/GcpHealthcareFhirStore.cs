using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.ManagedFhirStore;

/// <summary>Where a Google Cloud Healthcare API FHIR store lives.</summary>
public sealed class GcpHealthcareFhirStoreOptions
{
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>Healthcare API location, e.g. <c>us-central1</c>.</summary>
    public string Location { get; set; } = string.Empty;

    public string DatasetId { get; set; } = string.Empty;

    public string FhirStoreId { get; set; } = string.Empty;

    /// <summary>API root; overridden only by tests.</summary>
    public string ApiRoot { get; set; } = "https://healthcare.googleapis.com/v1";

    internal string StoreName =>
        $"projects/{ProjectId}/locations/{Location}/datasets/{DatasetId}/fhirStores/{FhirStoreId}";

    internal string FhirBase => $"{ApiRoot.TrimEnd('/')}/{StoreName}/fhir";

    internal void Validate()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(ProjectId)) missing.Add("ProjectId");
        if (string.IsNullOrWhiteSpace(Location)) missing.Add("Location");
        if (string.IsNullOrWhiteSpace(DatasetId)) missing.Add("DatasetId");
        if (string.IsNullOrWhiteSpace(FhirStoreId)) missing.Add("FhirStoreId");
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"ManagedFhirStore:Provider is GcpHealthcare but ManagedFhirStore:Gcp is missing {string.Join(", ", missing)}.");
        }
    }
}

/// <summary>
/// <see cref="IManagedFhirStore"/> backed by a Google Cloud Healthcare API
/// FHIR store.
///
/// The store is created by Terraform (<c>infrastructure/gcp/fhir-store.tf</c>),
/// never by this class. It must allow update-as-create
/// (<c>enable_update_create</c>) because CHO assigns resource ids. Changes
/// stream from the store to BigQuery and Pub/Sub by store configuration, so
/// nothing here talks to either.
///
/// Credentials come from Application Default Credentials. On GKE that is
/// Workload Identity: the service account needs
/// <c>roles/healthcare.fhirResourceEditor</c> on the store, and
/// <c>roles/healthcare.fhirStoreAdmin</c> to start exports.
/// </summary>
public sealed class GcpHealthcareFhirStore : IManagedFhirStore
{
    private const string FhirJson = "application/fhir+json";

    private readonly HttpClient _http;
    private readonly IGoogleAccessTokenSource _tokens;
    private readonly GcpHealthcareFhirStoreOptions _options;
    private readonly ILogger<GcpHealthcareFhirStore> _logger;

    public GcpHealthcareFhirStore(
        HttpClient http,
        IGoogleAccessTokenSource tokens,
        GcpHealthcareFhirStoreOptions options,
        ILogger<GcpHealthcareFhirStore> logger)
    {
        options.Validate();
        _http = http;
        _tokens = tokens;
        _options = options;
        _logger = logger;
    }

    public string ProviderName => "gcp-healthcare";

    public async Task<ManagedFhirWriteResult> UpsertAsync(
        string resourceType, string id, string resourceJson, CancellationToken ct = default)
    {
        ValidateResource(resourceType, id, resourceJson);

        using var request = await CreateRequestAsync(HttpMethod.Put, ResourceUrl(resourceType, id), ct);
        request.Content = new StringContent(resourceJson, Encoding.UTF8, FhirJson);
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var status = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
        {
            return new ManagedFhirWriteResult(status, ReadVersionId(response, body), null);
        }

        // 400/412/422: the store rejected this resource (profile validation,
        // a broken reference). That is the resource's problem, not the
        // store's, so it is returned rather than thrown.
        if (status is 400 or 412 or 422)
        {
            return new ManagedFhirWriteResult(status, null, AsOperationOutcome(body));
        }

        throw Failure(response, body, $"PUT {resourceType}/{id}");
    }

    public async Task<string?> ReadAsync(string resourceType, string id, CancellationToken ct = default)
    {
        ValidateKey(resourceType, id);

        using var request = await CreateRequestAsync(HttpMethod.Get, ResourceUrl(resourceType, id), ct);
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw Failure(response, body, $"GET {resourceType}/{id}");
        }
        return body;
    }

    public async Task DeleteAsync(string resourceType, string id, CancellationToken ct = default)
    {
        ValidateKey(resourceType, id);

        using var request = await CreateRequestAsync(HttpMethod.Delete, ResourceUrl(resourceType, id), ct);
        using var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }
        throw Failure(response, await response.Content.ReadAsStringAsync(ct), $"DELETE {resourceType}/{id}");
    }

    public async Task<ManagedFhirExportJob> StartExportAsync(ManagedFhirExportRequest exportRequest, CancellationToken ct = default)
    {
        if (!exportRequest.DestinationUri.StartsWith("gs://", StringComparison.Ordinal))
        {
            throw new ArgumentException("A Cloud Healthcare API export destination must be a gs:// URI.", nameof(exportRequest));
        }

        var payload = new JsonObject
        {
            ["gcsDestination"] = new JsonObject { ["uriPrefix"] = exportRequest.DestinationUri }
        };
        if (exportRequest.ResourceTypes is { Count: > 0 } types)
        {
            foreach (var type in types) ValidateResourceType(type);
            payload["_type"] = string.Join(",", types);
        }
        if (exportRequest.Since is { } since)
        {
            payload["_since"] = since.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        }

        using var request = await CreateRequestAsync(
            HttpMethod.Post, $"{_options.ApiRoot.TrimEnd('/')}/{_options.StoreName}:export", ct);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw Failure(response, body, "export");
        }

        // A long-running operation; its name is the job id.
        var operationName = JsonNode.Parse(body)?["name"]?.GetValue<string>()
            ?? throw new ManagedFhirStoreException("Export started but no operation name was returned.", (int)response.StatusCode);
        return new ManagedFhirExportJob(operationName, ManagedFhirExportState.Running);
    }

    public async Task<ManagedFhirExportJob> GetExportStatusAsync(string jobId, CancellationToken ct = default)
    {
        // Only follow operations that belong to this dataset, so a job id
        // from a caller cannot steer the request elsewhere.
        var datasetPrefix = $"projects/{_options.ProjectId}/locations/{_options.Location}/datasets/{_options.DatasetId}/operations/";
        if (!jobId.StartsWith(datasetPrefix, StringComparison.Ordinal) || jobId.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Export job id does not belong to this FHIR store's dataset.", nameof(jobId));
        }

        using var request = await CreateRequestAsync(HttpMethod.Get, $"{_options.ApiRoot.TrimEnd('/')}/{jobId}", ct);
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw Failure(response, body, "export status");
        }

        var operation = JsonNode.Parse(body)!;
        if (operation["done"]?.GetValue<bool>() != true)
        {
            return new ManagedFhirExportJob(jobId, ManagedFhirExportState.Running);
        }
        if (operation["error"] is JsonNode error)
        {
            return new ManagedFhirExportJob(jobId, ManagedFhirExportState.Failed, error["message"]?.GetValue<string>());
        }

        long? count = null;
        var success = operation["metadata"]?["counter"]?["success"];
        if (success is not null && long.TryParse(success.ToString(), out var parsed))
        {
            count = parsed;
        }
        return new ManagedFhirExportJob(jobId, ManagedFhirExportState.Succeeded, ResourceCount: count);
    }

    private string ResourceUrl(string resourceType, string id) =>
        $"{_options.FhirBase}/{resourceType}/{Uri.EscapeDataString(id)}";

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string url, CancellationToken ct)
    {
        var token = await _tokens.GetAccessTokenAsync(ct);
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(FhirJson));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private ManagedFhirStoreException Failure(HttpResponseMessage response, string body, string operation)
    {
        var status = (int)response.StatusCode;
        _logger.LogWarning("Cloud Healthcare API {Operation} failed with {StatusCode}", operation, status);
        return new ManagedFhirStoreException(
            $"Cloud Healthcare API {operation} returned {status} {response.ReasonPhrase}.",
            status,
            AsOperationOutcome(body));
    }

    private static string? ReadVersionId(HttpResponseMessage response, string body)
    {
        try
        {
            if (JsonNode.Parse(body)?["meta"]?["versionId"]?.GetValue<string>() is { } versionId)
            {
                return versionId;
            }
        }
        catch (JsonException)
        {
            // Fall through to the ETag.
        }
        return response.Headers.ETag?.Tag.Trim('"');
    }

    private static string? AsOperationOutcome(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["resourceType"]?.GetValue<string>() == "OperationOutcome" ? body : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ValidateResource(string resourceType, string id, string resourceJson)
    {
        ValidateKey(resourceType, id);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(resourceJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Resource is not valid JSON.", nameof(resourceJson), ex);
        }
        if (node?["resourceType"]?.GetValue<string>() != resourceType)
        {
            throw new ArgumentException($"resourceType in the body must be {resourceType}.", nameof(resourceJson));
        }
        if (node["id"]?.GetValue<string>() != id)
        {
            throw new ArgumentException("id in the body must match the id being written.", nameof(resourceJson));
        }
    }

    internal static void ValidateKey(string resourceType, string id)
    {
        ValidateResourceType(resourceType);
        // FHIR id: [A-Za-z0-9\-\.]{1,64}
        if (string.IsNullOrEmpty(id) || id.Length > 64
            || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
        {
            throw new ArgumentException($"'{id}' is not a valid FHIR id.", nameof(id));
        }
    }

    private static void ValidateResourceType(string resourceType)
    {
        if (string.IsNullOrEmpty(resourceType) || !char.IsAsciiLetterUpper(resourceType[0])
            || !resourceType.All(char.IsAsciiLetter))
        {
            throw new ArgumentException($"'{resourceType}' is not a FHIR resource type.", nameof(resourceType));
        }
    }
}
