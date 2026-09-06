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
    /// Starts a listener on a free port, reachable from the external container
    /// through the Docker bridge gateway.
    /// </summary>
    /// <param name="port">
    /// A specific port to bind, or 0 to choose one. With 0 the choice is retried:
    /// a port observed to be free can be taken by another process before the
    /// listener claims it, and in CI that race would surface as an intermittent
    /// scenario failure looking like external flakiness rather than a local
    /// collision.
    /// </param>
    public static FhirCallbackWatch Start(int port = 0)
    {
        const int attempts = 5;
        HttpListenerException? lastFailure = null;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var chosen = port == 0 ? FreePort() : port;
            try
            {
                return new FhirCallbackWatch(Listen(chosen), $"{DockerHostAddress()}:{chosen}/fhir");
            }
            catch (HttpListenerException ex)
            {
                lastFailure = ex;
                if (port != 0)
                {
                    // An explicitly requested port cannot be substituted: the
                    // caller asked for that one.
                    throw;
                }
            }
        }

        throw new InvalidOperationException(
            $"Could not bind a callback listener after {attempts} attempts at auto-selected ports. " +
            "The scenario needs one to assert that the external implementation did not call back.",
            lastFailure);
    }

    /// <summary>
    /// Binds one port, preferring a wildcard prefix so a container reaching the
    /// host across the bridge network is served.
    /// </summary>
    private static HttpListener Listen(int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{port}/");
        try
        {
            listener.Start();
            return listener;
        }
        catch (HttpListenerException) when (WildcardPrefixesAreRestricted())
        {
            // Some environments reserve wildcard prefixes for elevated processes.
            // Loopback still proves the assertion for a host-network scenario, and
            // is retried on the same port because that failure was about the
            // prefix, not about the port being taken. Where wildcards are not
            // restricted, a bind failure means the port is gone and the caller
            // picks a new one instead.
            listener.Close();
            var loopback = new HttpListener();
            loopback.Prefixes.Add($"http://127.0.0.1:{port}/");
            loopback.Start();
            return loopback;
        }
    }

    /// <summary>
    /// Whether wildcard prefixes need elevation here. Only Windows restricts them.
    /// </summary>
    private static bool WildcardPrefixesAreRestricted() =>
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows);

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
