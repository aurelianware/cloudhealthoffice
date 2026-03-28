using System.Diagnostics.Metrics;
using CloudHealthOffice.Infrastructure.Observability;

namespace CloudHealthOffice.Infrastructure.Tests;

public class ChoMetricsTests
{
    [Fact]
    public void MeterName_IsCloudHealthOffice()
    {
        ChoMetrics.MeterName.Should().Be("CloudHealthOffice");
    }

    [Fact]
    public void RequestDuration_IsNotNull()
    {
        ChoMetrics.RequestDuration.Should().NotBeNull();
    }

    [Fact]
    public void RequestDuration_HasCorrectName()
    {
        ChoMetrics.RequestDuration.Name.Should().Be("cho.http.request.duration");
    }

    [Fact]
    public void RequestDuration_HasSecondsUnit()
    {
        ChoMetrics.RequestDuration.Unit.Should().Be("s");
    }

    [Fact]
    public void ClaimProcessingLatency_IsNotNull()
    {
        ChoMetrics.ClaimProcessingLatency.Should().NotBeNull();
    }

    [Fact]
    public void ClaimProcessingLatency_HasCorrectName()
    {
        ChoMetrics.ClaimProcessingLatency.Name.Should().Be("cho.claims.processing.duration");
    }

    [Fact]
    public void ClaimProcessingLatency_HasSecondsUnit()
    {
        ChoMetrics.ClaimProcessingLatency.Unit.Should().Be("s");
    }

    [Fact]
    public void EdiTransactionCount_IsNotNull()
    {
        ChoMetrics.EdiTransactionCount.Should().NotBeNull();
    }

    [Fact]
    public void EdiTransactionCount_HasCorrectName()
    {
        ChoMetrics.EdiTransactionCount.Name.Should().Be("cho.edi.transactions.total");
    }

    [Fact]
    public void AdjudicationOutcome_IsNotNull()
    {
        ChoMetrics.AdjudicationOutcome.Should().NotBeNull();
    }

    [Fact]
    public void AdjudicationOutcome_HasCorrectName()
    {
        ChoMetrics.AdjudicationOutcome.Name.Should().Be("cho.claims.adjudication.outcome.total");
    }

    [Fact]
    public void Metrics_CanRecordValues_WithoutError()
    {
        // Verify that recording values doesn't throw (even without a listener)
        var act = () =>
        {
            ChoMetrics.RequestDuration.Record(0.5);
            ChoMetrics.ClaimProcessingLatency.Record(1.2);
            ChoMetrics.EdiTransactionCount.Add(1);
            ChoMetrics.AdjudicationOutcome.Add(1);
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void Metrics_CanRecordWithTags_WithoutError()
    {
        var act = () =>
        {
            ChoMetrics.RequestDuration.Record(0.3,
                new KeyValuePair<string, object?>("http.method", "GET"),
                new KeyValuePair<string, object?>("http.route", "/api/claims"),
                new KeyValuePair<string, object?>("http.status_code", 200));

            ChoMetrics.EdiTransactionCount.Add(1,
                new KeyValuePair<string, object?>("cho.edi_transaction_type", "837"));

            ChoMetrics.AdjudicationOutcome.Add(1,
                new KeyValuePair<string, object?>("cho.outcome", "approved"));
        };

        act.Should().NotThrow();
    }
}
