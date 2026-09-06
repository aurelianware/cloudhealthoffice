using System.Net;
using System.Text;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// A throwaway HTTP listener that stands in for the `fhirServer` a CDS Hooks
/// request advertises, and records anything the external service asks it for.
///
/// A CDS Hooks request must name a FHIR server the service may dereference for
/// data prefetch did not supply. Naming an address that does not exist would make
/// a scenario accidentally correct: it would pass only for as long as the payer
/// happened not to call back, and would start failing on an upstream change for
/// reasons no one could see. Pointing it here instead means the scenario can
/// assert what it actually depends on — that supplying the advertised prefetch
/// keys is sufficient, and no callback occurred.
///
/// If a scenario ever does need the payer to fetch data, this is the seam to
/// replace with the interop-cho profile, which runs CHO's own FHIR service.
/// </summary>
public sealed class FhirCallbackWatch : IDisposable
{
    private readonly HttpListener _listener;
    private readonly List<string> _requests = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;

    private FhirCallbackWatch(HttpListener listener, string baseUrl)
    {
        _listener = listener;
        BaseUrl = baseUrl;
        _loop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The FHIR base URL to advertise in a CDS Hooks request.</summary>
    public string BaseUrl { get; }

    /// <summary>Requests the external implementation made, as "METHOD path".</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    /// <summary>
    /// Starts a listener on a free loopback port, reachable from the external
    /// container through the Docker bridge gateway.
    /// </summary>
    public static FhirCallbackWatch Start(int port = 0)
    {
        var chosen = port == 0 ? FreePort() : port;
        var listener = new HttpListener();

        // Bind on all interfaces: the caller is a container reaching the host
        // across the bridge network, not a loopback client.
        listener.Prefixes.Add($"http://+:{chosen}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException)
        {
            // Restricted environments disallow wildcard prefixes; loopback still
            // proves the assertion for a host-network scenario.
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{chosen}/");
            listener.Start();
        }

        return new FhirCallbackWatch(listener, $"{DockerHostAddress()}:{chosen}/fhir");
    }

    /// <summary>
    /// The address a container uses to reach the test host. The interop stack adds
    /// a host-gateway alias, so this resolves from inside the external container.
    /// </summary>
    private static string DockerHostAddress() =>
        Environment.GetEnvironmentVariable("CHO_INTEROP_HOST_ADDRESS") ?? "http://host.docker.internal";

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            lock (_gate)
            {
                _requests.Add($"{context.Request.HttpMethod} {context.Request.Url?.AbsolutePath}");
            }

            try
            {
                // Answer as a FHIR server would for an unknown resource, so a payer
                // that does call back gets a well-formed answer rather than a
                // transport error that muddies the diagnosis.
                var body = Encoding.UTF8.GetBytes(
                    """{"resourceType":"OperationOutcome","issue":[{"severity":"error","code":"not-found"}]}""");
                context.Response.StatusCode = 404;
                context.Response.ContentType = "application/fhir+json";
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
            {
                // The caller hung up; the request is already recorded, which is all
                // the scenario needs.
            }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException)
        {
            // Already torn down.
        }

        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The accept loop exits by exception when the listener closes.
        }

        _shutdown.Dispose();
    }
}
