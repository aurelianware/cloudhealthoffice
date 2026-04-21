using System.Diagnostics;
using System.Net;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CloudHealthOffice.Infrastructure.Tests;

/// <summary>
/// Shared smoke-test helpers for the CHO observability contract. Per-service
/// test projects call <see cref="AssertStandardContract"/> instead of
/// duplicating the assertion logic.
///
/// Assertions:
/// 1. <c>GET /metrics</c> returns 200 and the Prometheus body contains the
///    expected CHO histogram name (proves AddChoObservability + Prometheus
///    exporter are wired end-to-end in the service pipeline).
/// 2. A span started via <see cref="ChoActivitySource.StartActivity"/> with a
///    raw memberId never carries the raw value — only the hashed form.
/// </summary>
public static class ObservabilityTestHelper
{
    /// <summary>
    /// Runs both smoke assertions against a service's test host.
    ///
    /// Ordering matters here: <c>CreateClient</c> is called first because that
    /// is what forces WebApplicationFactory to build the app, which in turn
    /// subscribes the OTel MeterProvider to the <c>CloudHealthOffice</c> meter.
    /// Any <c>ChoMetrics.RequestDuration.Record</c> call made *before* the
    /// subscription is dropped (no listener yet) and the sample never reaches
    /// the Prometheus exporter — a latent failure that only shows up under
    /// certain xUnit test-class orderings. The single sample we record after
    /// build ensures <c>/metrics</c> always has a cho_http_request_duration
    /// series to assert on, regardless of which tests ran before this one.
    /// The /metrics endpoint itself is filtered out of AspNetCore
    /// instrumentation, so it can't self-populate the counter.
    /// </summary>
    public static async Task AssertStandardContract<TProgram>(
        WebApplicationFactory<TProgram> factory)
        where TProgram : class
    {
        using var client = factory.CreateClient();

        ChoMetrics.RequestDuration.Record(
            0.001,
            new KeyValuePair<string, object?>("http.method", "GET"),
            new KeyValuePair<string, object?>("http.route", "/__smoke-test"),
            new KeyValuePair<string, object?>("http.status_code", 200));

        var response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "AddChoObservability + UseChoObservability must expose the Prometheus /metrics endpoint");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("cho_http_request_duration",
            "the CHO meter must be registered and rendered by the Prometheus exporter");

        AssertMemberIdIsHashed();
    }

    /// <summary>
    /// Asserts that ChoActivitySource hashes memberId rather than exporting it raw.
    /// Exposed separately for test projects without a WebApplicationFactory.
    /// </summary>
    public static void AssertMemberIdIsHashed()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ChoActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        const string rawMemberId = "raw-member-id";
        using var activity = ChoActivitySource.StartActivity(
            "test-span",
            memberId: rawMemberId);

        activity.Should().NotBeNull();
        activity!.TagObjects
            .Any(t => t.Value is string s && s.Contains(rawMemberId))
            .Should().BeFalse("raw member IDs must never appear on exported spans");
        activity.GetTagItem("cho.member_id_hash")
            .Should().Be(ChoActivitySource.HashIdentifier(rawMemberId));
    }
}
