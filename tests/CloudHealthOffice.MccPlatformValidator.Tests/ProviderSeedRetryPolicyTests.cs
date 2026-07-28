using System.Net;
using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class ProviderSeedRetryPolicyTests
{
    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void IsTransient_ReturnsTrueForRetryableStatus(HttpStatusCode statusCode)
    {
        Assert.True(ProviderSeedRetryPolicy.IsTransient(statusCode));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public void IsTransient_ReturnsFalseForNonRetryableStatus(HttpStatusCode statusCode)
    {
        Assert.False(ProviderSeedRetryPolicy.IsTransient(statusCode));
    }

    [Fact]
    public async Task ExecuteAsync_RetriesTransientFailuresUntilSuccess()
    {
        var attempts = 0;

        await ProviderSeedRetryPolicy.ExecuteAsync(
            () =>
            {
                attempts++;
                return attempts < 3
                    ? Task.FromException(new ProviderSeedRequestException(
                        "seed failed", HttpStatusCode.InternalServerError, "temporary"))
                    : Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryNonTransientFailures()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<ProviderSeedRequestException>(() =>
            ProviderSeedRetryPolicy.ExecuteAsync(
                () =>
                {
                    attempts++;
                    return Task.FromException(new ProviderSeedRequestException(
                        "seed failed", HttpStatusCode.BadRequest, "invalid"));
                },
                CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void RetryDelay_CapsExtendedOutageBackoff()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), ProviderSeedRetryPolicy.RetryDelay(10));
    }
}
