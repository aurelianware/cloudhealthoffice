using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// How far an external implementation has come up. These are ordered: a service
/// that reports <see cref="FhirMetadataAvailable"/> has already passed the earlier
/// stages. Reporting the furthest stage reached is what makes a startup failure
/// diagnosable — "container running, HTTP reachable, metadata never served" says
/// something very different from "container never started".
/// </summary>
public enum ReadinessStage
{
    /// <summary>Nothing observed yet.</summary>
    NotStarted,

    /// <summary>The container exists and is running.</summary>
    ContainerRunning,

    /// <summary>The port accepts connections and something answered HTTP.</summary>
    HttpReachable,

    /// <summary>GET {base}/metadata returned a parseable CapabilityStatement.</summary>
    FhirMetadataAvailable,

    /// <summary>Application-level readiness confirmed (e.g. CDS Hooks discovery answers).</summary>
    ApplicationReady,
}

/// <summary>The outcome of waiting for one external service to become usable.</summary>
public sealed record ReadinessOutcome(
    bool IsReady,
    ReadinessStage ReachedStage,
    TimeSpan Elapsed,
    int Attempts,
    string? Diagnostic,
    CapabilityStatement? CapabilityStatement);

/// <summary>
/// Bounded readiness polling for external implementations.
///
/// External reference implementations take far longer to start than a CHO unit
/// test — the pinned br-payer image installs several Da Vinci IG packages before
/// it serves its first request. The harness polls for an observable condition
/// with an explicit deadline rather than sleeping for a guessed duration, and it
/// distinguishes the stages above so a timeout says which one was never reached.
/// </summary>
public sealed class ReadinessProbe
{
    private readonly HttpClient _http;
    private readonly DockerCompose? _compose;

    public ReadinessProbe(HttpClient http, DockerCompose? compose = null)
    {
        _http = http;
        _compose = compose;
    }

    /// <summary>
    /// Polls until the requested stage is reached or the timeout expires.
    /// </summary>
    /// <param name="service">The external target being waited on.</param>
    /// <param name="requiredStage">The stage that counts as ready for this target.</param>
    /// <param name="timeout">Overall bound. Exceeding it is a failure with a diagnostic, never a hang.</param>
    /// <param name="pollInterval">Delay between attempts.</param>
    public async Task<ReadinessOutcome> WaitAsync(
        ExternalServiceDefinition service,
        ReadinessStage requiredStage,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var reached = ReadinessStage.NotStarted;
        var attempts = 0;
        string? lastDiagnostic = null;
        CapabilityStatement? capabilityStatement = null;

        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;

            try
            {
                var stage = await ProbeOnceAsync(service, requiredStage, cancellationToken);
                capabilityStatement = stage.CapabilityStatement ?? capabilityStatement;
                if (stage.Stage > reached)
                {
                    reached = stage.Stage;
                }

                lastDiagnostic = stage.Diagnostic;

                if (reached >= requiredStage)
                {
                    return new ReadinessOutcome(true, reached, stopwatch.Elapsed, attempts, null, capabilityStatement);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastDiagnostic = $"{ex.GetType().Name}: {ex.Message}";
            }

            try
            {
                await Task.Delay(pollInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        return new ReadinessOutcome(
            false,
            reached,
            stopwatch.Elapsed,
            attempts,
            $"'{service.Name}' did not reach {requiredStage} within {timeout.TotalSeconds:0}s " +
            $"(furthest stage: {reached}; {attempts} attempts). Last observation: {lastDiagnostic ?? "none"}.",
            capabilityStatement);
    }

    private sealed record ProbeResult(ReadinessStage Stage, string? Diagnostic, CapabilityStatement? CapabilityStatement);

    private async Task<ProbeResult> ProbeOnceAsync(
        ExternalServiceDefinition service,
        ReadinessStage requiredStage,
        CancellationToken cancellationToken)
    {
        // Stage 1 — the container is running at all.
        if (_compose is not null)
        {
            var running = await _compose.IsRunningAsync(
                service.Compose.Service, TimeSpan.FromSeconds(20), cancellationToken);
            if (!running)
            {
                return new ProbeResult(ReadinessStage.NotStarted,
                    $"compose service '{service.Compose.Service}' is not running", null);
            }
        }

        if (requiredStage == ReadinessStage.ContainerRunning)
        {
            return new ProbeResult(ReadinessStage.ContainerRunning, null, null);
        }

        var readinessUrl = service.Endpoints.ReadinessUrl
            ?? throw new InvalidOperationException(
                $"External target '{service.Key}' declares no readinessUrl in interop/versions.json.");

        // Stage 2 — something answers HTTP on the published port.
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(readinessUrl, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return new ProbeResult(ReadinessStage.ContainerRunning, $"HTTP not reachable yet: {ex.Message}", null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(ReadinessStage.ContainerRunning, "HTTP request timed out", null);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return new ProbeResult(ReadinessStage.HttpReachable,
                    $"readiness endpoint answered HTTP {(int)response.StatusCode}", null);
            }

            if (requiredStage == ReadinessStage.HttpReachable)
            {
                return new ProbeResult(ReadinessStage.HttpReachable, null, null);
            }

            // Stage 3 — the FHIR endpoint actually serves a parseable CapabilityStatement.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            CapabilityStatement statement;
            try
            {
                statement = new FhirJsonParser(new ParserSettings { AcceptUnknownMembers = true, PermissiveParsing = true })
                    .Parse<CapabilityStatement>(body);
            }
            catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
            {
                return new ProbeResult(ReadinessStage.HttpReachable,
                    $"readiness endpoint did not return a parseable CapabilityStatement: {ex.Message}", null);
            }

            if (requiredStage <= ReadinessStage.FhirMetadataAvailable)
            {
                return new ProbeResult(ReadinessStage.FhirMetadataAvailable, null, statement);
            }

            // Stage 4 — application-level readiness. For a CRD-capable target that
            // means CDS Hooks discovery answers with a service list, not just that
            // the FHIR servlet is up.
            var applicationReady = await IsApplicationReadyAsync(service, cancellationToken);
            return applicationReady.Ready
                ? new ProbeResult(ReadinessStage.ApplicationReady, null, statement)
                : new ProbeResult(ReadinessStage.FhirMetadataAvailable, applicationReady.Diagnostic, statement);
        }
    }

    private async Task<(bool Ready, string? Diagnostic)> IsApplicationReadyAsync(
        ExternalServiceDefinition service,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(service.Endpoints.CdsHooksBaseUrl))
        {
            // Nothing further to assert for this target: FHIR metadata is as ready as it gets.
            return (true, null);
        }

        try
        {
            var discovery = await _http.GetFromJsonAsync<CdsHooksDiscovery>(
                service.Endpoints.CdsHooksBaseUrl, cancellationToken);
            return discovery?.Services is { Count: > 0 }
                ? (true, null)
                : (false, "CDS Hooks discovery returned no services");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            return (false, $"CDS Hooks discovery not answering yet: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, "CDS Hooks discovery timed out");
        }
    }
}
