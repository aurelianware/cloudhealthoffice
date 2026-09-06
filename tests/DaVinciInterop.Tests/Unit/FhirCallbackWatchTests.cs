using FluentAssertions;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// The callback listener is what turns "the payer did not call back" from an
/// assumption into an assertion, so it has to start reliably. A port chosen by
/// probing can be taken before the listener claims it; in CI that race would
/// surface as an intermittent scenario failure that looks like external
/// flakiness rather than a local collision.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class FhirCallbackWatchTests
{
    [Fact]
    public void A_watch_starts_and_advertises_a_fhir_base_a_container_can_reach()
    {
        using var watch = FhirCallbackWatch.Start();

        watch.BaseUrl.Should().StartWith("http://").And.EndWith("/fhir");
        watch.Requests.Should().BeEmpty("nothing has called back yet");
    }

    [Fact]
    public void Several_watches_can_run_at_once_without_colliding()
    {
        // Exercises the retry: concurrent starts are exactly when a probed port
        // can be claimed by the next caller before this one binds it.
        var watches = Enumerable.Range(0, 8).Select(_ => FhirCallbackWatch.Start()).ToList();

        try
        {
            watches.Select(w => w.BaseUrl).Distinct().Should().HaveCount(watches.Count,
                "each watch must hold its own port");
        }
        finally
        {
            foreach (var watch in watches)
            {
                watch.Dispose();
            }
        }
    }

    [Fact]
    public void An_explicitly_requested_port_that_is_taken_fails_rather_than_being_substituted()
    {
        using var occupying = FhirCallbackWatch.Start();
        var takenPort = int.Parse(new Uri(occupying.BaseUrl).Port.ToString());

        var act = () => FhirCallbackWatch.Start(takenPort);

        // Silently binding a different port would hand the scenario a listener at
        // an address it never advertised, and the callback assertion would then
        // pass for the wrong reason.
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Disposing_a_watch_releases_its_port_for_reuse()
    {
        var watch = FhirCallbackWatch.Start();
        var port = new Uri(watch.BaseUrl).Port;
        watch.Dispose();

        using var reused = FhirCallbackWatch.Start(port);

        reused.BaseUrl.Should().Contain(port.ToString());
    }

    [Fact]
    public void A_watch_records_a_callback_when_one_arrives()
    {
        using var watch = FhirCallbackWatch.Start();
        var port = new Uri(watch.BaseUrl).Port;
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        // The scenario asserts this list is empty; that assertion is only
        // meaningful if a real callback would actually show up in it.
        _ = http.GetAsync($"http://127.0.0.1:{port}/fhir/Patient/interop-member-001").GetAwaiter().GetResult();

        watch.Requests.Should().ContainSingle()
            .Which.Should().Be("GET /fhir/Patient/interop-member-001");
    }
}
